using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Central.Domain;

namespace Central.Operations;

public sealed partial class YoutubeDesk(Store store, Distribution distribution, GoogleAuthorization google, WindowsVault vault) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly ConcurrentDictionary<string, byte> busy = new();
    private sealed record Policy(string Category, bool MadeForKids, string Subject);
    private sealed record UploadStatus(long Offset, string? Id);
    private const int Chunk = 4 * 1024 * 1024;

    public async Task Upload(string work, string id, string? account, string? category, string? audience, bool authorized, CancellationToken token)
    {
        if (!busy.TryAdd(id, 0)) throw new DomainError("O upload desta entrega já está em execução.");
        try
        {
            var item = distribution.Get(work, id);
            if (item.Visibility != "private" || !Channel().IsMatch(item.Channel))
                throw new DomainError("O ensaio do YouTube exige visibilidade privada e o ID exato do canal.");
            if (Held()) throw new DomainError("Após restaurar a base, reconcilie os efeitos externos antes de enviar.");
            if (item.Remote != null) throw new DomainError("O vídeo já tem identificador remoto. Consulte as métricas.");
            var profile = google.Get(item.Account ?? Store.Text(account, 64, "um perfil YouTube"));
            if (profile.Product != "youtube" || profile.Subject == null || !google.Connected(profile) ||
                item.Account != null && account != null && account.Length > 0 && account != item.Account)
                throw new DomainError("A conta precisa coincidir com o perfil autorizado desta entrega.");
            var media = store.GetAsset(item.Project, item.Media);
            var path = store.Verify(media);
            var key = "youtube-upload:" + item.Id;
            var session = vault.Get<string>(key);
            Policy policy;
            if (item.State == "aprovada_local")
            {
                if (!authorized || category == null || category.Length is < 1 or > 3 || category.Any(c => !char.IsAsciiDigit(c)) ||
                    audience is not ("sim" or "nao"))
                    throw new DomainError("Confirme envio privado, categoria e público infantil para este vídeo.");
                policy = new Policy(category, audience == "sim", profile.Subject);
            }
            else
            {
                if (session == null) throw new DomainError("A autorização original não devolveu uma sessão recuperável. Confira o canal antes de qualquer novo envio.");
                var raw = store.Read(db => db.Scalar<string>("SELECT remote_policy FROM deliveries WHERE id=$i", id));
                policy = raw == null ? throw new DomainError("Política de envio original ausente.")
                    : JsonSerializer.Deserialize<Policy>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                if (policy.Subject != profile.Subject) throw new DomainError("O titular mudou desde a autorização de envio.");
            }
            var bearer = await google.Access(profile, token);
            await CheckChannel(item.Channel, bearer, token);
            if (session == null)
            {
                store.Write(db =>
                {
                    if (db.Exec("UPDATE deliveries SET state='enviando',remote_account=$a,remote_policy=$p,updated=$n WHERE id=$i AND state='aprovada_local'",
                        profile.Id, Store.Json(policy), store.Now, item.Id) != 1)
                        throw new DomainError("O estado desta entrega mudou.");
                });
                try
                {
                    session = await Start(item, policy, media.Size, bearer, token);
                    vault.Put(key, session);
                }
                catch
                {
                    State(item.Id, "conciliar_sem_sessao");
                    throw;
                }
            }
            ValidateSession(session);
            try
            {
                var status = await Transfer(session, bearer, media.Size, null, 0, token);
                await using var input = File.OpenRead(path);
                while (status.Id == null)
                {
                    if (status.Offset >= media.Size) throw new DomainError("O YouTube ainda não confirmou o vídeo completo.");
                    input.Position = status.Offset;
                    var buffer = new byte[(int)Math.Min(Chunk, media.Size - status.Offset)];
                    await input.ReadExactlyAsync(buffer, token);
                    var updated = await Transfer(session, bearer, media.Size, buffer, status.Offset, token);
                    if (updated.Id == null && updated.Offset <= status.Offset) throw new DomainError("O YouTube ainda não confirmou este trecho. Consulte a sessão novamente.");
                    status = updated;
                }
                await CheckVideo(status.Id, item.Channel, bearer, token);
                store.Write(db => db.Exec("UPDATE deliveries SET state='privada_confirmada',remote_id=$r,updated=$n WHERE id=$i",
                    status.Id, store.Now, item.Id));
                vault.Remove(key);
            }
            catch
            {
                State(item.Id, "consulta_pendente");
                throw;
            }
        }
        finally { busy.TryRemove(id, out _); }
    }

    public async Task Metrics(string work, string id, CancellationToken token)
    {
        var item = distribution.Get(work, id);
        if (item.Remote == null || item.Account == null) throw new DomainError("O vídeo ainda não tem um ID confirmado.");
        var profile = google.Get(item.Account);
        var bearer = await google.Access(profile, token);
        await CheckChannel(item.Channel, bearer, token);
        var query = GoogleAuthorization.Query("https://youtubeanalytics.googleapis.com/v2/reports", new()
        {
            ["ids"] = "channel==MINE", ["metrics"] = "views", ["filters"] = "video==" + item.Remote,
            ["startDate"] = store.Clock.GetUtcNow().AddDays(-29).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = store.Clock.GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });
        using var result = await Get(query, bearer, token);
        var rows = result.RootElement.TryGetProperty("rows", out var values) ? values : default;
        long? count = rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0][0].GetInt64() : null;
        store.Write(db => db.Exec("UPDATE deliveries SET views=$v,updated_by='youtube-analytics-30d',updated=$n WHERE id=$i", count, store.Now, id));
    }

    private async Task<string> Start(Delivery item, Policy policy, long size, string bearer, CancellationToken token)
    {
        using var request = GoogleAuthorization.Authorized(HttpMethod.Post,
            "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status", bearer);
        request.Headers.Add("X-Upload-Content-Type", "video/mp4");
        request.Headers.Add("X-Upload-Content-Length", size.ToString(CultureInfo.InvariantCulture));
        request.Content = JsonContent.Create(new { snippet = new { title = item.Title, description = item.Description, categoryId = policy.Category },
            status = new { privacyStatus = "private", selfDeclaredMadeForKids = policy.MadeForKids } });
        using var result = await client.SendAsync(request, token);
        if (!result.IsSuccessStatusCode || result.Headers.Location is not { IsAbsoluteUri: true } location)
            throw new DomainError("O YouTube não devolveu uma sessão de upload confirmada.");
        ValidateSession(location.AbsoluteUri);
        return location.AbsoluteUri;
    }

    private async Task<UploadStatus> Transfer(string session, string bearer, long size, byte[]? bytes, long offset, CancellationToken token)
    {
        using var request = GoogleAuthorization.Authorized(HttpMethod.Put, session, bearer);
        request.Content = new ByteArrayContent(bytes ?? []);
        request.Content.Headers.ContentRange = bytes == null ? new ContentRangeHeaderValue(size) :
            new ContentRangeHeaderValue(offset, offset + bytes.Length - 1, size);
        if (bytes != null) request.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        using var response = await client.SendAsync(request, token);
        if ((int)response.StatusCode == 308)
        {
            var range = response.Headers.TryGetValues("Range", out var values) ? values.FirstOrDefault() : null;
            return new UploadStatus(NextOffset(range, size), null);
        }
        if ((int)response.StatusCode == 404) throw new DomainError("A sessão não está disponível. Confira a publicação original no canal antes de criar outra entrega.");
        using var result = await GoogleAuthorization.Read(response, "sessão original do YouTube", token);
        var video = result.RootElement.GetProperty("id").GetString();
        if (video == null || !Video().IsMatch(video)) throw new DomainError("O YouTube não confirmou o identificador do vídeo.");
        return new UploadStatus(size, video);
    }

    public static long NextOffset(string? range, long size)
    {
        if (range == null) return 0;
        if (!range.StartsWith("bytes=0-", StringComparison.Ordinal) ||
            !long.TryParse(range.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var last) || last >= size)
            throw new DomainError("A faixa recebida da sessão é inválida.");
        return last + 1;
    }

    private async Task CheckChannel(string expected, string bearer, CancellationToken token)
    {
        using var result = await Get("https://www.googleapis.com/youtube/v3/channels?mine=true&part=id&maxResults=50", bearer, token);
        var channels = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (channels.Length != 1 || channels[0].GetProperty("id").GetString() != expected)
            throw new DomainError("O canal verificado com esta autorização diverge do destino fixado.");
    }

    private async Task CheckVideo(string id, string expectedChannel, string bearer, CancellationToken token)
    {
        using var result = await Get("https://www.googleapis.com/youtube/v3/videos?part=snippet,status&id=" + id, bearer, token);
        var videos = result.RootElement.GetProperty("items").EnumerateArray().ToArray();
        if (videos.Length != 1 || videos[0].GetProperty("snippet").GetProperty("channelId").GetString() != expectedChannel ||
            videos[0].GetProperty("status").GetProperty("privacyStatus").GetString() != "private")
            throw new DomainError("A plataforma ainda não confirmou canal e visibilidade privados.");
    }

    private async Task<JsonDocument> Get(string uri, string bearer, CancellationToken token)
    {
        using var request = GoogleAuthorization.Authorized(HttpMethod.Get, uri, bearer);
        using var response = await client.SendAsync(request, token);
        return await GoogleAuthorization.Read(response, "leitura do YouTube", token);
    }

    private static void ValidateSession(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Host is not ("www.googleapis.com" or "upload.youtube.com" or "youtube.googleapis.com") ||
            !uri.AbsolutePath.StartsWith("/upload/youtube/v3/videos", StringComparison.Ordinal))
            throw new DomainError("Endereço inesperado para a sessão de upload.");
    }
    private bool Held() => store.Read(db => db.Scalar<long>("SELECT COUNT(*) FROM service_flags WHERE key='restore_hold'") != 0);
    private void State(string id, string state) => store.Write(db => db.Exec("UPDATE deliveries SET state=$s,updated=$n WHERE id=$i", state, store.Now, id));
    [GeneratedRegex(@"^UC[A-Za-z0-9_-]{22}$")] private static partial Regex Channel();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")] private static partial Regex Video();
    public void Dispose() => client.Dispose();
}
