using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class AreaModel(Store store, Workbench workbench) : PageModel
{
    public string Area { get; private set; } = "";
    public string? InitialMode { get; private set; }
    public string? SelectedProject { get; private set; }
    public string? Error { get; private set; }
    public IReadOnlyDictionary<string, string> Choices => Workbench.Modes(Area);
    public IReadOnlyList<Project> Projects => store.Projects();
    public IReadOnlyList<WorkItem> Items => workbench.List(Area, SelectedProject);
    public string Key { get; } = Store.Id();

    public IActionResult OnGet(string area, string? mode, string? project)
    {
        if (!Workbench.Areas.ContainsKey(area)) return NotFound();
        Area = area;
        InitialMode = mode;
        SelectedProject = Projects.Any(p => p.Id == project) ? project : null;
        return Page();
    }

    public IActionResult OnPost(string area, string mode, string? project, string? projectName, string? title, string? request,
        int priority, string? follows, string? key)
    {
        if (!Workbench.Areas.ContainsKey(area)) return NotFound();
        Area = area;
        InitialMode = mode;
        SelectedProject = project;
        try
        {
            if (!ModelState.IsValid || !Choices.ContainsKey(mode) || priority is < 0 or > 9)
                throw new DomainError("Confira a modalidade e a prioridade.");
            var validatedTitle = Store.Text(title, 120, "um título");
            var validatedRequest = Store.Text(request, 14000, "o pedido");
            if (string.IsNullOrWhiteSpace(project) && !string.IsNullOrWhiteSpace(follows))
                throw new DomainError("Uma dependência exige um projeto existente.");
            if (string.IsNullOrWhiteSpace(project))
                project = store.CreateProject(Store.Text(projectName, 100, "o nome do projeto"), area switch
                {
                    "video" or "audio" or "distribuicao" => "estudio", "pesquisa" => "pesquisa", _ => "software"
                }, "interno", key);
            return RedirectToPage("/Trabalho", new { id = workbench.Create(project, area, mode, validatedTitle, validatedRequest, priority, follows) });
        }
        catch (DomainError error) { Error = error.Message; Response.StatusCode = 422; return Page(); }
    }

    public IReadOnlyList<WorkItem> Dependencies() => SelectedProject == null ? [] : workbench.List(project: SelectedProject);
}
