using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CentralIA;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

Console.OutputEncoding = Encoding.UTF8;
var version = typeof(Store).Assembly.GetName().Version!.ToString(3);
var port = int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_PORT"), out var envPort) ? envPort : 7770;
if (port is < 1024 or >= 65535) { Console.Error.WriteLine("CENTRAL_PORT deve estar entre 1024 e 65534."); Environment.ExitCode = 1; return; }
var previewPort = port + 1;
var noBrowser = args.Contains("--sem-navegador") || Environment.GetEnvironmentVariable("CENTRAL_NO_BROWSER") == "1";
var mainUrl = $"http://127.0.0.1:{port}/";
var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Store.ResolveRoot()));
var instance = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dataRoot.ToUpperInvariant())));

if (!PortFree(port))
{
    using var local = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        if (await local.GetStringAsync(mainUrl + "build") == $"{version}/{instance}")
        {
            Console.WriteLine($"Central IA {version} já está aberta em {mainUrl}");
            if (!noBrowser) OpenBrowser(mainUrl);
            return;
        }
    }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
    Console.Error.WriteLine($"Outra instância está usando a porta {port}. Feche a Central anterior ou use CENTRAL_PORT e CENTRAL_DATA separados.");
    Environment.ExitCode = 1;
    return;
}
if (!PortFree(previewPort))
{
    Console.Error.WriteLine($"A porta de prévia {previewPort} está ocupada. Escolha outro CENTRAL_PORT.");
    Environment.ExitCode = 1;
    return;
}

Directory.CreateDirectory(dataRoot);
FileStream lease;
try { lease = File.Open(Path.Combine(dataRoot, ".centralia.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException)
{
    Console.Error.WriteLine("A pasta de dados está em uso. Feche a outra Central ou escolha outro CENTRAL_DATA.");
    Environment.ExitCode = 1;
    return;
}
using var dataLease = lease;
var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot)) webRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory, WebRootPath = webRoot });
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(IPAddress.Loopback, port);
    k.Listen(IPAddress.Loopback, previewPort);
    k.Limits.MaxRequestBodySize = 1024L * 1024 * 1024;
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 1024L * 1024 * 1024);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

var store = new Store(dataRoot);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LlmRouter>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<Continuity>();
builder.Services.AddSingleton<AgentService>();
builder.Services.AddSingleton<AntigravityPoolService>();
builder.Services.AddSingleton<FlowService>();

var app = builder.Build();
var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mjs"] = "text/javascript";

app.Use(async (ctx, next) =>
{
    var host = ctx.Request.Host.Host;
    if (host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
    var localPort = ctx.Connection.LocalPort;
    var path = ctx.Request.Path.Value ?? "/";
    if (localPort == previewPort)
    {
        if (!path.StartsWith("/p/")) { ctx.Response.StatusCode = 404; return; }
        await ServePreview(ctx, path);
        return;
    }
    if (path.StartsWith("/api/"))
    {
        var supplied = ctx.Request.Headers["X-Central-Token"].FirstOrDefault() ?? ctx.Request.Query["t"].FirstOrDefault();
        if (supplied != token) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Sessão expirada. Recarregue a página." }); return; }
        try { await next(); }
        catch (UserError e)
        {
            if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = e.Message }); }
        }
        catch (JsonException)
        {
            if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "Dados inválidos." }); }
        }
        return;
    }
    await next();
});

string indexHtml() => File.ReadAllText(Path.Combine(app.Environment.WebRootPath, "index.html"))
    .Replace("__TOKEN__", token).Replace("__PREVIEW__", $"http://127.0.0.1:{previewPort}");
app.MapGet("/", () => Results.Content(indexHtml(), "text/html; charset=utf-8"));
app.MapGet("/build", () => Results.Text($"{version}/{instance}"));
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

var api = app.MapGroup("/api");
var antigravity = app.Services.GetRequiredService<AntigravityPoolService>();
var accountsPool = api.MapGroup("/antigravity");
accountsPool.MapGet("/status", (CancellationToken ct) => antigravity.Status(ct));
accountsPool.MapPost("/start", async (CancellationToken ct) =>
{
    await antigravity.Start(ct);
    return await antigravity.Status(ct);
});
accountsPool.MapPost("/add", (CancellationToken ct) => antigravity.Add(ct));
accountsPool.MapPost("/cancel", (CancellationToken ct) => antigravity.Cancel(ct));
accountsPool.MapPost("/refresh", (CancellationToken ct) => antigravity.Refresh(ct));
accountsPool.MapPost("/test-limit", (CancellationToken ct) => antigravity.NextLimit(ct));
accountsPool.MapPost("/threshold", (JsonObject body, CancellationToken ct) => antigravity.SetThreshold(I(body, "percent", -1), ct));
accountsPool.MapPost("/projects/{pid}/launch", (string pid, JsonObject body, CancellationToken ct) =>
    antigravity.Launch(pid, S(body, "mode"), (body["accounts"] as JsonArray ?? [])
        .Select(id => id?.GetValue<string>() ?? "").ToArray(), B(body, "resume"), ct));
var settings = app.Services.GetRequiredService<SettingsService>();
var agent = app.Services.GetRequiredService<AgentService>();
var continuity = app.Services.GetRequiredService<Continuity>();
var flow = app.Services.GetRequiredService<FlowService>();
var hub = app.Services.GetRequiredService<EventHub>();
var router = app.Services.GetRequiredService<LlmRouter>();

