using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var data = Environment.GetEnvironmentVariable("ORCAMENTOS_DATA") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OficinaHorizonte");
Directory.CreateDirectory(data);
var quotes = new Quotes(Path.Combine(data, "orcamentos.sqlite"));
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(config =>
{
    config.AddServerHeader = false;
    config.Limits.MaxRequestBodySize = 16 * 1024;
    config.Listen(IPAddress.Loopback, int.TryParse(Environment.GetEnvironmentVariable("ORCAMENTOS_PORT"), out var port) ? port : 0);
});
builder.Services.AddAntiforgery(options => options.Cookie.SameSite = SameSiteMode.Strict);
var app = builder.Build();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host != "127.0.0.1" || ctx.Request.Host.Port != ctx.Connection.LocalPort)
    { ctx.Response.StatusCode = 400; return; }
    if (ctx.Request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != $"http://{ctx.Request.Host}")
    { ctx.Response.StatusCode = 403; return; }
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.MapGet("/", (HttpContext ctx, IAntiforgery csrf) => Html(Page(quotes.All(), csrf.GetAndStoreTokens(ctx).RequestToken!, null)));
app.MapPost("/", async (HttpContext ctx, IAntiforgery csrf) =>
{
    if (!await csrf.IsRequestValidAsync(ctx)) return Results.BadRequest();
    var form = await ctx.Request.ReadFormAsync();
    try
    {
        var quote = quotes.Save(form["key"].ToString(), form["cliente"].ToString(), form["item"].ToString(),
            form["quantidade"].ToString(), form["unitario"].ToString());
        return Results.Redirect("/orcamento/" + quote.Id);
    }
    catch (ArgumentException exception)
    {
        return Html(Page(quotes.All(), csrf.GetAndStoreTokens(ctx).RequestToken!, exception.Message), 422);
    }
});
app.MapGet("/orcamento/{id:long}", (long id) =>
{
    var item = quotes.Get(id);
    return item == null ? Results.NotFound() : Html(Shell("Orçamento #" + item.Id, $"""
        <a href="/">← Orçamentos salvos</a><h1>Orçamento #{item.Id}</h1>
        <dl><dt>Cliente</dt><dd>{H(item.Client)}</dd><dt>Item</dt><dd>{H(item.Item)}</dd>
        <dt>Quantidade e valor unitário</dt><dd>{item.Quantity} × {Money(item.Unit)}</dd>
        <dt>Total</dt><dd class="total">{Money(item.Total)}</dd></dl>
        <a class="button" href="/orcamento/{item.Id}/pdf">Baixar PDF</a>
        """));
});
app.MapGet("/orcamento/{id:long}/pdf", (long id) =>
{
    var item = quotes.Get(id);
    return item == null ? Results.NotFound() : Results.File(Pdf.Create(item), "application/pdf", $"orcamento-{item.Id}.pdf");
});
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    if (args.Contains("--abrir")) Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
});
await app.RunAsync();

static string Page(IReadOnlyList<Quote> items, string token, string? error) => Shell("Orçamentos", $"""
    <h1>Orçamentos</h1><div class="grid">
    <section><h2>Novo orçamento</h2>{(error == null ? "" : $"<p role='alert'>{H(error)}</p>")}
    <form method="post">
    <input type="hidden" name="__RequestVerificationToken" value="{H(token)}">
    <input type="hidden" name="key" value="{Guid.NewGuid():N}">
    <label>Cliente<input name="cliente" maxlength="80" required></label>
    <label>Item ou serviço<input name="item" maxlength="80" required></label>
    <label>Quantidade<input name="quantidade" type="number" min="1" max="100000" required></label>
    <label>Valor unitário em reais<input name="unitario" inputmode="decimal" placeholder="47,90" required></label>
    <button>Salvar orçamento</button></form></section>
    <section><h2>Salvos</h2>{(items.Count == 0 ? "<p>Nenhum orçamento salvo.</p>" : "")}
    <ul>{string.Concat(items.Select(item => $"<li><a href='/orcamento/{item.Id}'>{H(item.Client)} · {Money(item.Total)}</a></li>"))}</ul></section></div>
    """);

static string Shell(string title, string body) => $$"""
    <!doctype html><html lang="pt-BR"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>{{H(title)}} · Oficina Horizonte</title><style>
    *{box-sizing:border-box}body{margin:0;background:#f6f0e5;color:#292018;font:16px/1.5 system-ui}header{background:#482921;color:#fff0d0;padding:1rem max(1rem,calc((100% - 880px)/2));font-weight:700}main{max-width:900px;margin:auto;padding:1.5rem}h1{font:500 3rem Georgia;margin:.7rem 0 1.3rem}h2{margin:0 0 1rem}section{background:#fffcf5;border:1px solid #cbbba9;padding:1.2rem;border-radius:.6rem}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,300px),1fr));gap:1rem;align-items:start}label{display:grid;font-weight:600;gap:.25rem}form{display:grid;gap:.75rem}input,button{font:inherit;padding:.6rem;border:1px solid #b99b86;border-radius:.3rem}button,.button{background:#773b2c;color:#fff;padding:.6rem .9rem;border-radius:.3rem;cursor:pointer;width:fit-content;font-weight:650;text-decoration:none}a{color:#713525}dd{margin:0 0 .7rem}.total{font-size:1.8rem;font-weight:700}li{margin:.5rem 0}[role=alert]{background:#ffead6;padding:.65rem}
    </style></head><body><header>Oficina Horizonte</header><main>{{body}}</main></body></html>
    """;

static string H(string text) => HtmlEncoder.Default.Encode(text);
static IResult Html(string body, int status = 200) => Results.Content(body, "text/html; charset=utf-8", statusCode: status);
static string Money(long cents) => (cents / 100m).ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
