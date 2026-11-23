using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class DiagnosticoModel(Store store) : PageModel
{
    public string Info { get; private set; } = "";
    public string? Hold { get; private set; }
    public async Task OnGetAsync()
    {
        Info = await Tools.Diagnostics(store);
        Hold = store.Read(db => db.Scalar<string>("SELECT value FROM service_flags WHERE key='restore_hold'"));
    }
}