static string S(JsonObject? o, string k) { try { return (string?)o?[k] ?? ""; } catch { return o?[k]?.ToString() ?? ""; } }
static int I(JsonObject? o, string k, int d) { try { return o?[k]?.GetValue<int>() ?? d; } catch { return int.TryParse(S(o, k), out var v) ? v : d; } }
static double D(JsonObject? o, string k, double d) { try { return o?[k]?.GetValue<double>() ?? d; } catch { return double.TryParse(S(o, k).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d; } }
static bool B(JsonObject? o, string k) { try { return o?[k]?.GetValue<bool>() ?? false; } catch { return false; } }

Project NeedProject(string pid) => store.GetProject(pid) ?? throw new UserError("Projeto não encontrado.");
ChatThread NeedThread(string pid, string tid) => store.GetThread(pid, tid) ?? throw new UserError("Conversa não encontrada.");
Storyboard NeedFlow(string pid, string fid) => store.GetFlow(pid, fid) ?? throw new UserError("Storyboard não encontrado.");

object ThreadSummary(ChatThread t)
{
    lock (t)
    {
        var last = t.Messages.LastOrDefault(m => m.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(m.Content));
        return new { t.Id, t.Title, t.Mode, t.Updated, running = continuity.IsRunning(t), preview = Titles.Trunc(last?.Content, 90), changes = t.Changes.Select(c => c.Path).Distinct().Count() };
    }
}

object FlowSummary(Storyboard f)
{
    lock (f)
    {
        var scenes = f.AllScenes().ToList();
        var thumb = scenes.Select(s => s.Image).FirstOrDefault(i => i != null);
        return new { f.Id, f.Title, f.Format, f.Status, f.Updated, scenes = scenes.Count, videos = scenes.Count(s => s.Video != null), thumb, episodes = f.EpisodeList.Count };
    }
}

// ---------- Estado geral & Pool ----------

api.MapGet("/state", () => new
{
    version,
    laboratorio = Continuity.Lab,
    dataDir = store.Root,
    ffmpeg = FlowService.Tool("ffmpeg") != null,
    projects = store.Projects(),
    settings = settings.Snapshot(),
    cooldowns = router.Status(),
    pool = router.PoolSnapshot(),
});

api.MapGet("/pool", () => new { cooldowns = router.Status(), pool = router.PoolSnapshot() });
api.MapPost("/pool/reset-cooldown", (JsonObject? body) =>
{
    var pid = S(body, "providerId");
    router.ClearCooldown(pid.Length > 0 ? pid : null);
    return new { cooldowns = router.Status(), pool = router.PoolSnapshot() };
});

api.MapGet("/recent", () =>
{
    var items = new List<object>();
    foreach (var p in store.Projects().Take(20))
    {
        foreach (var t in store.Threads(p.Id)) { lock (t) items.Add(new { kind = "thread", projectId = p.Id, project = p.Name, t.Id, t.Title, t.Mode, t.Updated, running = continuity.IsRunning(t) }); }
        foreach (var f in store.Flows(p.Id)) { lock (f) items.Add(new { kind = "flow", projectId = p.Id, project = p.Name, f.Id, f.Title, Mode = f.Format, f.Updated, running = f.Status == "roteirizando" || f.Rendering != null }); }
    }
    return items.OrderByDescending(i => (DateTime)i.GetType().GetProperty("Updated")!.GetValue(i)!).Take(12);
});

// ---------- Projetos & Git ----------

api.MapPost("/projects", (JsonObject body) =>
    store.CreateProject(S(body, "name"), S(body, "kind") is var k && k.Length > 0 ? k : "geral", S(body, "customPath")));
api.MapPatch("/projects/{pid}", (string pid, JsonObject body) =>
{
    if (body.ContainsKey("customPath") && continuity.IsRunning(pid))
        throw new UserError("Pare a etapa em execução antes de mudar a pasta do projeto.");
    store.UpdateProject(pid, S(body, "name"), S(body, "customPath"), body.ContainsKey("customPath"));
    return NeedProject(pid);
});
api.MapDelete("/projects/{pid}", (string pid) =>
{
    if (continuity.IsRunning(pid)) throw new UserError("Pare a IA antes de apagar o projeto.");
    if (store.Flows(pid).Any(flow.HasPendingVideo)) throw new UserError("O projeto contém operações de vídeo pendentes.");
    store.TrashProject(pid);
    return Results.Ok(new { ok = true });
});
api.MapGet("/projects/{pid}", (string pid) =>
{
    var p = NeedProject(pid);
    return new
    {
        project = p,
        workspace = store.Workspace(pid),
        threads = store.Threads(pid).OrderByDescending(t => t.Updated).Select(ThreadSummary),
        flows = store.Flows(pid).OrderByDescending(f => f.Updated).Select(FlowSummary),
    };
});
api.MapGet("/projects/{pid}/files", (string pid) =>
{
    NeedProject(pid);
    return Store.ListFiles(store.Workspace(pid), 1500).Select(f => new { path = f.Path, size = f.Size });
});
api.MapGet("/projects/{pid}/file", (string pid, string path) =>
{
    NeedProject(pid);
    var full = Store.Inside(store.Workspace(pid), path);
    if (!File.Exists(full)) throw new UserError("Arquivo não encontrado.");
    var info = new FileInfo(full);
    if (info.Length > 2_000_000) return Results.Ok(new { path, binary = true, size = info.Length });
    var bytes = File.ReadAllBytes(full);
    var binary = bytes.Take(8000).Any(b => b == 0);
    return Results.Ok(new { path, binary, size = info.Length, content = binary ? null : Encoding.UTF8.GetString(bytes) });
});
api.MapGet("/projects/{pid}/raw", (string pid, string path) =>
{
    NeedProject(pid);
    var full = Store.Inside(store.Workspace(pid), path);
    if (!File.Exists(full)) return Results.NotFound();
    contentTypes.TryGetContentType(full, out var ct);
    return Results.File(full, ct ?? "application/octet-stream", Path.GetFileName(full));
});
api.MapGet("/projects/{pid}/download", (string pid) =>
{
    var p = NeedProject(pid);
    var ms = new MemoryStream();
    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
        var ws = store.Workspace(pid);
        foreach (var f in Store.ListFiles(ws, 20000)) zip.CreateEntryFromFile(Path.Combine(ws, f.Path), f.Path);
    }
    ms.Position = 0;
    var name = new string(p.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()) + ".zip";
    return Results.File(ms, "application/zip", name);
});
api.MapPost("/projects/{pid}/open-folder", (string pid, JsonObject? body) =>
{
    NeedProject(pid);
    var target = S(body, "flow") is var fid && fid.Length > 0 ? store.FlowDir(pid, fid) : store.Workspace(pid);
    Directory.CreateDirectory(target);
    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    return Results.Ok(new { ok = true });
});

