using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Central.Domain;
using Microsoft.Data.Sqlite;

namespace Central.Operations;

public sealed record GoogleProfile(string Id, string Product, string Label, string Cloud, string Expected,
    string? Identity, string? Subject, string? Verified);

public sealed class GoogleAuthorization(Store store, WindowsVault vault) : IDisposable
{
    private const string Columns = "id,product,label,cloud,expected,identity,subject,verified";
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(50) };
    private sealed record Config(string ClientId, string Secret);
    private sealed record Grant(string Refresh, string Subject);
    private sealed record Pending(GoogleProfile Profile, string Verifier, string Redirect, DateTimeOffset Expires);

    public IReadOnlyList<GoogleProfile> List() => store.Read(db => db.List($"SELECT {Columns} FROM google_profiles ORDER BY label", Map));
    public GoogleProfile Get(string id) => store.Read(db => db.One($"SELECT {Columns} FROM google_profiles WHERE id=$i", Map,id))
        ?? throw new DomainError("Perfil Google não encontrado.");
    public bool Connected(GoogleProfile profile) => profile.Subject != null && vault.Get<Grant>("google-grant:" + profile.Id) is { } grant
        && grant.Subject == profile.Subject;

    public string Configure(string product, string label, string cloud, string expected, string clientId, string clientSecret)
    {
        if (product is not ("notebook" or "youtube")) throw new DomainError("Produto Google inválido.");
        label = Store.Text(label, 100, "o nome do perfil");
        cloud = Store.Text(cloud, 63, "o projeto Cloud");
        if (cloud.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') || cloud.Length < 5)
            throw new DomainError("Informe o ID do projeto Google Cloud.");
        expected = Store.Text(expected, 254, "a conta Google esperada");
        if (!expected.Contains('@') || expected.Any(char.IsWhiteSpace)) throw new DomainError("Informe o e-mail da conta esperada.");
        clientId = Store.Text(clientId, 500, "o ID do cliente desktop");
        clientSecret = Store.Text(clientSecret, 500, "o segredo do cliente desktop");
        if (!clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || clientId.Any(char.IsWhiteSpace))
            throw new DomainError("Confira o ID do cliente OAuth desktop.");
        var id = Store.Id();
        vault.Put("google-client:" + id, new Config(clientId, clientSecret));
        store.Write(db => db.Exec("INSERT INTO google_profiles(id,product,label,cloud,expected) VALUES($i,$p,$l,$c,$e)",
            id, product, label, cloud, expected));
        return id;
    }

    public string Start(string id, int port)
    {
        var profile = Get(id);
        var config = Client(id);
        foreach (var entry in pending.Where(entry => entry.Value.Expires < DateTimeOffset.UtcNow))
            pending.TryRemove(entry.Key, out _);
        if (pending.Count >= 12) throw new DomainError("Encerre autorizações pendentes antes de iniciar outra.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var redirect = $"http://127.0.0.1:{port}/google/retorno";
        pending[state] = new Pending(profile, verifier, redirect, DateTimeOffset.UtcNow.AddMinutes(8));
        return Query("https://accounts.google.com/o/oauth2/v2/auth", new()
        {
            ["client_id"] = config.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = profile.Product == "notebook"
                ? "openid email https://www.googleapis.com/auth/notebook"
                : "openid email https://www.googleapis.com/auth/youtube.upload https://www.googleapis.com/auth/youtube.readonly https://www.googleapis.com/auth/yt-analytics.readonly",
            ["access_type"] = "offline", ["prompt"] = "consent select_account", ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), ["code_challenge_method"] = "S256"
        });
    }

    public async Task Finish(string? state, string? code, CancellationToken token)
    {
        if (state == null || !pending.TryRemove(state, out var authorization) ||
            authorization.Expires < DateTimeOffset.UtcNow || string.IsNullOrWhiteSpace(code))
            throw new DomainError("Autorização cancelada, expirada ou inválida.");
        var config = Client(authorization.Profile.Id);
        using var result = await Token(config, new()
        {
            ["grant_type"] = "authorization_code", ["code"] = code,
            ["redirect_uri"] = authorization.Redirect, ["code_verifier"] = authorization.Verifier
        }, token);
        var access = result.RootElement.GetProperty("access_token").GetString()!;
        var refresh = result.RootElement.TryGetProperty("refresh_token", out var entry) ? entry.GetString() : null;
        if (string.IsNullOrWhiteSpace(refresh)) throw new DomainError("O Google não concedeu renovação para este perfil.");
        var identity = await Identity(access, token);
        if (!string.Equals(identity.Email, authorization.Profile.Expected, StringComparison.OrdinalIgnoreCase) ||
            authorization.Profile.Subject != null && authorization.Profile.Subject != identity.Subject)
            throw new DomainError("A conta que concedeu acesso diverge da conta fixada no perfil.");
        vault.Put("google-grant:" + authorization.Profile.Id, new Grant(refresh, identity.Subject));
        store.Write(db => db.Exec("UPDATE google_profiles SET identity=$e,subject=$s,verified=$n WHERE id=$i",
            identity.Email, identity.Subject, store.Now, authorization.Profile.Id));
    }

    public void Disconnect(string id)
    {
        Get(id);
        vault.Remove("google-grant:" + id);
        store.Write(db => db.Exec("UPDATE google_profiles SET verified=NULL WHERE id=$i", id));
    }

    public async Task<string> Access(GoogleProfile profile, CancellationToken token)
    {
        var grant = vault.Get<Grant>("google-grant:" + profile.Id);
        if (grant == null || profile.Subject == null || grant.Subject != profile.Subject)
            throw new DomainError("Autorize novamente a conta Google deste perfil no Windows.");
        using var response = await Token(Client(profile.Id), new()
        { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.Refresh }, token);
        var access = response.RootElement.GetProperty("access_token").GetString()!;
        var identity = await Identity(access, token);
        if (identity.Subject != profile.Subject || !string.Equals(profile.Expected, identity.Email, StringComparison.OrdinalIgnoreCase))
            throw new DomainError("A conta Google verificada diverge do perfil. A operação aguarda.");
        return access;
    }

    private Config Client(string id) => vault.Get<Config>("google-client:" + id)
        ?? throw new DomainError("Cadastre o cliente OAuth desktop neste Windows.");

    private async Task<JsonDocument> Token(Config config, Dictionary<string, string> form, CancellationToken token)
    {
        form["client_id"] = config.ClientId;
        form["client_secret"] = config.Secret;
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", content, token);
        return await Read(response, "oauth", token);
    }

    private async Task<(string Subject, string Email)> Identity(string bearer, CancellationToken token)
    {
        using var request = Authorized(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo", bearer);
        using var response = await client.SendAsync(request, token);
        using var profile = await Read(response, "identidade", token);
        var root = profile.RootElement;
        if (!root.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)
            throw new DomainError("O Google não confirmou o e-mail do perfil.");
        return (root.GetProperty("sub").GetString()!, root.GetProperty("email").GetString()!);
    }

    internal static async Task<JsonDocument> Read(HttpResponseMessage response, string operation, CancellationToken token) =>
        response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: token)
            ?? throw new DomainError("Resposta vazia da operação " + operation + ".")
        : throw new DomainError($"A operação {operation} recebeu HTTP {(int)response.StatusCode}.");

    internal static HttpRequestMessage Authorized(HttpMethod method, string uri, string bearer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    internal static string Query(string url, Dictionary<string,string> args) => url + "?" +
        string.Join("&", args.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static GoogleProfile Map(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),
        r.GetString(3),r.GetString(4),r.NullableText(5),r.NullableText(6),r.NullableText(7));
    public void Dispose() => client.Dispose();
}
