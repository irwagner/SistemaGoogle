using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class GoogleModel(GoogleAuthorization google, Store store) : PageModel
{
    public string? Error { get; private set; }
    public string? AuthorizationUrl { get; private set; }
    public IReadOnlyList<GoogleProfile> Profiles => google.List();
    public bool RestoreHeld => Backup.Held(store);
    public bool Ready(GoogleProfile profile)
    {
        try { return google.Connected(profile); }
        catch (DomainError) { return false; }
    }
    public IActionResult OnPostConfigurar(string product, string label, string cloud, string expected, string clientId, string clientSecret)
    {
        try { google.Configure(product,label,cloud,expected,clientId,clientSecret); return RedirectToPage(); }
        catch (DomainError error) { return Fail(error); }
    }
    public IActionResult OnPostAutorizar(string profile)
    {
        try { AuthorizationUrl = google.Start(profile, HttpContext.Connection.LocalPort); return Page(); }
        catch (DomainError error) { return Fail(error); }
    }
    public IActionResult OnPostDesconectar(string profile)
    {
        try { google.Disconnect(profile); return RedirectToPage(); }
        catch (DomainError error) { return Fail(error); }
    }
    public IActionResult OnPostConciliar(string? evidence, bool confirmed)
    {
        try { Backup.ConfirmReview(store, evidence, confirmed); return RedirectToPage(); }
        catch (DomainError error) { return Fail(error); }
    }
    private IActionResult Fail(DomainError error) { Error = error.Message; Response.StatusCode = 422; return Page(); }
}