api.MapGet("/projects/{pid}/git", async (string pid) =>
{
    NeedProject(pid);
    var ws = store.Workspace(pid);
    var check = await AgentService.Git(ws, "rev-parse", "--is-inside-work-tree");
    if (check.Code != 0) return Results.Ok(new { isRepo = false, workspace = ws });
    var branch = (await AgentService.Git(ws, "branch", "--show-current")).Out.Trim();
    var status = (await AgentService.Git(ws, "status", "--porcelain")).Out.TrimEnd();
    var logOut = (await AgentService.Git(ws, "log", "-n", "8", "--oneline")).Out.TrimEnd();
    return Results.Ok(new
    {
        isRepo = true,
        workspace = ws,
        branch = branch.Length > 0 ? branch : "HEAD",
        changes = status.Length == 0 ? Array.Empty<string>() : status.Split('\n').Select(x => x.TrimEnd('\r')).ToArray(),
        commits = logOut.Length == 0 ? Array.Empty<string>() : logOut.Split('\n').Select(x => x.TrimEnd('\r')).ToArray(),
    });
});
api.MapPost("/projects/{pid}/git/init", async (string pid) =>
{
    NeedProject(pid);
    var ws = store.Workspace(pid);
    var r = await AgentService.Git(ws, "init");
    if (r.Code != 0) throw new UserError("Git init falhou: " + r.Err);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/git/commit", async (string pid, JsonObject body) =>
{
    NeedProject(pid);
    var msg = S(body, "message").Trim();
    if (msg.Length == 0) throw new UserError("Escreva uma mensagem de commit.");
    var ws = store.Workspace(pid);
    var add = await AgentService.Git(ws, "add", "-A");
    if (add.Code != 0) throw new UserError("git add falhou: " + add.Err);
    var commit = await AgentService.Git(ws, "-c", "user.name=Central IA", "-c", "user.email=central@local", "commit", "-m", msg);
    if (commit.Code != 0) throw new UserError("git commit falhou: " + (commit.Err.Length > 0 ? commit.Err : commit.Out));
    return Results.Ok(new { ok = true, output = commit.Out.Trim() });
});

// ---------- Conversas / Agente / Equipe ----------

api.MapPost("/projects/{pid}/threads", (string pid, JsonObject body) =>
{
    NeedProject(pid);
    var text = S(body, "text").Trim();
    var rawMode = S(body, "mode");
    var mode = rawMode is "conversa" or "equipe" ? rawMode : "agente";
    var execution = S(body, "execution");
    var selected = (body["accounts"] as JsonArray ?? []).Select(id => id?.GetValue<string>() ?? "").ToArray();
    ReviewedWork? reviewed = null;
    if (execution.Length > 0)
    {
        if (mode != "agente" || text.Length == 0) throw new UserError("Escolha Programação e escreva um pedido.");
        if (execution == "revisao")
        {
            if (!B(body, "confirmApis")) throw new UserError("Confirme o uso das conexões selecionadas.");
            var routes = (body["routes"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(entry => new RouteEntry { ProviderId = S(entry, "providerId"), Model = S(entry, "model") });
            reviewed = agent.CreateReviewed(pid, routes);
        }
        else continuity.Validate(execution, selected);
    }
    var t = store.CreateThread(pid, text.Length > 0 ? Titles.FromPrompt(text) : "Nova conversa", mode);
    try
    {
        if (reviewed != null) store.Mutate(t, th => th.ReviewedWork = reviewed);
        else if (execution.Length > 0) continuity.Configure(t, execution, selected);
        if (text.Length > 0) agent.Start(t, text);
    }
    catch { if (!continuity.IsRunning(t) && t.Messages.Count == 0) store.TrashThread(pid, t.Id); throw; }
    return ThreadSummary(t);
});
api.MapGet("/projects/{pid}/threads/{tid}", (string pid, string tid) =>
{
    var t = NeedThread(pid, tid);
    return Results.Content($"{{\"thread\":{store.Serialize(t)},\"live\":{JsonSerializer.Serialize(agent.LiveState(tid), Store.Json)}}}", "application/json");
});
api.MapPatch("/projects/{pid}/threads/{tid}", (string pid, string tid, JsonObject body) =>
{
    var t = NeedThread(pid, tid);
    store.Mutate(t, th =>
    {
        if (S(body, "title") is var title && title.Length > 0) th.Title = Store.Clean(title, 100, th.Title);
        if ((th.Continuation != null || th.ReviewedWork != null) && (body.ContainsKey("mode") || body.ContainsKey("pinnedProviderId") || body.ContainsKey("pinnedModel")))
            throw new UserError("As contas deste trabalho foram selecionadas no pedido inicial.");
        if (S(body, "mode") is "agente" or "equipe" or "conversa") th.Mode = S(body, "mode");
        if (body.ContainsKey("pinnedProviderId")) th.PinnedProviderId = S(body, "pinnedProviderId") is var pp && pp.Length > 0 ? pp : null;
        if (body.ContainsKey("pinnedModel")) th.PinnedModel = S(body, "pinnedModel") is var pm && pm.Length > 0 ? pm : null;
    });
    return ThreadSummary(t);
});
api.MapDelete("/projects/{pid}/threads/{tid}", (string pid, string tid) =>
{
    if (continuity.IsRunning(NeedThread(pid, tid))) throw new UserError("Pare a execução antes de apagar o trabalho.");
    store.TrashThread(pid, tid);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/threads/{tid}/messages", (string pid, string tid, JsonObject body) =>
{
    var t = NeedThread(pid, tid);
    var text = S(body, "text");
    if (text.Trim().Length == 0) throw new UserError("Escreva uma mensagem.");
    if (B(body, "ask")) agent.AskReviewed(t, text.Trim());
    else agent.Start(t, text);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/threads/{tid}/stop", (string pid, string tid) =>
    Results.Ok(new { ok = continuity.Stop(NeedThread(pid, tid)) || agent.Stop(tid) }));
api.MapGet("/projects/{pid}/threads/{tid}/checkpoint", (string pid, string tid) => continuity.Package(NeedThread(pid, tid)));
api.MapGet("/projects/{pid}/threads/{tid}/inspect", (string pid, string tid) => continuity.Inspect(NeedThread(pid, tid)));
api.MapPost("/projects/{pid}/threads/{tid}/resume", (string pid, string tid) =>
{ continuity.Resume(NeedThread(pid, tid)); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/threads/{tid}/receipt", (string pid, string tid, JsonObject body) =>
{
    continuity.RecordAssisted(NeedThread(pid, tid), S(body, "tests"), S(body, "next"), S(body, "profile"), B(body, "done"));
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/threads/{tid}/approval", (string pid, string tid, JsonObject body) =>
    Results.Ok(new { ok = agent.Decide(tid, S(body, "id"), B(body, "approve")) }));
api.MapPost("/projects/{pid}/threads/{tid}/plan/approve", (string pid, string tid, JsonObject body) =>
{ agent.ApproveReviewed(NeedThread(pid, tid), S(body, "id")); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/threads/{tid}/plan/resume", (string pid, string tid) =>
{ agent.ResumeReviewed(NeedThread(pid, tid)); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/threads/{tid}/plan/reconcile", (string pid, string tid, JsonObject body) =>
{ agent.ReconcileReviewed(NeedThread(pid, tid), S(body, "note")); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/threads/{tid}/revert", (string pid, string tid, JsonObject body) =>
{
    var t = NeedThread(pid, tid);
    if (continuity.IsRunning(pid)) throw new UserError("Pare a IA antes de desfazer alterações.");
    var path = S(body, "path");
    FileChange? first;
    lock (t) first = t.Changes.FirstOrDefault(c => c.Path == path);
    if (first == null) throw new UserError("Esse arquivo não foi alterado nesta conversa.");
    var full = Store.Inside(store.Workspace(pid), path);
    if (first.Before == null) { if (File.Exists(full)) File.Delete(full); }
    else { Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, first.Before, new UTF8Encoding(false)); }
    store.Mutate(t, th =>
    {
        th.Changes.RemoveAll(c => c.Path == path);
        th.Messages.Add(new ChatMessage { Id = Store.NewId(), Role = "notice", ToolName = "info", Content = $"Você desfez as alterações em {path}.", Time = DateTime.UtcNow });
    });
    hub.Publish(tid, new { type = "files", path });
    return Results.Ok(new { ok = true });
});
api.MapGet("/projects/{pid}/threads/{tid}/events", (HttpContext ctx, string pid, string tid) => Sse(ctx, tid));

// ---------- Contas, Modelos, Descoberta e Importação em Lote ----------

api.MapGet("/discover", (CancellationToken ct) => CliRunner.DiscoverAsync(settings, ct));

api.MapPost("/google-accounts", (JsonObject body) =>
{
    var name = S(body, "name").Trim();
    var identity = S(body, "identity").Trim().ToLowerInvariant();
    var fixture = B(body, "fixture");
    var key = S(body, "key");
    if (name.Length is < 1 or > 60 || identity.Length is < 3 or > 254 || identity.Any(char.IsWhiteSpace))
        throw new UserError("Informe um nome e uma identidade para o perfil.");
    if (fixture && (!Continuity.Lab || !identity.StartsWith("ensaio-", StringComparison.Ordinal) || key.Length == 0))
        throw new UserError("Perfil de ensaio indisponível.");
    if (!fixture && (!System.Net.Mail.MailAddress.TryCreate(identity, out var address) || address.Address != identity || key.Length != 0))
        throw new UserError("Informe o e-mail Google. A autenticação é feita no cliente oficial.");
    var p = new ProviderConfig
    {
        Id = Store.NewId(), Name = name, DeclaredIdentity = identity, Kind = fixture ? "ensaio" : "google-personal",
        BaseUrl = fixture ? Continuity.LabEndpoint!.ToString() : "https://antigravity.google/", Enabled = true
    };
    settings.Update(s =>
    {
        if (s.Providers.Any(q => q.Kind == p.Kind && q.DeclaredIdentity == identity))
            throw new UserError("Já existe um perfil para essa identidade.");
        s.Providers.Add(p);
    });
    if (fixture) settings.SetKey(p.Id, key);
    return new { profile = p, settings = settings.Snapshot() };
});

api.MapPost("/providers/batch", async (JsonObject body, CancellationToken ct) =>
{
    var kind = S(body, "kind") is var k && k.Length > 0 ? k : "gemini";
    if (kind is "google-personal" or "ensaio" or "cli") throw new UserError("Use o cadastro específico desse tipo de conexão.");
    var baseUrl = S(body, "baseUrl").Trim().TrimEnd('/');
    var defaultModel = S(body, "defaultModel").Trim();
    var prefix = S(body, "prefix") is var pr && pr.Length > 0 ? pr.Trim() : "Conta";
    var rawKeys = (body["keys"] as JsonArray ?? new()).Select(x => ((string?)x ?? "").Trim()).Where(x => x.Length > 0).ToList();
    if (rawKeys.Count == 0) throw new UserError("Cole ao menos uma chave (uma por linha).");
    if (baseUrl.Length == 0) throw new UserError("Endereço da API ausente.");
    var doTest = !body.ContainsKey("test") || B(body, "test");

    var startIndex = settings.Current.Providers.Count(p => p.Kind == kind);
    var created = new List<(ProviderConfig P, string Key)>();

    foreach (var raw in rawKeys)
    {
        string name;
        string key;
        var eq = raw.IndexOf('=');
        if (eq > 0 && eq < 36 && !raw[..eq].Contains(' ')) { name = raw[..eq].Trim(); key = raw[(eq + 1)..].Trim(); }
        else { startIndex++; name = $"{prefix} {startIndex}"; key = raw; }
        if (key.Length == 0) continue;

        ProviderConfig? p = null;
        settings.Update(s =>
        {
            p = new ProviderConfig
            {
                Id = Store.NewId(), Name = Store.Clean(name, 60, prefix), Kind = kind,
                BaseUrl = baseUrl, DefaultModel = defaultModel, Enabled = true,
            };
            s.Providers.Add(p);
            if (defaultModel.Length > 0)
            {
                foreach (var role in SettingsService.Roles)
                    s.Chains[role].Add(new RouteEntry { ProviderId = p.Id, Model = defaultModel });
            }
            if (kind == "gemini" && s.Media.ProviderId == null) s.Media.ProviderId = p.Id;
        });
        settings.SetKey(p!.Id, key);
        created.Add((p!, key));
    }

    var results = new List<object>();
    if (doTest && defaultModel.Length > 0)
    {
        var tasks = created.Select(async item =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = await LlmClient.ChatAsync(item.P, item.Key, defaultModel,
                    new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Responda só: OK" } }, null, null, ct);
                return (object)new { id = item.P.Id, name = item.P.Name, ok = true, ms = sw.ElapsedMilliseconds, reply = Titles.Trunc(r.Content.Trim(), 60) };
            }
            catch (LlmException e)
            {
                return (object)new { id = item.P.Id, name = item.P.Name, ok = false, ms = sw.ElapsedMilliseconds, reply = e.Message };
            }
        });
        results.AddRange(await Task.WhenAll(tasks));
    }
    else
    {
        foreach (var item in created) results.Add(new { id = item.P.Id, name = item.P.Name, ok = true, ms = 0, reply = "Adicionada" });
    }

    return new { added = created.Count, results, settings = settings.Snapshot(), pool = router.PoolSnapshot() };
});

api.MapPost("/providers/import-env", (JsonObject body) =>
{
    var envVar = S(body, "envVar").Trim();
    var allowed = new Dictionary<string, (string Name, string Kind, string BaseUrl, string Model)>(StringComparer.OrdinalIgnoreCase)
    {
        ["GEMINI_API_KEY"] = ("Gemini (Env)", "gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash"),
        ["GOOGLE_API_KEY"] = ("Google AI (Env)", "gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash"),
        ["OPENROUTER_API_KEY"] = ("OpenRouter (Env)", "openrouter", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4"),
        ["OPENAI_API_KEY"] = ("OpenAI (Env)", "openai", "https://api.openai.com/v1", "gpt-4.1"),
        ["GROQ_API_KEY"] = ("Groq (Env)", "groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
        ["DEEPSEEK_API_KEY"] = ("DeepSeek (Env)", "deepseek", "https://api.deepseek.com/v1", "deepseek-chat"),
    };
    if (!allowed.TryGetValue(envVar, out var def)) throw new UserError("Variável não suportada.");
    var val = Environment.GetEnvironmentVariable(envVar);
    if (string.IsNullOrWhiteSpace(val)) throw new UserError($"Variável {envVar} não encontrada.");
    ProviderConfig? p = null;
    settings.Update(s =>
    {
        p = new ProviderConfig { Id = Store.NewId(), Name = def.Name, Kind = def.Kind, BaseUrl = def.BaseUrl, DefaultModel = def.Model, Enabled = true };
        s.Providers.Add(p);
        foreach (var role in SettingsService.Roles) s.Chains[role].Add(new RouteEntry { ProviderId = p.Id, Model = def.Model });
        if (def.Kind == "gemini" && s.Media.ProviderId == null) s.Media.ProviderId = p.Id;
    });
    settings.SetKey(p!.Id, val.Trim());
    return settings.Snapshot();
});

api.MapGet("/settings", () => settings.Snapshot());
api.MapPost("/providers", (JsonObject body) =>
{
    var id = S(body, "id");
    ProviderConfig? saved = null;
    settings.Update(s =>
    {
        var p = id.Length > 0 ? s.Providers.FirstOrDefault(x => x.Id == id) : null;
        if (p?.Kind is "google-personal" or "ensaio")
        {
            if (body.Any(item => item.Key is not ("id" or "enabled"))) throw new UserError("Use o cadastro Google para alterar perfis.");
            if (body.ContainsKey("enabled")) p.Enabled = B(body, "enabled");
            saved = p;
            return;
        }
        var isNew = p == null;
        p ??= new ProviderConfig { Id = Store.NewId() };
        p.Name = Store.Clean(S(body, "name"), 60, p.Name.Length > 0 ? p.Name : "Conexão");
        if (S(body, "kind") is var kind && kind.Length > 0)
        {
            if (kind is "google-personal" or "ensaio") throw new UserError("Use Adicionar conta Google.");
            p.Kind = kind;
        }
        var url = S(body, "baseUrl").Trim();
        if (url.Length > 0)
        {
            if (p.Kind == "cli")
            {
                if (!url.StartsWith("cli://", StringComparison.OrdinalIgnoreCase)) throw new UserError("Identificador de CLI inválido.");
                p.BaseUrl = url.ToLowerInvariant();
                if (p.BaseUrl is "cli://agy" or "cli://gemini") throw new UserError("Use o cliente oficial para contas Google pessoais.");
            }
            else
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) throw new UserError("Endereço da API inválido.");
                p.BaseUrl = url.TrimEnd('/');
            }
        }
        if (p.BaseUrl.Length == 0) throw new UserError("Informe o endereço da API.");
        if (body.ContainsKey("defaultModel")) p.DefaultModel = S(body, "defaultModel").Trim();
        if (p.Kind == "cli" && p.DefaultModel.Length == 0) p.DefaultModel = "auto";
        if (body.ContainsKey("enabled")) p.Enabled = B(body, "enabled");
        if (body["models"] is JsonArray ms) p.Models = ms.Select(m => (string?)m).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!).Distinct().ToList();
        if (isNew) s.Providers.Add(p);
        if (isNew && p.DefaultModel.Length > 0)
        {
            var roles = p.Kind == "cli" ? new[] { "codigo" } : SettingsService.Roles;
            foreach (var role in roles)
                if (s.Chains.TryGetValue(role, out var chain) && !chain.Any(x => x.ProviderId == p.Id))
                    chain.Add(new RouteEntry { ProviderId = p.Id, Model = p.DefaultModel });
        }
        if (isNew && p.Kind == "gemini" && s.Media.ProviderId == null) s.Media.ProviderId = p.Id;
        saved = p;
    });
    if (body.ContainsKey("apiKey") && S(body, "apiKey").Trim() is var key && key.Length > 0) settings.SetKey(saved!.Id, key);
    return settings.Snapshot();
});
api.MapDelete("/providers/{id}", (string id) =>
{
    settings.Update(s =>
    {
        s.Providers.RemoveAll(p => p.Id == id);
        if (s.Media.ProviderId == id) s.Media.ProviderId = null;
    });
    settings.SetKey(id, null);
    router.ClearCooldown(id);
    return settings.Snapshot();
});
api.MapPost("/providers/{id}/models", async (string id, CancellationToken ct) =>
{
    var p = settings.Current.Providers.FirstOrDefault(x => x.Id == id) ?? throw new UserError("Conexão não encontrada.");
    if (!SettingsService.CanRoute(p)) throw new UserError("Essa conexão não expõe modelos verificáveis pela Central.");
    var models = await LlmClient.FetchModels(p, settings.GetKey(id), ct);
    if (models.Count == 0) throw new UserError("O serviço respondeu, mas não listou modelos.");
    settings.Update(s => { s.Providers.First(q => q.Id == id).Models = models; });
    return settings.Snapshot();
});
api.MapPost("/providers/{id}/test", async (string id, JsonObject? body, CancellationToken ct) =>
{
    var p = settings.Current.Providers.FirstOrDefault(x => x.Id == id) ?? throw new UserError("Conexão não encontrada.");
    if (!SettingsService.CanRoute(p)) throw new UserError("Essa conexão não atende pelo pool de modelos da Central.");
    if (p.Kind == "cli")
    {
        var (ok, reply, ms) = await CliRunner.TestCliAsync(p, ct);
        return Results.Ok(new { ok, reply, ms });
    }
    var model = S(body, "model") is var m && m.Length > 0 ? m : p.DefaultModel;
    if (model.Length == 0) throw new UserError("Escolha um modelo para testar.");
    var sw = Stopwatch.StartNew();
    try
    {
        var r = await LlmClient.ChatAsync(p, settings.GetKey(id), model,
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Responda só: OK, funcionando." } }, null, null, ct);
        return Results.Ok(new { ok = true, reply = Titles.Trunc(r.Content.Trim(), 120), ms = sw.ElapsedMilliseconds });
    }
    catch (LlmException e) { return Results.Ok(new { ok = false, reply = e.Message, ms = sw.ElapsedMilliseconds }); }
});
api.MapPut("/chains/{role}", (string role, JsonArray body) =>
{
    if (!SettingsService.Roles.Contains(role)) throw new UserError("Função desconhecida.");
    var list = body.OfType<JsonObject>().Select(o => new RouteEntry { ProviderId = S(o, "providerId"), Model = S(o, "model").Trim() }).ToList();
    settings.Update(s => s.Chains[role] = list);
    return settings.Snapshot();
});
api.MapPut("/strategies/{role}", (string role, JsonObject body) =>
{
    if (!SettingsService.StrategyRoles.Contains(role)) throw new UserError("Função desconhecida.");
    var strategy = S(body, "strategy");
    if (strategy is not ("failover" or "roundrobin" or "leastused")) throw new UserError("Estratégia inválida.");
    settings.Update(s => s.Strategies[role] = strategy);
    return settings.Snapshot();
});
api.MapPut("/settings/media", (JsonObject body) =>
{
    settings.Update(s =>
    {
        var m = s.Media;
        if (body.ContainsKey("providerId")) m.ProviderId = S(body, "providerId") is var pid && pid.Length > 0 ? pid : null;
        if (S(body, "imageModel") is var im && im.Length > 0) m.ImageModel = im.Trim();
        if (S(body, "videoModel") is var vm && vm.Length > 0) m.VideoModel = vm.Trim();
        if (S(body, "ttsModel") is var tm && tm.Length > 0) m.TtsModel = tm.Trim();
        if (S(body, "voice") is var v && v.Length > 0) m.Voice = v.Trim();
        if (body.ContainsKey("resolution")) m.Resolution = S(body, "resolution").Trim();
        if (body.ContainsKey("videoUsesFrame")) m.VideoUsesFrame = B(body, "videoUsesFrame");
        if (S(body, "videoCostPerSecond") is var c && decimal.TryParse(c.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var cost)) m.VideoCostPerSecond = Math.Max(0, cost);
        if (S(body, "currency") is var cur && cur.Length > 0) m.Currency = cur.Trim();
    });
    return settings.Snapshot();
});
api.MapPut("/settings/approval", (JsonObject body) =>
{
    settings.Update(s => s.Approval = S(body, "approval") == "automatico" ? "automatico" : "perguntar");
    return settings.Snapshot();
});

// ---------- Flow v2 ----------

api.MapPost("/projects/{pid}/flows", (string pid, JsonObject body) =>
{
    var o = new FlowOptions
    {
        Format = S(body, "format"), Episodes = I(body, "episodes", 3), Scenes = I(body, "scenes", 3),
        Seconds = I(body, "seconds", 8), Aspect = S(body, "aspect"), Style = S(body, "style"),
    };
    return FlowSummary(flow.Create(pid, S(body, "prompt"), o));
});
api.MapGet("/projects/{pid}/flows/{fid}", (string pid, string fid) =>
{
    var f = NeedFlow(pid, fid);
    return Results.Content(store.SerializeFlow(f), "application/json");
});
api.MapDelete("/projects/{pid}/flows/{fid}", (string pid, string fid) =>
{
    var f = NeedFlow(pid, fid);
    lock (f)
    {
        if (flow.HasPendingVideo(f)) throw new UserError("O storyboard contém uma geração de vídeo pendente.");
        store.TrashFlow(pid, fid);
    }
    return Results.Ok(new { ok = true });
});
api.MapPatch("/projects/{pid}/flows/{fid}", (string pid, string fid, JsonObject body) =>
{
    var f = NeedFlow(pid, fid);
    store.MutateFlow(f, x =>
    {
        if (S(body, "title") is var t && t.Length > 0) x.Title = Store.Clean(t, 120, x.Title);
        if (body.ContainsKey("style")) x.Style = S(body, "style");
        if (body.ContainsKey("logline")) x.Logline = S(body, "logline");
        if (body.ContainsKey("transitionSeconds")) x.TransitionSeconds = Math.Clamp(D(body, "transitionSeconds", x.TransitionSeconds), 0, 1.5);
        if (body.ContainsKey("musicVolume")) x.MusicVolume = Math.Clamp(D(body, "musicVolume", x.MusicVolume), 0.05, 1.0);
    });
    flow.Publish(f);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/flows/{fid}/rewrite", (string pid, string fid, JsonObject body) => { flow.Rewrite(NeedFlow(pid, fid), S(body, "feedback")); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/characters/image", (string pid, string fid, JsonObject body) => { flow.GenerateCharacterImage(NeedFlow(pid, fid), S(body, "name")); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/characters/import", async (string pid, string fid, HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var name = form["name"].ToString();
    var file = form.Files.FirstOrDefault() ?? throw new UserError("Escolha uma imagem.");
    await flow.ImportCharacterImage(NeedFlow(pid, fid), name, file);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/flows/{fid}/music", async (string pid, string fid, HttpRequest req) =>
{
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault() ?? throw new UserError("Escolha um arquivo de áudio.");
    await flow.UploadMusic(NeedFlow(pid, fid), file);
    return Results.Ok(new { ok = true });
});
api.MapDelete("/projects/{pid}/flows/{fid}/music", (string pid, string fid) => { flow.ClearMusic(NeedFlow(pid, fid)); return Results.Ok(new { ok = true }); });

api.MapPatch("/projects/{pid}/flows/{fid}/scenes/{sid}", (string pid, string fid, string sid, JsonObject body) => { flow.EditScene(NeedFlow(pid, fid), sid, body); return Results.Ok(new { ok = true }); });
api.MapDelete("/projects/{pid}/flows/{fid}/scenes/{sid}", (string pid, string fid, string sid) => { flow.RemoveScene(NeedFlow(pid, fid), sid); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/move", (string pid, string fid, string sid, JsonObject body) => { flow.MoveScene(NeedFlow(pid, fid), sid, I(body, "delta", 0)); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/episodes/{eid}/scenes", (string pid, string fid, string eid) => { flow.AddScene(NeedFlow(pid, fid), eid); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/image", (string pid, string fid, string sid) => { _ = flow.GenerateImageAsync(NeedFlow(pid, fid), sid); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/voice", (string pid, string fid, string sid) => { _ = flow.GenerateVoiceAsync(NeedFlow(pid, fid), sid); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/video", (string pid, string fid, string sid, JsonObject body) =>
{
    var f = NeedFlow(pid, fid);
    if (!B(body, "confirm")) throw new UserError("Confirme o custo antes de gerar o vídeo.");
    _ = flow.GenerateVideoAsync(f, sid, S(body, "providerId"));
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/resume-video", (string pid, string fid, string sid) =>
{ flow.ResumeVideo(NeedFlow(pid, fid), sid); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/import", async (string pid, string fid, string sid, HttpRequest req) =>
{
    var f = NeedFlow(pid, fid);
    var form = await req.ReadFormAsync();
    var file = form.Files.FirstOrDefault() ?? throw new UserError("Escolha um arquivo.");
    await flow.ImportMedia(f, sid, file);
    return Results.Ok(new { ok = true });
});
api.MapPost("/projects/{pid}/flows/{fid}/scenes/{sid}/takes/{tkid}/select", (string pid, string fid, string sid, string tkid) => { flow.SelectTake(NeedFlow(pid, fid), sid, tkid); return Results.Ok(new { ok = true }); });
api.MapDelete("/projects/{pid}/flows/{fid}/scenes/{sid}/takes/{tkid}", (string pid, string fid, string sid, string tkid) => { flow.DeleteTake(NeedFlow(pid, fid), sid, tkid); return Results.Ok(new { ok = true }); });
api.MapDelete("/projects/{pid}/flows/{fid}/scenes/{sid}/media/{kind}", (string pid, string fid, string sid, string kind) => { flow.ClearMedia(NeedFlow(pid, fid), sid, kind); return Results.Ok(new { ok = true }); });
api.MapPost("/projects/{pid}/flows/{fid}/batch", (string pid, string fid, JsonObject body) =>
{
    var f = NeedFlow(pid, fid);
    var what = S(body, "what");
    if (what == "video" && !B(body, "confirm")) throw new UserError("Confirme o custo antes de gerar os vídeos.");
    var count = flow.RunBatchParallel(f, what);
    return Results.Ok(new { ok = true, count });
});
api.MapPost("/projects/{pid}/flows/{fid}/render", (string pid, string fid, JsonObject body) => { flow.Assemble(NeedFlow(pid, fid), B(body, "subtitles")); return Results.Ok(new { ok = true }); });
api.MapGet("/projects/{pid}/flows/{fid}/media/{**path}", (string pid, string fid, string path) =>
{
    NeedFlow(pid, fid);
    var full = Store.Inside(store.FlowDir(pid, fid), path);
    if (!File.Exists(full)) return Results.NotFound();
    contentTypes.TryGetContentType(full, out var ct);
    return Results.File(full, ct ?? "application/octet-stream", enableRangeProcessing: true);
});
api.MapGet("/projects/{pid}/flows/{fid}/events", (HttpContext ctx, string pid, string fid) => Sse(ctx, "flow:" + fid));

flow.ResumePending();
continuity.Recover();
agent.RecoverReviewed();

app.Lifetime.ApplicationStarted.Register(() =>
{
    Console.WriteLine();
    Console.WriteLine("  ┌──────────────────────────────────────────────┐");
    Console.WriteLine($"  │  Central IA {version} em {mainUrl}");
    Console.WriteLine("  │  Mantenha esta janela aberta enquanto usa.   │");
    Console.WriteLine("  └──────────────────────────────────────────────┘");
    Console.WriteLine($"  Dados: {store.Root}");
    if (!noBrowser) OpenBrowser(mainUrl);
});

app.Run();

// ---------- Funções locais ----------

async Task Sse(HttpContext ctx, string topic)
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    var (id, reader) = hub.Subscribe(topic);
    try
    {
        await ctx.Response.WriteAsync(": ok\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            using var beat = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            beat.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                if (await reader.WaitToReadAsync(beat.Token))
                {
                    while (reader.TryRead(out var msg)) await ctx.Response.WriteAsync("data: " + msg + "\n\n", ctx.RequestAborted);
                }
                else break;
            }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                await ctx.Response.WriteAsync(": ping\n\n", ctx.RequestAborted);
            }
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    finally { hub.Unsubscribe(topic, id); }
}

async Task ServePreview(HttpContext ctx, string path)
{
    var parts = path["/p/".Length..].Split('/', 2);
    var pid = parts[0];
    var rel = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
    if (parts.Length == 1) { ctx.Response.Redirect($"/p/{pid}/"); return; }
    string full;
    try
    {
        if (store.GetProject(pid) == null) { ctx.Response.StatusCode = 404; return; }
        var ws = store.Workspace(pid);
        full = Store.Inside(ws, rel);
        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        if (!File.Exists(full) && rel.Length == 0)
        {
            var firstHtml = Store.ListFiles(ws, 500).Select(f => f.Path).FirstOrDefault(f => f.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                ?? Store.ListFiles(ws, 500).Select(f => f.Path).FirstOrDefault(f => f.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            if (firstHtml != null) { ctx.Response.Redirect($"/p/{pid}/{firstHtml}"); return; }
        }
    }
    catch (UserError) { ctx.Response.StatusCode = 400; return; }
    if (!File.Exists(full))
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync("<body style='font-family:Segoe UI;background:#0d0f14;color:#8b93a7;display:grid;place-items:center;height:90vh'><div>Nada para mostrar ainda. Peça à IA para criar um <b>index.html</b>.</div></body>");
        return;
    }
    contentTypes.TryGetContentType(full, out var ct);
    ctx.Response.ContentType = ct ?? "application/octet-stream";
    ctx.Response.Headers.CacheControl = "no-store";
    await ctx.Response.SendFileAsync(full);
}

static bool PortFree(int p)
{
    try { var l = new TcpListener(IPAddress.Loopback, p); l.Start(); l.Stop(); return true; }
    catch (SocketException) { return false; }
}

static void OpenBrowser(string url)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
}
