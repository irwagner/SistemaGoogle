using Central.Domain;
using Microsoft.Data.Sqlite;

namespace Central.Operations;

public sealed record NotebookJob(string Id, string Work, string Project, string Account, string Destination, string Label,
    string? Asset, string? Body, string State, string? Remote, string? Error);

public sealed class NotebookJobs(Store store, Workbench workbench, GoogleAuthorization google) : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(4) };
    private const string Fields = "id,work,project,account,destination,label,asset,body,state,remote,error";

    public IReadOnlyList<NotebookJob> List(string work) => store.Read(db => db.List($"SELECT {Fields} FROM notebook_jobs WHERE work=$w ORDER BY updated DESC", Map, work));
    public void Queue(string work, string account, string? destination, string label, string? asset, string? body, bool authorized)
    {
        var item = workbench.Get(work);
        var profile = google.Get(account);
        if (item.Area != "pesquisa" || profile.Product != "notebook" || !google.Connected(profile))
            throw new DomainError("Autorize o perfil pessoal de Notebook antes de enfileirar.");
        if (!authorized) throw new DomainError("Confirme a fonte, o destino e o uso da API neste projeto Cloud.");
        label = Store.Text(label, 500, "o título");
        destination = destination?.Trim() ?? "";
        if (destination.Length != 0 && !ValidName(destination, source: false)) throw new DomainError("Use notebooks/ID como destino.");
        asset = string.IsNullOrWhiteSpace(asset) ? null : asset;
        body = string.IsNullOrWhiteSpace(body) ? null : Store.Text(body, 15000, "o texto da fonte");
        if (destination.Length == 0 ? asset != null || body != null : (asset == null) == (body == null))
            throw new DomainError("Para criar caderno, deixe o conteúdo vazio. Para ingerir, confirme somente texto ou arquivo.");
        if (asset != null)
        {
            var input = workbench.Files(item).FirstOrDefault(file => file.Asset.Id == asset)?.Asset
                ?? throw new DomainError("Vincule o arquivo a este trabalho.");
            Mime(input.Type);
            if (input.Size > 209_715_200) throw new DomainError("O arquivo excede o limite do Notebook.");
        }
        store.Write(db =>
        {
            if (Hold(db)) throw new DomainError("A base foi restaurada. Concilie as operações anteriores antes de novos envios.");
            if (db.Exec("""
                INSERT OR IGNORE INTO notebook_jobs(id,work,project,account,destination,label,asset,body,state,authorization,updated)
                VALUES($i,$w,$p,$a,$d,$l,$f,$b,'fila',$auth,$n)
                """, Store.Id(), work, item.Project, account, destination, label, asset, body,
                Store.Json(new { profile.Subject, profile.Cloud, asset, destination, approvedAt = store.Now }), store.Now) != 1)
                throw new DomainError("Já existe uma operação com este título e destino. Confira o efeito original.");
        });
    }

    public void Recover() => store.Write(db =>
    {
        db.Exec("UPDATE notebook_jobs SET state='fila' WHERE state='preparando'");
        db.Exec("""
            UPDATE notebook_jobs SET state='indeterminado',error='Envio interrompido. Confira o efeito original.',updated=$n WHERE state='enviando'
            """, store.Now);
    });
    public void Reconcile(string work, string id, string source)
    {
        var job = List(work).SingleOrDefault(item => item.Id == id) ?? throw new DomainError("Operação não encontrada.");
        if (job.State != "indeterminado" || !ValidName(source, job.Destination.Length > 0) ||
            job.Destination.Length > 0 && !source.StartsWith(job.Destination + "/sources/", StringComparison.Ordinal))
            throw new DomainError("Informe o identificador confirmado no caderno original.");
        Update(job.Id, "PROCESSING", source, null);
    }

    public async Task<bool> Next(CancellationToken token)
    {
        var job = store.Write(db =>
        {
            var item = db.One($"""
                SELECT {Fields} FROM notebook_jobs WHERE state='PROCESSING' AND updated<$p
                OR state='fila' AND NOT EXISTS(SELECT 1 FROM service_flags WHERE key='restore_hold')
                ORDER BY updated LIMIT 1
                """, Map, store.Clock.GetUtcNow().AddSeconds(-4).ToString("O"));
            if (item?.State == "fila")
                db.Exec("UPDATE notebook_jobs SET state='preparando',updated=$n WHERE id=$i", store.Now, item.Id);
            return item;
        });
        if (job == null) return false;
        var sent = false;
        try
        {
            var profile = google.Get(job.Account);
            var bearer = await google.Access(profile, token);
            var connector = new Notebook(client);
            if (job.Remote != null)
            {
                var checkedSource = await connector.Get(job.Remote, profile.Cloud, bearer, token);
                Update(job.Id, checkedSource.State ?? "PRONTO", checkedSource.Name, null);
                return true;
            }
            if (job.State != "fila") throw new DomainError("Operação sem identificador original.");
            var canSend = store.Write(db => !Hold(db) && db.Exec("""
                UPDATE notebook_jobs SET state='enviando',updated=$n WHERE id=$i AND state='preparando'
                """, store.Now, job.Id) == 1);
            if (!canSend) return true;
            sent = true;
            NotebookResource submitted;
            if (job.Destination.Length == 0)
                submitted = await connector.CreateNotebook(job.Label, profile.Cloud, bearer, token);
            else if (job.Asset == null)
                submitted = await connector.CreateText(job.Destination, job.Label, job.Body!, profile.Cloud, bearer, token);
            else
            {
                var asset = store.GetAsset(job.Project, job.Asset);
                await using var stream = File.OpenRead(store.Verify(asset));
                submitted = await connector.Upload(job.Destination, job.Label, Mime(asset.Type), stream, asset.Size, profile.Cloud, bearer, token);
            }
            Update(job.Id, submitted.State ?? "PRONTO", submitted.Name, null);
        }
        catch (Exception error) when (!token.IsCancellationRequested && (error is DomainError or IOException or HttpRequestException or
            OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException))
        {
            Update(job.Id, sent ? "indeterminado" : "aguardando", job.Remote, error is DomainError ? error.Message : "A chamada do Notebook não foi confirmada.");
        }
        return true;
    }

    public void RetryInspection(string work, string id)
    {
        var job = List(work).SingleOrDefault(item => item.Id == id) ?? throw new DomainError("Operação não encontrada.");
        if (job.State != "aguardando") throw new DomainError("O estado atual requer conciliação ou já está em fila.");
        Update(id, job.Remote == null ? "fila" : "PROCESSING", job.Remote, null);
    }

    private void Update(string id, string state, string? remote, string? error) => store.Write(db =>
        db.Exec("UPDATE notebook_jobs SET state=$s,remote=$r,error=$e,updated=$n WHERE id=$i", state, remote, error, store.Now, id));
    private static bool Hold(SqliteConnection db) => db.Scalar<long>("SELECT COUNT(*) FROM service_flags WHERE key='restore_hold'") != 0;
    private static NotebookJob Map(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.GetString(5),r.NullableText(6),r.NullableText(7),r.GetString(8),r.NullableText(9),r.NullableText(10));
    private static bool ValidName(string value, bool source) => System.Text.RegularExpressions.Regex.IsMatch(value,
        source ? @"^notebooks/[A-Za-z0-9_-]+/sources/[A-Za-z0-9_-]+$" : @"^notebooks/[A-Za-z0-9_-]+$");
    private static string Mime(string type) => type switch
    {
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".md" => "text/markdown", ".csv" => "text/csv",
        ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".png" => "image/png", ".jpg" => "image/jpeg",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        _ => throw new DomainError("Formato de fonte indisponível para Notebook.")
    };
    public void Dispose() => client.Dispose();
}

public sealed class NotebookWorker(NotebookJobs queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (!await queue.Next(stop)) await Task.Delay(1000, stop);
        }
    }
}
