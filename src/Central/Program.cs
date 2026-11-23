using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

var culture = CultureInfo.GetCultureInfo("pt-BR");
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;
var root = Environment.GetEnvironmentVariable("CENTRAL_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralProducao", "dados");
root = Path.GetFullPath(root);
if (root.StartsWith(@"\\", StringComparison.Ordinal) || !new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
    throw new DomainError("Selecione um disco local NTFS para os dados.");
foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
{
    var synced = Environment.GetEnvironmentVariable(variable);
    if (synced != null && Path.GetRelativePath(synced, root) is var relative && relative != ".."
        && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
        throw new DomainError("Escolha uma pasta de dados fora do OneDrive.");
}
Directory.CreateDirectory(root);
Store.RejectLinks(root);
var lockFile = Path.Combine(root, "central.lock");
Store.RejectLinks(lockFile);
FileStream singleWriter;
try { singleWriter = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
catch (IOException)
{
    Console.Error.WriteLine("Já existe uma Central em execução para esta pasta de dados.");
    return;
}
using var instance = singleWriter;
if (args.FirstOrDefault() == "backup")
{
    if (args.Length != 2) throw new DomainError("Informe a pasta de destino nova.");
    var store = new Store(root);
    Backup.Create(store, args[1]);
    return;
}
if (args.FirstOrDefault() == "restore")
{
    if (args.Length != 2) throw new DomainError("Informe a pasta do snapshot.");
    Backup.Restore(args[1], root);
    return;
}
if (args.FirstOrDefault() == "diagnostico")
{
    var store = new Store(root);
    Console.WriteLine(await Tools.Diagnostics(store));
    return;
}

var openBrowser = args.Contains("--abrir");
var testTokenFile = Environment.GetEnvironmentVariable("CENTRAL_BOOTSTRAP_FILE");
var bootstrap = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var session = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
var authentication = new object();
var expires = DateTimeOffset.UtcNow.AddHours(8);
var bootstrapExpiry = DateTimeOffset.UtcNow.AddMinutes(5);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.ConfigureKestrel(config =>
{
    config.AddServerHeader = false;
    config.Limits.MaxRequestBodySize = Store.FileLimit + 1024 * 1024;
    var port = int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_PORT"), out var configured) ? configured : 0;
    config.Listen(IPAddress.Loopback, port);
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = Store.FileLimit + 1024 * 1024);
builder.Services.AddRazorPages();
builder.Services.AddAntiforgery(options => options.Cookie.SameSite = SameSiteMode.Strict);
builder.Services.AddSingleton(new Store(root));
builder.Services.AddSingleton<Coordinator>();
builder.Services.AddSingleton<Studio>();
builder.Services.AddSingleton<Research>();
builder.Services.AddSingleton<Software>();
var app = builder.Build();
string address = "";
app.Use(async (context, next) =>
{
    var request = context.Request;
    if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)
        || request.Host.Host != "127.0.0.1" || request.Host.Port != context.Connection.LocalPort)
    { context.Response.StatusCode = 400; return; }
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != $"http://{request.Host}")
    { context.Response.StatusCode = 403; return; }
    if (request.Path == "/entrar" || request.Path == "/entrar.js" || request.Path == "/central.css")
    { await next(); return; }
    var authenticated = false;
    lock (authentication)
    {
        if (DateTimeOffset.UtcNow < expires && request.Cookies.TryGetValue("central", out var value) && Equal(value, session))
        {
            authenticated = true;
            expires = DateTimeOffset.UtcNow.AddHours(8);
        }
    }
    if (!authenticated)
    {
        if (HttpMethods.IsGet(request.Method)) context.Response.Redirect("/entrar");
        else context.Response.StatusCode = 401;
        return;
    }
    await next();
});
app.MapGet("/entrar", () => Results.Content("""
    <!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
    <title>Acesso à Central</title><link rel="stylesheet" href="/central.css"><main>
    <h1>Acesso à Central</h1><p id="estado">Abra o launcher para iniciar uma sessão local.</p>
    <script src="/entrar.js" defer></script></main></html>
    """, "text/html; charset=utf-8"));
app.MapPost("/entrar", (HttpContext context) =>
{
    lock (authentication)
    {
        if (bootstrap.Length == 0 || DateTimeOffset.UtcNow >= bootstrapExpiry
            || context.Request.Headers.Origin != $"http://{context.Request.Host}"
            || !Equal(context.Request.Headers["X-Central-Bootstrap"].ToString(), bootstrap))
            return Results.Unauthorized();
        bootstrap = "";
        expires = DateTimeOffset.UtcNow.AddHours(8);
        context.Response.Cookies.Append("central", session, new CookieOptions { SameSite = SameSiteMode.Strict, HttpOnly = true, Path = "/" });
        return Results.NoContent();
    }
});
app.UseStaticFiles();
app.MapRazorPages();
app.MapGet("/api/v1/projetos/{id}/eventos", (string id, long? cursor, Store store) =>
{
    if (!store.Projects().Any(project => project.Id == id)) return Results.NotFound();
    return Results.Ok(new { version = 1, events = store.Events(id, cursor ?? 0) });
});
app.MapGet("/api/v1/saude", (Store store) => Results.Ok(new { version = 1, migrations = store.Read(db => db.Scalar<long>("SELECT COUNT(*) FROM migrations")) }));
app.Lifetime.ApplicationStarted.Register(() =>
{
    address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var start = address + "/entrar#" + bootstrap;
    if (testTokenFile != null) File.WriteAllText(testTokenFile, start);
    if (openBrowser)
    {
        try { Process.Start(new ProcessStartInfo(start) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("Falha ao abrir o navegador. Verifique o navegador padrão."); }
    }
});
await app.RunAsync();

static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
    System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));
