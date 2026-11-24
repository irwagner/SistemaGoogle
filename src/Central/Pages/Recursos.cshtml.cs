using System.Text.Json;
using System.Text.Json.Serialization;
using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class RecursosModel(Store store, Coordinator coordinator, Workbench workbench) : PageModel
{
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public IReadOnlyList<Limit> Observations => coordinator.Observations();
    public IReadOnlyList<ResourceCard> Connections => coordinator.Connections();
    public WorkMetrics Metrics => workbench.Metrics();
    public long Now => store.Millis;
    public string? Error { get; private set; }
    [TempData] public string? Notice { get; set; }
    public string Example => Store.Json(new ImportModel("perfil-a", "titular-a", "flow", "perfil-a", "versao-observada",
        "modelo-informado", "catalogo-1", false, ["interno"], [new ImportLimit("janela-a", "sujeito-a", "grupo-informado",
        "mensal", "creditos", "unknown", null, null, "Painel do operador")]));

    public IActionResult OnPostObservar(string? snapshot)
    {
        try
        {
            var input = JsonSerializer.Deserialize<ImportModel>(Store.Text(snapshot, 24000, "o snapshot"), Input)
                ?? throw new DomainError("Snapshot ausente.");
            if (input.Limits is not { Length: > 0 and <= 30 } || input.Classes == null)
                throw new DomainError("Informe até 30 vínculos de cota observados.");
            var expiry = Now + 300_000;
            var limits = input.Limits.Select(l => new Limit(
                Store.Text(l.Id, 100, "a chave da cota"), Store.Text(l.Subject, 100, "o sujeito"),
                input.Product, Store.Text(l.Bucket, 100, "o grupo"), Store.Text(l.Window, 100, "a janela"),
                Store.Text(l.Unit, 40, "a unidade"), l.Status, l.Remaining, expiry, l.Reset?.ToUnixTimeMilliseconds(), l.Source)).ToArray();
            if (limits.Select(limit => (limit.Id, limit.Window)).Distinct().Count() != limits.Length)
                throw new DomainError("Há janelas repetidas no snapshot.");
            var connection = new Connection(input.Connection, input.Account, input.Product, null,
                "observacao_manual", input.Profile, 1, false, null, input.ClientVersion, expiry, input.Classes);
            coordinator.Record(connection, input.Model, input.Catalog, input.Retired, limits);
            Notice = "Observação registrada com validade de cinco minutos para a projeção.";
            return RedirectToPage();
        }
        catch (Exception exception) when (exception is DomainError or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = exception is DomainError ? exception.Message : "Confira o formato, os vínculos e os tipos do snapshot.";
            Response.StatusCode = 422;
            return Page();
        }
    }

    public IActionResult OnPostCarteira(string? wallet, string? unit, long? ceiling)
    {
        try
        {
            if (!ModelState.IsValid) throw new DomainError("O teto deve ser um inteiro ou ficar em branco.");
            coordinator.Wallet(Store.Text(wallet, 100, "a carteira"), Store.Text(unit, 40, "a unidade"), ceiling);
            Notice = "Carteira registrada.";
            return RedirectToPage();
        }
        catch (DomainError exception)
        {
            Error = exception.Message;
            Response.StatusCode = 422;
            return Page();
        }
    }

    public IActionResult OnPostRegistrar(string? connection, string? account, string product, string? profile,
        string? version, string? model, string? subject, string? bucket, string? window,
        string unit, string status, long? remaining, string? source)
    {
        try
        {
            if (!ModelState.IsValid || product is not ("antigravity" or "flow" or "notebook" or "youtube" or "ollama") ||
                unit is not ("requests" or "tokens" or "creditos" or "percentual"))
                throw new DomainError("Confira produto, unidade e saldo observado.");
            var id = Store.Text(connection, 100, "a conexão");
            var modelName = Store.Text(model, 120, "o modelo ou capacidade");
            var actor = Store.Text(account, 100, "um nome para a conta");
            var owner = Store.Text(subject, 100, "o sujeito da cota");
            var group = Store.Text(bucket, 100, "o grupo da cota");
            var revision = Store.Text(version, 100, "a versão do provedor");
            var expiry = Now + 300_000;
            var key = Store.Hash(System.Text.Encoding.UTF8.GetBytes(Store.Json(new { product, owner, group, unit })))[..32];
            coordinator.Record(new Connection(id,actor,product,null,"observacao_manual",Store.Text(profile,100,"o perfil"),
                1,false,null,revision,expiry,["interno"]), modelName, revision, false,
                [new Limit(key,owner,product,group,Store.Text(window,100,"a janela"),unit,status,
                    remaining,expiry,null,Store.Text(source,120,"a origem da medida"))], append: true);
            Notice = "Conexão e observação registradas.";
            return RedirectToPage();
        }
        catch (Exception error) when (error is DomainError or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = error is DomainError ? error.Message : "A observação diverge da conexão ou da unidade cadastrada.";
            Response.StatusCode = 422;
            return Page();
        }
    }

    public sealed record ImportModel(string Connection, string Account, string Product, string Profile, string ClientVersion,
        string Model, string Catalog, bool Retired, string[] Classes, ImportLimit[] Limits);
    public sealed record ImportLimit(string Id, string Subject, string Bucket, string Window, string Unit,
        string Status, long? Remaining, DateTimeOffset? Reset, string Source);
}
