using System.Text;
using System.Text.Json;
using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class ProjetoModel(Store store, Coordinator coordinator, Studio studio, Research research, Software software, VideoStories video, Workbench workbench) : PageModel
{
    public Project Project { get; private set; } = null!;
    public string Section { get; private set; } = "trabalho";
    public IReadOnlyList<Work> Tasks => store.Tasks(Project.Id);
    public IReadOnlyList<Asset> Assets => store.Assets(Project.Id);
    public IReadOnlyList<Checkpoint> Checkpoints => store.Checkpoints(Project.Id);
    public IReadOnlyList<Entry> Events => store.Events(Project.Id);
    public IReadOnlyList<Effect> Effects => coordinator.Effects(Project.Id);
    public IReadOnlyList<LedgerEntry> Ledger => coordinator.Ledger(Project.Id);
    public Bible? Bible => studio.Current(Project.Id);
    public IReadOnlyList<Shot> Shots => studio.Shots(Project.Id);
    public IReadOnlyList<Take> Takes => studio.Takes(Project.Id);
    public IReadOnlyList<Track> Tracks => studio.Tracks(Project.Id);
    public IReadOnlyList<Edit> Edits => studio.Edits(Project.Id);
    public IReadOnlyList<Question> Questions => research.Questions(Project.Id);
    public IReadOnlyList<Source> Sources => research.Sources(Project.Id);
    public IReadOnlyList<Claim> Claims => research.Claims(Project.Id);
    public EpisodeScope? Scope => video.Owner(Project.Id);
    public IReadOnlyList<WorkItem> Works => workbench.List(project: Project.Id);
    [TempData] public string? Notice { get; set; }
    public string? Error { get; private set; }
    public string Key { get; } = Store.Id();

    public IActionResult OnGet(string id, string? sec)
    {
        try { Load(id, sec); return Page(); }
        catch (DomainError) { return NotFound(); }
    }

    public async Task<IActionResult> OnPostImportar(string id, string? sec, string? task, IFormFile? file, string origin, string rights, string key)
    {
        try
        {
            Load(id, sec);
            if (file == null || file.Length == 0) throw new DomainError("Escolha um arquivo não vazio.");
            await using var contents = file.OpenReadStream();
            var asset = await store.Import(id, task, file.FileName, contents, origin, rights, key, HttpContext.RequestAborted);
            Notice = $"Arquivo validado · SHA-256 {asset.Hash}";
            return RedirectToPage(new { id, sec = Section });
        }
        catch (DomainError error) { return Failed(error); }
        catch (IOException) { return Failed(new DomainError("Não foi possível gravar o arquivo. Verifique espaço e permissões.")); }
    }

    public IActionResult OnPostTarefa(string id, string title, string[]? predecessors, string policy) =>
        Change(id, "trabalho", () => store.CreateTask(id, title, predecessors ?? [], policy), "Tarefa criada.");
    public IActionResult OnPostAvancar(string id, string task, int version, string action, string note) =>
        Change(id, "trabalho", () => store.Advance(id, task, version, action, note), "Decisão registrada no checkpoint.");

    public IActionResult OnPostPacoteTarefa(string id, string task, string baseCommit, string objective, string acceptance) =>
        Download(() => software.Package(id, task, baseCommit, objective, acceptance), "application/json", "pacote-tarefa.json");
    public IActionResult OnPostBiblia(string id, string title, string script, string bible) =>
        Change(id, "estudio", () => studio.SaveBible(id, title, script, bible), "Versão editorial registrada.");
    public IActionResult OnPostPlano(string id, int scene, string sceneDescription, string direction, int frames) =>
        Change(id, "estudio", () => studio.AddShot(id, scene, sceneDescription, direction, frames), "Plano registrado.");
    public IActionResult OnPostTake(string id, string shot, string asset, string parameters) =>
        Change(id, "estudio", () => studio.AddTake(id, shot, asset, parameters), "Take acrescentado para revisão.");
    public IActionResult OnPostRevisarTake(string id, string take, bool select, string notes) =>
        Change(id, "estudio", () => studio.ReviewTake(id, take, select, notes), "Decisão de continuidade registrada.");
    public IActionResult OnPostTrocarTake(string id, string take, string reason) =>
        Change(id, "estudio", () => studio.ReplaceTake(id, take, reason), "Somente o take deste plano mudou. Registre outra timeline para exportar.");
    public IActionResult OnPostFaixa(string id, string asset, string role, string voice, double gain, int start) =>
        Change(id, "estudio", () => studio.AddTrack(id, asset, role, voice, gain, start), "Faixa acrescentada.");
    public IActionResult OnPostEdicao(string id) =>
        Change(id, "estudio", () => studio.CreateEdit(id), "Timeline registrada.");
    public IActionResult OnPostRevisarEdicao(string id, string edit, bool accept, string notes) =>
        Change(id, "estudio", () => studio.ReviewEdit(id, edit, accept, notes), "Revisão do master registrada.");
    public IActionResult OnGetFlow(string id, string shot) =>
        Download(() => studio.FlowPackage(id, shot, connection: FlowConnection(id)), "application/json", "ficha-flow.json");
    public IActionResult OnPostFlow(string id, string shot, int outputs, int? unitCost, string? reference) =>
        Download(() =>
        {
            if (!ModelState.IsValid) throw new DomainError("Confira saídas e custo em créditos.");
            return studio.FlowPackage(id, shot, outputs, unitCost, reference, FlowConnection(id));
        }, "application/json", "ficha-flow.json");
    private string? FlowConnection(string id) => video.Owner(id) is { } owner ? workbench.Resource(owner.Work) : null;
    public IActionResult OnGetProbe(string id, string edit) =>
        Download(() => studio.Probe(id, edit), "application/json", "ffprobe.json");
    public IActionResult OnGetArquivo(string id, string asset, bool inline = false)
    {
        try
        {
            var item = store.GetAsset(id, asset);
            var path = store.Verify(item);
            if (inline && item.Type == ".mp4") return new PhysicalFileResult(path, "video/mp4") { EnableRangeProcessing = true };
            if (inline && item.Type is ".wav" or ".mp3")
                return new PhysicalFileResult(path, item.Type == ".wav" ? "audio/wav" : "audio/mpeg") { EnableRangeProcessing = true };
            return PhysicalFile(path, "application/octet-stream", item.Name);
        }
        catch (DomainError) { return NotFound(); }
    }

    public async Task<IActionResult> OnPostRender(string id, string edit)
    {
        try
        {
            Load(id, "estudio");
            await studio.Render(id, edit, HttpContext.RequestAborted);
            Notice = "Master validado por ffprobe e decodificação. Registre a revisão visual e sonora.";
            return RedirectToPage(new { id, sec = Section });
        }
        catch (DomainError error) { return Failed(error); }
    }

    public IActionResult OnPostExportar(string id, string edit, string caption, string cover, string metadata, bool authorized)
    {
        try
        {
            Load(id, "estudio");
            var path = studio.Export(id, edit, caption, cover, metadata, authorized);
            return File(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.DeleteOnClose),
                "application/zip", "pacote-editorial.zip");
        }
        catch (DomainError error) { return Failed(error); }
    }

    public IActionResult OnPostPergunta(string id, string question, string criteria) =>
        Change(id, "pesquisa", () => research.Ask(id, question, criteria), "Pergunta registrada.");
    public IActionResult OnPostFonte(string id, string question, string label, string locator, string author, string rights, string excerpt) =>
        Change(id, "pesquisa", () => research.AddSource(id, question, label, locator, author, rights, excerpt), "Fonte registrada.");
    public IActionResult OnPostAfirmacao(string id, string source, string claim, bool inference) =>
        Change(id, "pesquisa", () => research.AddClaim(id, source, claim, inference), "Afirmação vinculada à fonte.");
    public IActionResult OnPostRevisarPesquisa(string id, string claim, bool approved, string notes) =>
        Change(id, "pesquisa", () => research.Review(id, claim, approved, notes), "Revisão registrada.");
    public IActionResult OnGetRelatorio(string id) => Download(() => research.Report(id), "text/markdown", "relatorio.md");

    private IActionResult Change(string id, string section, Action action, string message)
    {
        try
        {
            Load(id, section);
            if (!ModelState.IsValid) throw new DomainError("Revise os campos do formulário.");
            action();
            Notice = message;
            return RedirectToPage(new { id, sec = Section });
        }
        catch (DomainError error) { return Failed(error); }
    }

    private IActionResult Download(Func<string> content, string type, string name)
    {
        try { return File(Encoding.UTF8.GetBytes(content()), type, name); }
        catch (DomainError error) { return BadRequest(error.Message); }
    }

    private void Load(string id, string? section)
    {
        Project = store.GetProject(id);
        Section = section switch
        {
            "estudio" when Project.Kind == "estudio" => section,
            "pesquisa" when Project.Kind == "pesquisa" => section,
            "arquivos" or "historico" or "recursos" => section,
            _ => "trabalho"
        };
    }

    private IActionResult Failed(DomainError error)
    {
        if (Project == null) return NotFound();
        Error = error.Message;
        Response.StatusCode = 422;
        return Page();
    }

    public static string DisplayTime(string text) => TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(text),
        TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).ToString("dd/MM/yyyy HH:mm:ss");
}
