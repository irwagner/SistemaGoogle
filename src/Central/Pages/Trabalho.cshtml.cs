using System.Text;
using Central.Domain;
using Central.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Central.Pages;

public sealed class TrabalhoModel(Store store, Workbench workbench, LocalModel local, VideoStories video, Studio studio,
    Coordinator coordinator, AudioDesk audio, Distribution distribution, GoogleAuthorization google, NotebookJobs notebook,
    YoutubeDesk youtube, Research research) : PageModel
{
    public WorkItem Item { get; private set; } = null!;
    public Project Project { get; private set; } = null!;
    public string? Error { get; private set; }
    public IReadOnlyList<string> Models { get; private set; } = [];
    public IReadOnlyList<WorkMessage> Messages => workbench.Messages(Item.Id);
    public IReadOnlyList<WorkRun> Runs => workbench.Runs(Item.Id);
    public IReadOnlyList<WorkFile> Files => workbench.Files(Item);
    public IReadOnlyList<Asset> ProjectFiles => store.Assets(Project.Id);
    public IReadOnlyList<WorkItem> OtherEpisodes => workbench.List("video", Item.Project).Where(w => video.Scope(w.Id) != null).ToArray();
    public IReadOnlyList<AudioReview> Audio => audio.Reviews(Item);
    public IReadOnlyList<Delivery> Deliveries => distribution.List(Item.Id);
    public IReadOnlyList<GoogleProfile> Profiles(string product) => google.List().Where(p => p.Product == product && Ready(p)).ToArray();
    public IReadOnlyList<NotebookJob> Ingestion => notebook.List(Item.Id);
    public IReadOnlyList<Source> Sources => research.Sources(Item.Project).Where(s => s.Question == workbench.Question(Item.Id)).ToArray();
    public IReadOnlyList<Claim> Claims => research.Claims(Item.Project).Where(c => c.Question == workbench.Question(Item.Id)).ToArray();
    private bool Ready(GoogleProfile profile)
    {
        try { return google.Connected(profile); }
        catch (DomainError) { return false; }
    }
    public Story? Story => video.Get(Item.Id);
    public EpisodeScope? Episode => video.Scope(Item.Id);
    public IReadOnlyList<Asset> Masters => Episode is { } scope
        ? studio.Edits(scope.Scope).Where(edit => edit.State == "aprovada" && edit.Master != null)
            .Select(edit => store.GetAsset(scope.Scope, edit.Master!)).ToArray()
        : [];
    public string RequestDraft => workbench.Draft(Item, "pedido");
    public string PlanDraft => workbench.Draft(Item, "plano");
    public bool Editable => Item.State is "rascunho" or "plano" or "falhou";
    public string? Resource => workbench.Resource(Item.Id);
    public IReadOnlyList<ResourceCard> Resources => coordinator.Connections().Where(card => Workbench.SupportsResource(Item.Area, card.Product)).ToArray();

    public async Task<IActionResult> OnGet(string id)
    {
        try { await Load(id); return Page(); }
        catch (DomainError) { return NotFound(); }
    }

    public IActionResult OnGetEstado(string id)
    {
        try { var item = workbench.Get(id); return new JsonResult(new { item.State, item.Revision, item.Note }); }
        catch (DomainError) { return NotFound(); }
    }

    public IActionResult OnGetArquivo(string id, string asset, bool read = false)
    {
        try
        {
            var item = workbench.Get(id);
            var result = workbench.Files(item).FirstOrDefault(a => a.Asset.Id == asset)?.Asset;
            if (result == null) return NotFound();
            var path = store.Verify(result);
            if (read && result.Size <= 512 * 1024 &&
                result.Type is ".txt" or ".md" or ".json" or ".patch" or ".html" or ".css" or ".js" or ".cs" or ".csproj" or ".py")
                return Content(System.IO.File.ReadAllText(path, Encoding.UTF8), "text/plain; charset=utf-8");
            return PhysicalFile(path, "application/octet-stream", result.Name);
        }
        catch (DomainError) { return NotFound(); }
    }

    public IActionResult OnPostRascunho(string id, int revision, string field, string? body)
    {
        try { workbench.SaveDraft(id, revision, field, body); return new StatusCodeResult(204); }
        catch (DomainError) { return new StatusCodeResult(409); }
    }

    public Task<IActionResult> OnPostPedido(string id, int revision, string body) =>
        Change(id, () => workbench.SaveRequest(id, revision, body));
    public Task<IActionResult> OnPostMensagem(string id, string body) => Change(id, () => workbench.Comment(id, body));
    public Task<IActionResult> OnPostPlanejar(string id, int revision, string? model, string action) =>
        Change(id, () => { if (action == "modelo") workbench.PlanWithModel(id, revision, model); else workbench.Template(id, revision); });
    public Task<IActionResult> OnPostPlano(string id, int revision, string plan, string executor, string? model, string action) =>
        Change(id, () => { if (action == "confirmar") workbench.Approve(id, revision, plan, executor, model); else workbench.SavePlan(id, revision, plan); });
    public Task<IActionResult> OnPostAcao(string id, int revision, string action) =>
        Change(id, () => workbench.SetState(id, revision, action));
    public Task<IActionResult> OnPostVincular(string id, string asset, string role) =>
        Change(id, () => workbench.Attach(id, asset, role));
    public Task<IActionResult> OnPostReceber(string id, int revision) =>
        Change(id, () => workbench.ReceiveAssisted(id, revision));
    public Task<IActionResult> OnPostRecurso(string id, string? connection) =>
        Change(id, () => workbench.SelectResource(id, connection));
    public Task<IActionResult> OnPostCusto(string id, long? cents, string? note) => Change(id, () =>
    {
        if (!ModelState.IsValid) throw new DomainError("Informe um valor inteiro de centavos.");
        workbench.Cost(id, cents, note);
    });
    public Task<IActionResult> OnPostFonte(string id, string label, string locator, string author, string rights, string excerpt) =>
        Change(id, () => research.AddSource(workbench.Get(id).Project, workbench.Question(id), label, locator, author, rights, excerpt));
    public Task<IActionResult> OnPostAfirmacao(string id, string source, string claim, bool inference) => Change(id, () =>
    {
        var item = workbench.Get(id);
        if (!research.Sources(item.Project).Any(s => s.Id == source && s.Question == workbench.Question(id)))
            throw new DomainError("Escolha uma fonte desta investigação.");
        research.AddClaim(item.Project, source, claim, inference);
    });
    public Task<IActionResult> OnPostRevisaoFonte(string id, string claim, bool approved, string notes) => Change(id, () =>
    {
        var item = workbench.Get(id);
        if (!research.Claims(item.Project).Any(c => c.Id == claim && c.Question == workbench.Question(id)))
            throw new DomainError("Escolha uma afirmação desta investigação.");
        research.Review(item.Project, claim, approved, notes);
    });
    public IActionResult OnGetRelatorio(string id)
    {
        try { return File(Encoding.UTF8.GetBytes(research.Report(workbench.Get(id).Project, workbench.Question(id))), "text/markdown", "pesquisa.md"); }
        catch (DomainError) { return NotFound(); }
    }
    public Task<IActionResult> OnPostNotebook(string id, string account, string? destination, string label, string? asset, string? body, bool authorized) =>
        Change(id, () => notebook.Queue(id, account, destination, label, asset, body, authorized));
    public Task<IActionResult> OnPostConciliarNotebook(string id, string job, string remote) =>
        Change(id, () => notebook.Reconcile(id, job, remote));
    public Task<IActionResult> OnPostRetomarNotebook(string id, string job) => Change(id, () => notebook.RetryInspection(id, job));

    public Task<IActionResult> OnPostRevisarAudio(string id, string asset, string role, bool approved, string note) =>
        AsyncChange(id, () => audio.Review(id, asset, role, approved, note, HttpContext.RequestAborted));
    public Task<IActionResult> OnPostEnviarAudio(string id, string asset, string episode, int scene) =>
        Change(id, () => audio.AddToScene(id, asset, episode, scene));
    public Task<IActionResult> OnPostEntrega(string id, string media, string? cover, string? caption, string title, string description,
        string channel, string visibility, bool approved) =>
        Change(id, () => distribution.Create(id, media, cover, caption, title, description, channel, visibility, approved));
    public Task<IActionResult> OnPostAprovarEntrega(string id, string delivery) => Change(id, () => distribution.Approve(id, delivery));
    public Task<IActionResult> OnPostYoutube(string id, string delivery, string? profile, string? category, string? audience, bool authorized) =>
        AsyncChange(id, () => youtube.Upload(id, delivery, profile, category, audience, authorized, HttpContext.RequestAborted));
    public Task<IActionResult> OnPostMetricas(string id, string delivery) =>
        AsyncChange(id, () => youtube.Metrics(id, delivery, HttpContext.RequestAborted));
    public IActionResult OnGetPacoteCanal(string id, string delivery)
    {
        try
        {
            var file = distribution.Export(id, delivery);
            return File(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.DeleteOnClose),
                "application/zip", "pacote-canal.zip");
        }
        catch (DomainError) { return NotFound(); }
    }
    public Task<IActionResult> OnPostRoteiro(string id, string script, string characters, string shot1, string shot2, string shot3, bool approved) =>
        Change(id, () => video.Save(id, script, characters, [shot1, shot2, shot3], approved));
    public Task<IActionResult> OnPostVinculoVideo(string id, string asset, bool toParent) => Change(id, () =>
    {
        if (toParent && video.Scope(id) is { } scope && !studio.Edits(scope.Scope).Any(edit => edit.Master == asset && edit.State == "aprovada"))
            throw new DomainError("Aprove a montagem antes de compartilhar o master.");
        video.Link(id, asset, toParent);
    });

    public async Task<IActionResult> OnPostImportar(string id, IFormFile? file, string role, string? origin, string? rights)
    {
        try
        {
            var item = workbench.Get(id);
            if (item.State is "fila" or "rodando" or "aceito" or "cancelado") throw new DomainError("Pause o trabalho antes de importar.");
            if (file == null) throw new DomainError("Selecione um arquivo.");
            if (role == "repositorio" && !file.FileName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) ||
                role == "patch" && !file.FileName.EndsWith(".patch", StringComparison.OrdinalIgnoreCase))
                throw new DomainError("Confira o formato do repositório ou patch.");
            await using var stream = file.OpenReadStream();
            var asset = await store.Import(item.Project, null, file.FileName, stream, origin, rights, null, HttpContext.RequestAborted);
            workbench.Attach(id, asset.Id, role);
            return RedirectToPage(new { id });
        }
        catch (Exception error) when (error is DomainError or IOException)
        {
            return await Failure(id, error is DomainError ? error.Message : "O arquivo não pôde ser gravado. Confira o espaço disponível.");
        }
    }

    private async Task<IActionResult> Change(string id, Action action)
    {
        try { action(); return RedirectToPage(new { id }); }
        catch (DomainError error) { return await Failure(id, error.Message); }
    }

    private async Task<IActionResult> AsyncChange(string id, Func<Task> action)
    {
        try { await action(); return RedirectToPage(new { id }); }
        catch (Exception error) when (error is DomainError or IOException or HttpRequestException or System.Text.Json.JsonException
            or KeyNotFoundException or OperationCanceledException or InvalidOperationException)
        { return await Failure(id, error is DomainError ? error.Message : "A operação terminou sem confirmação. Confira seu estado antes de retomar."); }
    }

    private async Task<IActionResult> Failure(string id, string message)
    {
        try { await Load(id); }
        catch (DomainError) { return NotFound(); }
        Error = message;
        Response.StatusCode = 422;
        return Page();
    }

    private async Task Load(string id)
    {
        Item = workbench.Get(id);
        Project = store.GetProject(Item.Project);
        if (Editable && Item.Area is "programacao" or "video")
            Models = await local.Models(HttpContext.RequestAborted);
    }

    public static string StateLabel(string state) => state switch
    {
        "rascunho" => "Rascunho", "plano" => "Plano para revisar", "fila" => "Na fila", "rodando" => "Em execução",
        "aguardando" => "Aguardando operador", "pausado" => "Pausado", "falhou" => "Precisa de atenção",
        "producao" => "Em produção",
        "revisao" => "Em revisão", "aceito" => "Aceito", "cancelado" => "Cancelado", _ => state
    };
}
