using Central.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class IndexModel(Store store) : PageModel
{
    public IReadOnlyList<Project> Projects => store.Projects();
    public string Key { get; } = Store.Id();
    public string? Error { get; private set; }
    public IActionResult OnPost(string name, string kind, string classification, string key)
    {
        try { return RedirectToPage("/Projeto", new { id = store.CreateProject(name, kind, classification, key) }); }
        catch (DomainError error) { Error = error.Message; Response.StatusCode = 422; return Page(); }
    }
}
