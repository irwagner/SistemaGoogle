using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Orbita.Core;
using Orbita.Engine;
using Orbita.Pool;
using Orbita.Studio;

const string version = OrbitaRelease.Version;
var port = int.TryParse(Environment.GetEnvironmentVariable("ORBITA_PORT"), out var configured) ? configured : 7784;
if (port is < 1024 or > 65535) { Console.Error.WriteLine("ORBITA_PORT deve estar entre 1024 e 65535."); return; }
var address = $"http://127.0.0.1:{port}";
const string desktopMcpPath = "/api/desktop-mcp";
var files = new StudioFiles(StudioFiles.ResolveRoot());
using var workspaceLock = LockStorage(files.Root);
if (workspaceLock == null) return;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.Limits.MaxRequestBodySize = null;
    options.AddServerHeader = false;
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddSingleton(files);
builder.Services.AddSingleton<ICheckpointStore>(files);
builder.Services.AddSingleton<IGitSnapshotService, GitSnapshotService>();
builder.Services.AddSingleton<IAgentEngine, AntigravityEngine>();
builder.Services.AddSingleton<IQuotaPolicy, QuotaPolicy>();
builder.Services.AddSingleton<AccountPool>();
builder.Services.AddSingleton(new WindowsDesktop(files, new Uri(address + desktopMcpPath)));
builder.Services.AddSingleton<MissionAttachments>();
builder.Services.AddSingleton<MissionRunner>();
var app = builder.Build();
var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var html = File.ReadAllText(Path.Combine(app.Environment.WebRootPath, "index.html"))
    .Replace("__ORBITA_TOKEN__", token, StringComparison.Ordinal);
app.Use(async (ctx, next) =>
{
    if (!string.Equals(ctx.Request.Host.Value, $"127.0.0.1:{port}", StringComparison.Ordinal))
    { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'";
    if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Path != new PathString(desktopMcpPath) && ctx.Request.Headers["X-Orbita-Token"] != token)
    { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "Sessão local expirada. Recarregue a página." }); return; }
    if (ctx.Request.Path == "/index.html") { ctx.Response.Redirect("/"); return; }
    try { await next(); }
    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
    catch (StudioException error) when (!ctx.Response.HasStarted)
    { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = error.Message }); }
});
app.UseStaticFiles();
app.MapGet("/", () => Results.Content(html, "text/html", Encoding.UTF8));
app.MapGet("/build", () => Results.Text(version));

var api = app.MapGroup("/api");
api.MapGet("/state", (StudioFiles store, ICheckpointStore cp, IAgentEngine engine, IGitSnapshotService git) => new
{
    name = "Orbita Studio", version, projects = store.Projects(), missions = cp.List(),
    tools = new { agy = engine.Available, git = git.Available, ffmpeg = Tool("ffmpeg", "CENTRAL_FFMPEG"), ffprobe = Tool("ffprobe", "CENTRAL_FFPROBE"), desktop = WindowsDesktop.Available }
});
app.MapPost(desktopMcpPath, (HttpContext http, WindowsDesktop desktop) => desktop.McpAsync(http));
api.MapPost("/projects", (StudioFiles store, CreateProject request) => store.CreateProject(request.Name, request.Workspace));
api.MapGet("/projects/{id}/context/{name}", (StudioFiles store, string id, string name) => new { text = store.ReadProjectFile(id, name) });
api.MapPut("/projects/{id}/context/{name}", (StudioFiles store, string id, string name, EditContext body) =>
{ store.WriteProjectFile(id, name, body.Text ?? ""); return Results.NoContent(); });
api.MapPost("/projects/{id}/missions", (MissionRunner service, string id, NewMission request, CancellationToken ct) => service.CreateAsync(id, request.Name, ct));
api.MapGet("/missions/{id}", (ICheckpointStore store, string id) => store.Get(id));
api.MapPost("/missions/{id}/open-app", (ICheckpointStore store, WindowsDesktop desktop, string id, OpenProjectApp input, CancellationToken ct)
    => desktop.OpenAsync(store.Get(id), input.Path, ct));
