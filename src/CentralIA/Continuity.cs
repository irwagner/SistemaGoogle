using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CentralIA;

public sealed class Continuation
{
    public string Mode { get; set; } = "";
    public string[] Selected { get; set; } = [];
    public string? Active { get; set; }
    public string State { get; set; } = "pronto";
    public int Step { get; set; }
    public string? Workspace { get; set; }
    public string? Base { get; set; }
    public string Next { get; set; } = "Começar pelo pedido salvo.";
    public string? Issue { get; set; }
    public string? Pending { get; set; }
    public DateTimeOffset? Wake { get; set; }
    public List<StageReceipt> History { get; set; } = [];
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, LimitPause> Pauses { get; set; } = [];
    public Dictionary<string, string> Identities { get; set; } = [];
    public string? BaseRevision { get; set; }
}

public sealed record LimitPause(string Subject, string Group, string Window, DateTimeOffset? Reset, DateTimeOffset Recheck);
public sealed record StageReceipt(string Attempt, int Step, string Requested, string? Effective, string Status,
    string Next, string? Test = null, string? Result = null, Dictionary<string,string>? Before = null, Dictionary<string,string>? After = null);
public sealed record LabIdentity(string Identity, string[] Subjects, string Model);
public sealed record LabChange(string Path, string Content, string? Expected);
public sealed record LabReply(string Identity, LabChange[]? Writes, string? Next, bool Done, bool Test,
    string? Subject = null, string? Group = null, string? Window = null, DateTimeOffset? Reset = null,
    bool LostReceipt = false);

