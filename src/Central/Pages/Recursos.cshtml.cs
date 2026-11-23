using System.Text.Json;
using System.Text.Json.Serialization;
using Central.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class RecursosModel(Store store, Coordinator coordinator) : PageModel
{
    private static readonly JsonSerializerOptions Input = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public IReadOnlyList<Limit> Observations => coordinator.Observations();
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

    public sealed record ImportModel(string Connection, string Account, string Product, string Profile, string ClientVersion,
        string Model, string Catalog, bool Retired, string[] Classes, ImportLimit[] Limits);
    public sealed record ImportLimit(string Id, string Subject, string Bucket, string Window, string Unit,
        string Status, long? Remaining, DateTimeOffset? Reset, string Source);
}