api.MapPost("/missions/{id}/turns", (MissionRunner service, string id, TurnInput body) => Results.Accepted(value: service.Start(id, body)));
api.MapPost("/missions/{id}/turns/attachments", async (HttpContext http, ICheckpointStore checkpoints,
    MissionRunner service, MissionAttachments attachments, string id, CancellationToken ct) =>
{
    _ = checkpoints.Get(id);
    if (!http.Request.HasFormContentType) throw new StudioException("Envie os anexos pelo formulário da conversa.");
    IFormCollection form;
    TurnInput turn;
    try
    {
        form = await http.Request.ReadFormAsync(new FormOptions
        {
            MultipartBodyLengthLimit = long.MaxValue,
            ValueCountLimit = MissionAttachments.MaxCount + 1,
            ValueLengthLimit = int.MaxValue
        }, ct);
        turn = JsonSerializer.Deserialize<TurnInput>(form["turn"].ToString(), StudioFiles.Json)
            ?? throw new JsonException();
    }
    catch (InvalidDataException) { throw new StudioException("Não foi possível receber os anexos. O limite é de 5 MB por arquivo e 10 MB por mensagem."); }
    catch (JsonException) { throw new StudioException("O texto do pedido veio em um formato inválido."); }
    if (form.Files.Count == 0) throw new StudioException("Selecione pelo menos um arquivo.");
    var saved = await attachments.SaveAsync(id, form.Files, ct);
    try { return Results.Accepted(value: service.Start(id, turn, uploaded: saved)); }
    catch (StudioException) { attachments.Discard(id, saved); throw; }
});
api.MapGet("/missions/{id}/attachments/{attachmentId}", (ICheckpointStore checkpoints, MissionAttachments attachments,
    string id, string attachmentId) =>
{
    _ = checkpoints.Get(id);
    var (attachment, path) = attachments.Get(id, attachmentId);
    return Results.File(path, attachment.MimeType, attachment.Name);
});
api.MapPost("/missions/{id}/resume", (MissionRunner service, string id, TurnInput body) => Results.Accepted(value: service.Start(id, body, resume: true)));
api.MapPost("/missions/{id}/cancel", (MissionRunner service, string id) => { service.Cancel(id); return Results.NoContent(); });
api.MapGet("/missions/{id}/checkpoints", (ICheckpointStore store, string id) => store.History(id));
api.MapGet("/missions/{id}/signal-activity", SignalActivity.Read);
api.MapGet("/missions/{id}/diff", (ICheckpointStore cp, IGitSnapshotService git, string id, CancellationToken ct) => git.DiffAsync(cp.Get(id), ct));
api.MapPost("/missions/{id}/accept", (MissionRunner service, string id, ReviewDiff request, CancellationToken ct) => service.ReviewAsync(id, false, request.Revision, ct));
api.MapPost("/missions/{id}/revert", (MissionRunner service, string id, ReviewDiff request, CancellationToken ct) => service.ReviewAsync(id, true, request.Revision, ct));
api.MapGet("/missions/{id}/events", async (string id, long? after, HttpContext ctx, ICheckpointStore store, CancellationToken ct) =>
{
    _ = store.Get(id);
    ctx.Response.ContentType = "text/event-stream";
    var sequence = Math.Max(after ?? 0, 0);
    await ctx.Response.StartAsync(ct);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(350));
    var heartbeat = DateTimeOffset.UtcNow;
    do
    {
        foreach (var entry in store.EventsAfter(id, sequence))
        {
            await ctx.Response.WriteAsync($"id: {entry.Sequence}\ndata: {JsonSerializer.Serialize(entry, StudioFiles.Json)}\n\n", ct);
            sequence = entry.Sequence;
        }
        if (DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(10))
        { await ctx.Response.WriteAsync(": orbita\n\n", ct); heartbeat = DateTimeOffset.UtcNow; }
        await ctx.Response.Body.FlushAsync(ct);
    } while (await timer.WaitForNextTickAsync(ct));
});

api.MapGet("/pool", (AccountPool pool, CancellationToken ct) => pool.StatusAsync(ct));
api.MapPost("/pool/start", async (AccountPool pool, CancellationToken ct) => { await pool.StartAsync(ct); return await pool.StatusAsync(ct); });
api.MapPost("/pool/accounts", async (AccountPool pool, CancellationToken ct) => { await pool.AddAsync(ct); return await pool.StatusAsync(ct); });
api.MapPost("/pool/cancel", async (AccountPool pool, CancellationToken ct) => { await pool.CancelLoginAsync(ct); return await pool.StatusAsync(ct); });
api.MapPost("/pool/refresh", async (AccountPool pool, CancellationToken ct) => { await pool.RefreshAsync(ct); return await pool.StatusAsync(ct); });
api.MapPost("/pool/test", async (AccountPool pool, RotationTest input, CancellationToken ct) =>
{
    await pool.StartAsync(ct);
    return await pool.TestRotationAsync(input.Accounts, input.Model, ct);
});
api.MapPost("/pool/verification", (AccountPool pool, VerifyAccount input, CancellationToken ct) =>
    pool.VerificationAsync(input.Id, ct));
api.MapPut("/pool/reserve", async (AccountPool pool, SetReserve input, CancellationToken ct) =>
{
    if (input.Percent is < 0 or > 100) throw new StudioException("A margem deve estar entre 0 e 100%.");
    await pool.ThresholdAsync(input.Percent, ct);
    return await pool.StatusAsync(ct);
});

var runner = app.Services.GetRequiredService<MissionRunner>();
runner.Recover();
await app.StartAsync();
Console.WriteLine($"Orbita Studio v{version} em {address}");
if (!args.Contains("--sem-navegador") && Environment.GetEnvironmentVariable("ORBITA_NO_BROWSER") != "1")
{
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(address) { UseShellExecute = true }); }
    catch (System.ComponentModel.Win32Exception) { }
}
await app.WaitForShutdownAsync();
await runner.DrainAsync();
await app.DisposeAsync();

static bool Tool(string exe, string setting) => (LocalTools.Configured(setting) ?? LocalTools.Locate(exe)) != null;
static FileStream? LockStorage(string folder)
{
    try { return File.Open(Path.Combine(folder, ".orbita.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { Console.Error.WriteLine("Outra instância do Orbita está usando esta pasta de dados."); return null; }
}

sealed record CreateProject(string Name, string? Workspace);
sealed record NewMission(string Name);
sealed record EditContext(string? Text);
sealed record ReviewDiff(string? Revision);
sealed record SetReserve(int Percent);
sealed record RotationTest(string[]? Accounts, string? Model);
sealed record VerifyAccount(string Id);