// Production never routes a subscription through this connector. It is enabled only for
// a dedicated data directory and accepts a configured loopback fixture with attested identities.
public sealed class Continuity(Store store, SettingsService settings, EventHub hub)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(12) };
    private const string TestCommand = "node --test --test-reporter=tap tests.mjs";
    private static readonly HashSet<string> TestEnvironment = new(StringComparer.OrdinalIgnoreCase) { "SystemRoot", "WINDIR", "TEMP", "TMP" };
    private readonly ConcurrentDictionary<string, string> active = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> workers = new();
    private readonly object gate = new();
    public static Uri? LabEndpoint { get; } = ResolveLab();
    public static bool Lab => LabEndpoint != null;

    private static Uri? ResolveLab()
    {
        var data = Environment.GetEnvironmentVariable("CENTRAL_DATA");
        return !string.IsNullOrEmpty(data) && File.Exists(Path.Combine(data, ".centralia-laboratorio")) &&
            Uri.TryCreate(Environment.GetEnvironmentVariable("CENTRALIA_LAB_ENDPOINT"), UriKind.Absolute, out var uri) &&
            uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.AbsolutePath == "/" && uri.UserInfo == "" &&
            uri.Query == "" && uri.Fragment == "" ? uri : null;
    }

    public bool IsRunning(string project) => active.ContainsKey(project);
    public bool IsRunning(ChatThread t) => active.TryGetValue(t.ProjectId, out var thread) && thread == t.Id;
    public bool Reserve(ChatThread t) => active.TryAdd(t.ProjectId, t.Id);
    public void Release(ChatThread t) => active.TryRemove(new KeyValuePair<string,string>(t.ProjectId, t.Id));

    public void Validate(string mode, string[] selected)
    {
        if (mode is not ("assistido" or "fixa" or "alternar") || selected.Length < (mode == "alternar" ? 2 : 1) ||
            selected.Length != selected.Distinct().Count() ||
            (mode != "alternar" && selected.Length != 1))
            throw new UserError("Escolha a modalidade e as contas em ordem.");
        if (mode == "assistido")
        {
            if (!settings.Current.Providers.Any(p => p.Id == selected[0] && p.Kind == "google-personal" && p.Enabled))
                throw new UserError("Selecione o perfil pessoal assistido.");
        }
        else if (!Lab || selected.Any(id => !settings.Current.Providers.Any(p => p.Id == id && p.Kind == "ensaio")))
            throw new UserError("A alternância automática está disponível no ensaio local. Use o cliente oficial para contas pessoais.");
    }

    public void Configure(ChatThread t, string mode, string[] selected)
    {
        Validate(mode, selected);
        store.Mutate(t, x =>
        {
            if (x.Messages.Any(m => m.Role == "user")) throw new UserError("A modalidade deve ser escolhida antes do pedido inicial.");
            x.Continuation = new Continuation { Mode = mode, Selected = selected.ToArray(), Active = selected[0] };
        });
    }

    public void Start(ChatThread t, string text)
    {
        lock (gate)
        {
            if (t.Continuation == null || t.Continuation.State != "pronto") throw new UserError("Este trabalho já tem um pedido registrado.");
            var project = store.GetProject(t.ProjectId) ?? throw new UserError("Projeto não encontrado.");
            if (t.Continuation.Mode != "assistido" && project.CustomPath != null)
                throw new UserError("Use um workspace de teste gerenciado pela Central.");
            if (!Reserve(t)) throw new UserError("Já existe uma etapa em execução neste projeto.");
            try
            {
                var ws = store.Workspace(t.ProjectId);
                var files = Snapshot(ws);
                store.Mutate(t, th =>
                {
                    var c = th.Continuation!;
                    c.Workspace = ws;
                    c.Base = Hash(JsonSerializer.SerializeToUtf8Bytes(files, Store.Json));
                    c.BaseRevision = ReadRevision(ws);
                    c.Files = files;
                    c.State = c.Mode == "assistido" ? "operador" : "fila";
                    th.Messages.Add(new ChatMessage { Id = Store.NewId(), Role = "user", Content = text, Time = DateTime.UtcNow });
                });
                store.TouchProject(t.ProjectId);
                Notify(t);
                if (t.Continuation.Mode != "assistido") Launch(t);
                else Release(t);
            }
            catch { Release(t); throw; }
        }
    }

    public object Inspect(ChatThread t)
    {
        lock (t)
        {
            var c = t.Continuation ?? throw new UserError("Configure a continuidade do projeto.");
            var attempt = c.History.LastOrDefault(r => r.Attempt == c.Pending && r.Before != null);
            return new { c.Pending, c.State, before = attempt?.Before ?? c.Files, current = Snapshot(Workspace(t)) };
        }
    }

    public object Package(ChatThread t)
    {
        lock (t)
        {
            var c = t.Continuation ?? throw new UserError("Configure a continuidade do projeto.");
            if (c.Mode != "assistido" || c.State != "operador") throw new UserError("Abra um trabalho assistido e envie o pedido.");
            return new { project = t.ProjectId, task = t.Id, profile = c.Active, c.Workspace, c.Base, c.BaseRevision, c.Files, c.Next, c.History,
                request = t.Messages.First(m => m.Role == "user").Content,
                instructions = "Confira a conta no cliente oficial e continue sobre a mesma pasta e os arquivos atuais. Registre decisões, testes e próximo passo." };
        }
    }

    public void RecordAssisted(ChatThread t, string tested, string next, string? nextProfile, bool done)
    {
        if (string.IsNullOrWhiteSpace(tested) || string.IsNullOrWhiteSpace(next)) throw new UserError("Informe os testes e o próximo passo.");
        lock (t)
        {
            var c = t.Continuation ?? throw new UserError("Configure este trabalho.");
            if (c.Mode != "assistido" || c.State != "operador") throw new UserError("O trabalho não está com o operador.");
            if (!string.IsNullOrWhiteSpace(nextProfile) && !settings.Current.Providers.Any(p => p.Id == nextProfile && p.Enabled && p.Kind == "google-personal"))
                throw new UserError("Selecione outro perfil assistido cadastrado.");
            var files = Snapshot(Workspace(t));
            Save(t, state =>
            {
                state.History.Add(new StageReceipt(Store.NewId(), state.Step++, state.Active!, null, "declarado pelo operador",
                    Store.Clean(next, 1000, "Revisar"), null, Store.Clean(tested, 4000, ""), state.Files, files));
                state.Files = files;
                state.Next = Store.Clean(next, 1000, "Revisar");
                if (done) state.State = "concluido";
                if (!string.IsNullOrWhiteSpace(nextProfile) && nextProfile != state.Active)
                {
                    state.Active = nextProfile;
                    state.Selected = state.Selected.Append(nextProfile).Distinct().ToArray();
                }
            });
        }
    }

    public void Recover()
    {
        foreach (var project in store.Projects())
            foreach (var t in store.Threads(project.Id))
            {
                var c = t.Continuation;
                if (c == null || c.Mode == "assistido") continue;
                if (c.Pending != null)
                    Save(t, x => { x.State = "conciliar"; x.Issue = "Confira o efeito da tentativa interrompida antes de continuar."; });
                else if (Lab && c.State is ("fila" or "limite" or "executando") && Reserve(t)) Launch(t);
            }
    }

    public bool Stop(ChatThread t)
    {
        lock (gate)
        {
            if (!workers.TryGetValue(t.Id, out var cts)) return false;
            cts.Cancel();
            return true;
        }
    }

    public void Resume(ChatThread t)
    {
        lock (gate)
        {
            var c = t.Continuation ?? throw new UserError("Trabalho sem continuidade.");
            if (!Lab || c.Mode == "assistido" || c.Pending != null || c.State is not ("autenticar" or "pausado"))
                throw new UserError("Este trabalho precisa de conciliação ou já está na fila.");
            if (!Reserve(t)) throw new UserError("Já existe uma etapa em execução neste projeto.");
            try { Save(t, x => { x.State = "fila"; x.Issue = null; }); Launch(t); }
            catch { Release(t); throw; }
        }
    }

    private void Launch(ChatThread t)
    {
        lock (gate)
        {
            var cts = new CancellationTokenSource();
            workers[t.Id] = cts;
            _ = Task.Run(async () =>
            {
                try { await Work(t, cts.Token); }
                catch (Exception e) when (e is UserError or HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    Save(t, c =>
                    {
                        c.State = c.Pending != null ? "conciliar" : "pausado";
                        if (c.Pending != null) c.Files = SnapshotOrPrevious(t, c.Files);
                        c.Issue = e is UserError ? e.Message : "A tentativa terminou sem recibo. Confira os arquivos e o efeito original.";
                    });
                }
                finally
                {
                    lock (gate)
                    {
                        workers.TryRemove(t.Id, out _);
                        Release(t);
                        cts.Dispose();
                    }
                }
            });
        }
    }

    private async Task Work(ChatThread t, CancellationToken token)
    {
        var c = t.Continuation!;
        if (!Lab) throw new UserError("Abra o laboratório com os perfis do ensaio.");
        var ws = Workspace(t);
        for (var iteration = 0; iteration < 60;)
        {
            token.ThrowIfCancellationRequested();
            var entries = c.Selected.Select(id => settings.Current.Providers.FirstOrDefault(p => p.Id == id)).ToArray();
            var candidates = new List<(ProviderConfig Provider, LabIdentity Identity, string Key)>();
            foreach (var p in entries.Where(p => p is { Kind: "ensaio", Enabled: true }).Select(p => p!))
            {
                var key = settings.GetKey(p.Id);
                if (key == null) continue;
                var identity = await Identity(key, token);
                if (identity != null && (identity.Identity != p.DeclaredIdentity ||
                    (c.Identities.TryGetValue(p.Id, out var previous) && previous != identity.Identity)))
                    throw new UserError("A identidade do perfil mudou. Confira a conexão original.");
                if (identity != null && !c.Identities.ContainsKey(p.Id))
                    Save(t, x => x.Identities[p.Id] = identity.Identity);
                if (identity != null) candidates.Add((p, identity, key));
            }
            if (candidates.GroupBy(p => p.Identity.Identity).Any(g => g.Count() > 1))
                throw new UserError("Dois perfis representam a mesma identidade conferida.");
            if (c.Mode == "fixa" && candidates.Count == 0)
            {
                Save(t, x => { x.State = "autenticar"; x.Issue = "A conta fixada precisa de reautenticação."; });
                return;
            }
            var now = DateTimeOffset.UtcNow;
            bool Limited(LabIdentity identity) => c.Pauses.Values.Any(pause =>
                (pause.Subject == identity.Identity || identity.Subjects.Contains(pause.Subject)) && pause.Recheck > now);
            if (!candidates.Any(i => i.Provider.Id == c.Active) &&
                !(c.State == "limite" && c.History.LastOrDefault(r => r.Requested == c.Active)?.Status == "limite confirmado"))
            {
                Save(t, x => { x.State = "autenticar"; x.Wake = null; x.Issue = "Reautentique a conexão ativa e verifique a identidade."; });
                return;
            }
            var ordered = candidates.OrderByDescending(i => i.Provider.Id == c.Active);
            var candidate = ordered.FirstOrDefault(i => !Limited(i.Identity));
            if (candidate.Provider == null)
            {
                if (candidates.Count == 0)
                {
                    Save(t, x => { x.State = "autenticar"; x.Issue = "Nenhuma identidade selecionada foi confirmada."; });
                    return;
                }
                var wakes = candidates.Select(i => c.Pauses.Values.Where(p => p.Subject == i.Identity.Identity || i.Identity.Subjects.Contains(p.Subject))
                    .Max(p => p.Recheck)).ToArray();
                var wake = wakes.Min();
                Save(t, x => { x.State = "limite"; x.Wake = wake; });
                var delay = Math.Clamp((wake - DateTimeOffset.UtcNow).TotalMilliseconds, 50, TimeSpan.FromMinutes(15).TotalMilliseconds);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                continue;
            }
            iteration++;
            var before = Snapshot(ws);
            if (!before.OrderBy(x => x.Key).SequenceEqual(c.Files.OrderBy(x => x.Key)))
                throw new UserError("Os arquivos mudaram após o último checkpoint. Preserve as alterações e revise antes de retomar.");
            var attempt = Store.NewId();
            var next = c.Next;
            Save(t, x =>
            {
                if (x.Active != candidate.Provider.Id)
                    x.History.Add(new StageReceipt(attempt, x.Step, candidate.Provider.Id, candidate.Identity.Identity, "troca após limite", x.Next));
                x.Active = candidate.Provider.Id;
                x.Pending = attempt;
                x.State = "executando";
                x.Wake = null;
                x.Issue = null;
                x.History.Add(new StageReceipt(attempt, x.Step, x.Active, null, "intenção persistida", x.Next, Before: before));
            });
            var (reply, status) = await Request(candidate.Key, new
            {
                project = t.ProjectId, task = t.Id, attempt, step = c.Step, next, c.Base, c.BaseRevision,
                pedido = t.Messages.First(m => m.Role == "user").Content,
                files = before.Keys.Where(name => new FileInfo(Store.Inside(ws, name)).Length <= 20000).Take(30)
                    .ToDictionary(name => name, name => File.ReadAllText(Store.Inside(ws, name))),
                hashes = before, receipts = c.History.Where(h => h.Status == "confirmado").Select(h => new { h.Step, h.Test, h.Result, h.Next })
            }, token);
            if (reply.Identity != candidate.Identity.Identity) throw new UserError("A identidade efetiva divergiu da conta selecionada.");
            if (status == 429)
            {
                var subject = reply.Subject ?? reply.Identity;
                if (subject != reply.Identity && !candidate.Identity.Subjects.Contains(subject))
                    throw new UserError("O sujeito da cota não foi confirmado pelo conector.");
                var group = reply.Group ?? "desconhecido";
                var window = reply.Window ?? "desconhecida";
                var probe = reply.Reset is { } reset && reset > now ? reset : now.AddSeconds(10);
                Save(t, x =>
                {
                    x.Pauses[subject + "|" + group + "|" + window] = new LimitPause(subject, group, window, reply.Reset, probe);
                    x.History.Add(new StageReceipt(attempt, x.Step, x.Active!, reply.Identity, "limite confirmado", x.Next));
                    x.Pending = null;
                    x.State = "limite";
                });
                continue;
            }
            if (status is 401 or 403)
            {
                Save(t, x => { x.Pending = null; x.State = "autenticar"; x.Issue = "Reautentique a conta original."; });
                return;
            }
            if (status != 200) throw new UserError("O conector não forneceu recibo conclusivo.");
            Save(t, x => x.History.Add(new StageReceipt(attempt, x.Step, x.Active!, reply.Identity, "identidade conferida", x.Next)));
            if (reply.Writes is { Length: > 8 }) throw new UserError("A etapa excede o número de arquivos permitido.");
            var writes = reply.Writes ?? [];
            if (writes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != writes.Length)
                throw new UserError("A etapa contém uma escrita repetida.");
            foreach (var change in writes)
            {
                var path = SafePath(ws, change.Path);
                if (change.Content.Length > 100000 || before.GetValueOrDefault(change.Path) != change.Expected ||
                    (File.Exists(path) ? Hash(File.ReadAllBytes(path)) : null) != change.Expected)
                    throw new UserError("A revisão de entrada não confere. Confira as mudanças nesta tentativa.");
            }
            foreach (var change in writes)
            {
                var file = SafePath(ws, change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + "." + attempt + ".tmp";
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await output.WriteAsync(Encoding.UTF8.GetBytes(change.Content), token);
                if ((File.Exists(file) ? Hash(File.ReadAllBytes(file)) : null) != change.Expected)
                    throw new UserError("O arquivo mudou durante a etapa. Confira os efeitos antes de retomar.");
                File.Move(temp, file, change.Expected != null);
                if (reply.LostReceipt) throw new UserError("Timeout injetado após escrita. Inspecione o efeito antes de retomar.");
            }
            string? result = null;
            if (reply.Test)
            {
                var test = await RunTests(ws, token);
                result = test.Result;
                if (!test.Passed)
                {
                    Save(t, x => x.History.Add(new StageReceipt(attempt, x.Step, x.Active!, reply.Identity,
                        "testes falharam", x.Next, TestCommand, result, before, Snapshot(ws))));
                    throw new UserError("Os testes falharam. Confira os arquivos da tentativa.");
                }
            }
            var after = Snapshot(ws);
            Save(t, x =>
            {
                x.Files = after;
                x.Next = Store.Clean(reply.Next, 1500, "Conferir a etapa.");
                x.History.Add(new StageReceipt(attempt, x.Step++, x.Active!, reply.Identity, "confirmado", x.Next,
                    reply.Test ? TestCommand : null, result, before, after));
                x.Pending = null;
                x.State = reply.Done && reply.Test ? "concluido" : "fila";
            });
            if (c.State == "concluido") return;
        }
        throw new UserError("Limite de etapas do ensaio atingido. Confira o histórico.");
    }

    private async Task<LabIdentity?> Identity(string key, CancellationToken token)
    {
        using var req = Call(HttpMethod.Get, "identity", key);
        using var res = await Http.SendAsync(req, token);
        if ((int)res.StatusCode is 401 or 403) return null;
        if (!res.IsSuccessStatusCode) throw new UserError("A verificação da conexão falhou temporariamente. Retome após restaurar o conector.");
        var identity = await res.Content.ReadFromJsonAsync<LabIdentity>(Store.Json, token);
        if (identity == null || !identity.Identity.StartsWith("ensaio-", StringComparison.Ordinal) ||
            identity.Subjects == null || identity.Model != "codigo-ensaio") return null;
        return identity;
    }

    private async Task<(LabReply, int)> Request(string key, object request, CancellationToken token)
    {
        using var req = Call(HttpMethod.Post, "step", key);
        req.Content = JsonContent.Create(request);
        using var res = await Http.SendAsync(req, token);
        var reply = await res.Content.ReadFromJsonAsync<LabReply>(Store.Json, token)
            ?? throw new UserError("O conector não devolveu recibo.");
        return (reply, (int)res.StatusCode);
    }
    private static HttpRequestMessage Call(HttpMethod method, string path, string key) =>
        new(method, new Uri(LabEndpoint!, path)) { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", key) } };

    private string Workspace(ChatThread t)
    {
        var path = store.Workspace(t.ProjectId);
        if (!string.Equals(path, t.Continuation?.Workspace, StringComparison.OrdinalIgnoreCase))
            throw new UserError("O workspace mudou. Volte à pasta registrada.");
        return path;
    }
    private Dictionary<string, string> SnapshotOrPrevious(ChatThread t, Dictionary<string,string> previous)
    { try { return Snapshot(Workspace(t)); } catch (Exception e) when (e is IOException or UserError or UnauthorizedAccessException) { return previous; } }

    private static Dictionary<string,string> Snapshot(string ws)
    {
        var files = Store.ListFiles(ws, 1001);
        if (files.Count > 1000) throw new UserError("Use até 1000 arquivos no workspace do ensaio.");
        return files.ToDictionary(f => f.Path, f => Hash(File.ReadAllBytes(SafePath(ws, f.Path))), StringComparer.OrdinalIgnoreCase);
    }
    private static string SafePath(string ws, string name)
    {
        if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(part =>
            part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new UserError("Caminho inválido no workspace do ensaio.");
        var path = Store.Inside(ws, name);
        for (var current = path; current != null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UserError("O workspace contém um link ou ponto de reanálise.");
        return path;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string? ReadRevision(string ws)
    {
        var git = CliRunner.FindExe("git");
        if (git == null || !git.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var start = new ProcessStartInfo(git) { UseShellExecute = false, WorkingDirectory = ws, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("--verify");
        start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start);
        if (process == null) return null;
        if (!process.WaitForExit(1500)) { process.Kill(true); return null; }
        return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd().Trim() : null;
    }

    private static async Task<(bool Passed, string Result)> RunTests(string ws, CancellationToken token)
    {
        _ = SafePath(ws, "tests.mjs");
        var node = CliRunner.FindExe("node") ?? throw new UserError("Instale Node.js para a prova de programação.");
        if (!node.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new UserError("O ensaio requer node.exe.");
        var start = new ProcessStartInfo(node) { UseShellExecute = false, WorkingDirectory = ws, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add("--test-reporter=tap");
        start.ArgumentList.Add("tests.mjs");
        foreach (var key in start.Environment.Keys.Where(key => !TestEnvironment.Contains(key)).ToArray())
            start.Environment.Remove(key);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode == 0, Store.Clean($"{await output}\n{await error}", 3000, $"Node saiu com código {process.ExitCode}."));
    }
    private void Save(ChatThread t, Action<Continuation> action) { store.Mutate(t, x => action(x.Continuation!)); Notify(t); }
    private void Notify(ChatThread t) => hub.Publish(t.Id, new { type = "continuity" });
}
