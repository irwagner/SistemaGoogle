using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CentralIA;

public sealed class AntigravityPoolService(Store store, Continuity continuity) : IDisposable
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(45) };
    private readonly SemaphoreSlim gate = new(1);
    private Process? process;
    private FileStream? credentialLease;
    private int port;
    private string? key;

    private static bool Testing => Continuity.Lab;
    private static string? AgyPath
    {
        get
        {
            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
            return File.Exists(local) ? local : FindExe("agy");
        }
    }
    private static string? FindExe(string name) => CliRunner.FindExe(name) is { } file &&
        file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file : null;

    public async Task<object> Status(CancellationToken ct)
    {
        var ready = process is { HasExited: false } && port > 0;
        var state = ready ? await Call("status", null, ct) : null;
        return new { ready, simulated = Testing, dependencies = new { agy = AgyPath != null || Testing,
            node = FindExe("node") != null, pwsh = FindExe("pwsh") != null || Testing }, pool = state };
    }

    public async Task Start(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (process is { HasExited: false } && port > 0) return;
            Stop();
            var node = FindExe("node") ?? throw new UserError("Instale Node.js 20 ou mais recente.");
            var agy = AgyPath;
            var powershell = FindExe("pwsh");
            if (!Testing && (agy == null || powershell == null))
                throw new UserError("Instale o Antigravity CLI oficial e o PowerShell 7 para abrir o pool.");
            if (!Testing)
            {
                var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".gemini", "antigravity-cli", "settings.json");
                if (File.Exists(settingsPath))
                {
                    try
                    {
                        var config = JsonNode.Parse(File.ReadAllText(settingsPath), documentOptions: new JsonDocumentOptions
                        { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                        if ((string?)config?["modelProvider"] == "gemini")
                            throw new UserError("O Antigravity CLI usa uma chave Gemini. Selecione o login Google no CLI para usar as assinaturas.");
                    }
                    catch (JsonException) { throw new UserError("Confira o arquivo de configuração do Antigravity CLI."); }
                }
            }
            var path = Path.Combine(AppContext.BaseDirectory, "AntigravityPool", "server.mjs");
            if (!File.Exists(path)) throw new UserError("Extraia todo o pacote da Central IA.");
            var data = Path.Combine(store.Root, "antigravity-pool");
            Directory.CreateDirectory(data);
            var leaseRoot = Testing ? data : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralIA");
            Directory.CreateDirectory(leaseRoot);
            try { credentialLease = File.Open(Path.Combine(leaseRoot, "agy-pool.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { throw new UserError("O pool está aberto em outra instância da Central."); }

            key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var start = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = data };
            start.ArgumentList.Add(path);
            foreach (var option in new[] { "NODE_OPTIONS", "NODE_PATH", "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS",
                "AGY_ADC_AUTH", "CLOUD_CODE_URL", "GOOGLE_GEMINI_BASE_URL", "CENTRALIA_POOL_TEST_URL", "AGY_POOL_AGY_PATH", "AGY_POOL_PWSH" })
                start.Environment.Remove(option);
            start.Environment["CENTRALIA_POOL_DATA"] = data;
            start.Environment["CENTRALIA_POOL_KEY"] = key;
            if (Testing) start.Environment["CENTRALIA_POOL_TEST_URL"] = Continuity.LabEndpoint!.ToString();
            if (agy != null) start.Environment["AGY_POOL_AGY_PATH"] = agy;
            if (powershell != null) start.Environment["AGY_POOL_PWSH"] = powershell;
            try
            {
                process = Process.Start(start) ?? throw new UserError("Não foi possível iniciar o pool.");
                process.BeginErrorReadLine();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                const string Prefix = "CENTRALIA_POOL_PORT:";
                if (line == null || !line.StartsWith(Prefix, StringComparison.Ordinal) ||
                    !int.TryParse(line[Prefix.Length..], out var bound) || bound is < 1024 or > 65535)
                    throw new UserError("O processo do pool não iniciou. Confira o Node.js e as dependências do Antigravity CLI.");
                port = bound;
            }
            catch (Exception e) when (e is not UserError and not OperationCanceledException)
            { Stop(); throw new UserError("Falha ao iniciar o processo do Antigravity Account Pool."); }
            catch { Stop(); throw; }
        }
        finally { gate.Release(); }
    }

    public Task<JsonNode> Add(CancellationToken ct) => Call("accounts/add", new { }, ct);
    public Task<JsonNode> Cancel(CancellationToken ct) => Call("accounts/cancel", new { }, ct);
    public Task<JsonNode> Refresh(CancellationToken ct) => Call("quota/check", new { }, ct);
    public Task<JsonNode> NextLimit(CancellationToken ct) => Call("diagnostics/next429", new { }, ct);
    public Task<JsonNode> SetThreshold(int percent, CancellationToken ct) => Call("settings/threshold", new { percent }, ct);

    public async Task<JsonNode> Launch(string pid, string mode, string[] accounts, bool resume, CancellationToken ct)
    {
        if (store.GetProject(pid) == null) throw new UserError("Escolha um projeto existente.");
        var reservation = new ChatThread { ProjectId = pid, Id = Store.NewId() };
        if (!continuity.Reserve(reservation)) throw new UserError("Já existe uma sessão usando este projeto.");
        try
        {
            var owner = process;
            var result = await Call("projects/launch", new { projectId = pid, path = store.Workspace(pid), mode, accounts, resume }, CancellationToken.None);
            _ = ReleaseWhenClosed(reservation, owner);
            store.TouchProject(pid);
            return result;
        }
        catch { continuity.Release(reservation); throw; }
    }

    private async Task ReleaseWhenClosed(ChatThread reservation, Process? owner)
    {
        try
        {
            while (ReferenceEquals(owner, process))
            {
                await Task.Delay(2000);
                if (!ReferenceEquals(owner, process) || owner is not { HasExited: false }) break;
                try
                {
                    if ((string?) (await Call("status", null, CancellationToken.None))["session"]?["projectId"] != reservation.ProjectId)
                        break;
                }
                catch (UserError) { /* Keep the workspace reserved while the pool's state is uncertain. */ }
            }
        }
        finally { continuity.Release(reservation); }
    }

    private async Task<JsonNode> Call(string route, object? body, CancellationToken ct)
    {
        var currentPort = port;
        var currentKey = key;
        if (currentPort == 0 || currentKey == null || process is not { HasExited: false })
            throw new UserError("Inicie o Antigravity Account Pool em Contas e modelos.");
        using var message = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post,
            $"http://127.0.0.1:{currentPort}/api/{route}");
        message.Headers.Add("X-Agy-Pool-Key", currentKey);
        if (body != null) message.Content = JsonContent.Create(body);
        try
        {
            using var reply = await Http.SendAsync(message, ct);
            var json = await reply.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
            if (!reply.IsSuccessStatusCode || json == null)
                throw new UserError((string?)json?["error"] ?? "O pool recusou a operação.");
            return json;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        { throw new UserError("O pool local não respondeu. Confira se a Central ainda está aberta."); }
    }

    private void Stop()
    {
        port = 0;
        key = null;
        var current = process;
        process = null;
        if (current != null)
        {
            try
            {
                if (!current.HasExited)
                {
                    current.StandardInput.Close();
                    current.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException) { }
            current.Dispose();
        }
        credentialLease?.Dispose();
        credentialLease = null;
    }

    public void Dispose()
    {
        gate.Wait();
        try { Stop(); }
        finally { gate.Release(); }
    }
}
