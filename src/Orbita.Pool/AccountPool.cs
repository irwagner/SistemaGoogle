using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Orbita.Core;

namespace Orbita.Pool;

public sealed class AccountPool(StudioFiles files, IQuotaPolicy quota) : IDisposable
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(35) };
    private readonly SemaphoreSlim gate = new(1);
    private Process? worker;
    private FileStream? vaultLease;
    private Uri? address;
    private string? key;
    private static string? Node => LocalTools.Locate("node");
    private static string? Pwsh => LocalTools.Locate("pwsh");
    private bool Running => address != null && worker is { HasExited: false };
    private string DataDirectory => Path.Combine(files.DataRoot, "antigravity-pool");

    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var status = Running ? await CallAsync("status", null, ct) as JsonObject : null;
        var metadata = status ?? await ReadSavedAsync(ct);
        var accounts = (metadata?["accounts"] as JsonArray ?? []).OfType<JsonObject>().Select(Parse).ToArray();
        var reserve = (int?)metadata?["lowQuotaPercent"] ?? 0;
        var now = DateTimeOffset.UtcNow;
        return new { running = status != null, reserve, accounts,
            ready = accounts.Count(a => a.Observed && (quota.IsReady(a, true, reserve, now) || quota.IsReady(a, false, reserve, now))),
            active = (string?)status?["session"]?["account"], model = (string?)status?["lastRequestedModel"],
            mode = (string?)status?["session"]?["mode"], enrollment = status?["enrollment"],
            dependencies = new { node = Node != null, powershell = Pwsh != null, agy = LocalTools.Agy() != null } };
    }

    private async Task<JsonObject?> ReadSavedAsync(CancellationToken ct)
    {
        var file = Path.Combine(DataDirectory, "accounts.json");
        try
        {
            await using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonNode.ParseAsync(stream, cancellationToken: ct) as JsonObject;
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { return null; }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (Running) return;
            Stop();
            var node = Node ?? throw new StudioException("Instale o Node.js 20 ou superior para abrir o Account Pool.");
            var pwsh = Pwsh ?? throw new StudioException("Instale o PowerShell 7 para abrir o Account Pool.");
            var agy = LocalTools.Agy() ?? throw new StudioException("Instale o Antigravity CLI oficial.");
            var script = Path.Combine(AppContext.BaseDirectory, "AntigravityPool", "server.mjs");
            if (!File.Exists(script)) throw new StudioException("O componente Account Pool está ausente no pacote.");
            var vaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralIA");
            Directory.CreateDirectory(vaultRoot);
            try { vaultLease = File.Open(Path.Combine(vaultRoot, "agy-pool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new StudioException("O Account Pool já está aberto em outra instância. Feche o outro aplicativo para usar as contas no Orbita."); }

            var data = DataDirectory;
            Directory.CreateDirectory(data);
            key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = data };
            start.ArgumentList.Add(script);
            foreach (var env in new[] { "NODE_OPTIONS", "NODE_PATH", "CENTRALIA_POOL_TEST_URL", "CLOUD_CODE_URL",
                "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "AGY_ADC_AUTH", "GOOGLE_GEMINI_BASE_URL" })
                start.Environment.Remove(env);
            start.Environment["CENTRALIA_POOL_DATA"] = data;
            start.Environment["CENTRALIA_POOL_KEY"] = key;
            start.Environment["AGY_POOL_AGY_PATH"] = agy;
            start.Environment["AGY_POOL_PWSH"] = pwsh;
            try
            {
                worker = Process.Start(start) ?? throw new StudioException("O Account Pool não iniciou.");
                worker.BeginErrorReadLine();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var line = await worker.StandardOutput.ReadLineAsync(timeout.Token);
                const string prefix = "CENTRALIA_POOL_PORT:";
                if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal) ||
                    !int.TryParse(line.AsSpan(prefix.Length), out var port) || port is < 1024 or > 65535)
                    throw new StudioException("O Account Pool falhou ao iniciar. Confira as dependências e o login oficial.");
                address = new Uri($"http://127.0.0.1:{port}/");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new StudioException("O Account Pool demorou para iniciar."); }
        }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }

    public Task<JsonNode> AddAsync(CancellationToken ct) => CallAsync("accounts/add", new { }, ct);
    public Task<JsonNode> CancelLoginAsync(CancellationToken ct) => CallAsync("accounts/cancel", new { }, ct);
    public Task<JsonNode> RefreshAsync(CancellationToken ct) => CallAsync("quota/check", new { }, ct);
    public Task<JsonNode> TestRotationAsync(string[]? accounts, string? model, CancellationToken ct) =>
        CallAsync("diagnostics/rotation", new { accounts, model }, ct);
    public Task<JsonNode> VerificationAsync(string id, CancellationToken ct) =>
        CallAsync("accounts/verification", new { id }, ct);
    public Task<JsonNode> ThresholdAsync(int percent, CancellationToken ct) => CallAsync("settings/threshold", new { percent }, ct);

    public async Task<PoolLease> ReserveAsync(Mission mission, string[] accounts, CancellationToken ct)
    {
        await StartAsync(ct);
        var leaseId = StudioFiles.NewId();
        var uri = address ?? throw new StudioException("Abra o Account Pool em Contas.");
        try
        {
            var mode = accounts.Length == 1 ? "fixa" : "alternar";
            await CallAsync("projects/reserve", new { projectId = mission.ProjectId, path = mission.Workspace, mode, accounts, leaseId }, ct);
            return new PoolLease(uri, leaseId, this);
        }
        catch { await ReleaseAsync(leaseId); throw; }
    }

    public sealed class PoolLease(Uri address, string id, AccountPool owner) : IAsyncDisposable
    {
        public Uri Address { get; } = address;
        public ValueTask DisposeAsync() => owner.ReleaseAsync(id);
    }

    private async ValueTask ReleaseAsync(string leaseId)
    {
        if (!Running) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await CallAsync("projects/release", new { leaseId }, timeout.Token); }
        catch (Exception error) when (error is StudioException or OperationCanceledException) { }
    }

    private async Task<JsonNode> CallAsync(string route, object? payload, CancellationToken ct)
    {
        var current = address;
        if (current == null || key == null || !Running) throw new StudioException("Abra o Account Pool em Contas.");
        using var request = new HttpRequestMessage(payload == null ? HttpMethod.Get : HttpMethod.Post, new Uri(current, "api/" + route));
        request.Headers.Add("X-Agy-Pool-Key", key);
        if (payload != null) request.Content = JsonContent.Create(payload);
        try
        {
            using var response = await Http.SendAsync(request, ct);
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
            if (!response.IsSuccessStatusCode || json == null) throw new StudioException((string?)json?["error"] ?? "O Account Pool recusou a operação.");
            return json;
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException ||
            (error is OperationCanceledException && !ct.IsCancellationRequested)) { throw new StudioException("O Account Pool local não respondeu."); }
    }

    private static QuotaAccount Parse(JsonObject row)
    {
        var group = row["quotaPools"];
        var verificationRequired = (bool?)row["verificationRequired"] == true;
        return new((string?)row["id"] ?? "", (string?)row["label"] ?? "Conta", (string?)row["email"] ?? "",
            Family(group?["gemini"]), Family(group?["other"]), (long?)row["blockedUntil"] ?? 0,
            (long?)row["blockedGroups"]?["gemini"] ?? 0, (long?)row["blockedGroups"]?["other"] ?? 0,
            !verificationRequired && row["quotaError"] == null && row["quotaCheckedAt"] != null, verificationRequired);
    }

    private static QuotaFamily Family(JsonNode? group) => new(Window(group?["fiveHour"]), Window(group?["weekly"]));
    private static QuotaWindow? Window(JsonNode? value) => value?["percent"] is JsonValue percent
        ? new(percent.GetValue<int>(), DateTimeOffset.TryParse((string?)value["resetTime"], out var at) ? at : null) : null;

    private void Stop()
    {
        address = null;
        key = null;
        var current = worker;
        worker = null;
        if (current != null)
        {
            try
            {
                if (!current.HasExited)
                {
                    current.StandardInput.Close();
                    if (!current.WaitForExit(5000)) current.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) { }
            current.Dispose();
        }
        vaultLease?.Dispose();
        vaultLease = null;
    }

    public void Dispose()
    {
        gate.Wait();
        try { Stop(); }
        finally { gate.Release(); }
    }
}
