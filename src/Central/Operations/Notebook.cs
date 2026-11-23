using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Central.Domain;

namespace Central.Operations;

public sealed record ConnectorManifest(string Id, string Version, string Schema, string Transport,
    string Authentication, IReadOnlyDictionary<string, string> Capabilities);
public sealed record NotebookResource(string Name, string? State);

// Transport only. A caller must obtain OAuth, attest the pinned identity and write a durable
// intent before invoking either POST. This transport is not registered as a dispatch worker.
public sealed partial class Notebook(HttpClient client)
{
    private static readonly Uri Api = new("https://notebook.googleapis.com/v1/");
    public static ConnectorManifest Manifest { get; } = new("gemini-notebook-pessoal", "0.1.0", "v1", "REST",
        "OAuth 2.0 pessoal, projeto consumidor separado", new Dictionary<string, string>
        {
            ["probe"] = "experimental", ["beginAuth"] = "unsupported", ["authStatus"] = "unsupported", ["discover"] = "unsupported",
            ["readQuota"] = "documentado; leitura de saldo unsupported", ["prepare"] = "experimental",
            ["submit"] = "experimental; criar caderno e fonte de texto", ["inspect"] = "experimental; GET por nome",
            ["collect"] = "experimental; ACTIVE", ["cancel"] = "unsupported", ["reconcile"] = "experimental; GET por nome conhecido",
            ["uploadFile"] = "unsupported", ["idempotency"] = "unsupported"
        });

    public Task<NotebookResource> Get(string name, string project, string bearer, CancellationToken token) =>
        Call(HttpMethod.Get, Resource(name), project, bearer, null, token);
    public Task<NotebookResource> CreateNotebook(string name, string project, string bearer, CancellationToken token) =>
        Call(HttpMethod.Post, "notebooks", project, bearer, new { display_name = Store.Text(name, 500, "o caderno") }, token);
    public Task<NotebookResource> CreateText(string notebook, string title, string text, string project, string bearer, CancellationToken token)
    {
        if (Resource(notebook).Split('/').Length != 2 || System.Text.Encoding.UTF8.GetByteCount(text) > 1_000_000)
            throw new DomainError("Caderno ou fonte fora do limite do REST v1.");
        return Call(HttpMethod.Post, notebook + "/sources", project, bearer,
            new { display_name = Store.Text(title, 500, "a fonte"), text_content = new { content = Store.Text(text, 1_000_000, "o conteúdo") } }, token);
    }

    public async Task<NotebookResource> WaitForSource(string source, string project, string bearer, int maxChecks, CancellationToken token,
        TimeProvider? clock = null)
    {
        if (Resource(source).Split('/').Length != 4 || maxChecks is < 1 or > 40) throw new DomainError("Fonte ou prazo inválido.");
        for (var index = 0; index < maxChecks; index++)
        {
            var item = await Get(source, project, bearer, token);
            if (item.State is "ACTIVE" or "FAILED" or "DELETING" || index == maxChecks - 1) return item;
            if (item.State != "PROCESSING") throw new DomainError("Estado de fonte desconhecido.");
            await Task.Delay(TimeSpan.FromSeconds(2), clock ?? TimeProvider.System, token);
        }
        throw new DomainError("A fonte segue pendente.");
    }

    private async Task<NotebookResource> Call(HttpMethod method, string path, string project, string bearer, object? data, CancellationToken token)
    {
        if (!CloudProject().IsMatch(project) || string.IsNullOrWhiteSpace(bearer)) throw new DomainError("Projeto ou autorização OAuth ausente.");
        using var request = new HttpRequestMessage(method, new Uri(Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("x-goog-user-project", project);
        if (data != null) request.Content = JsonContent.Create(data);
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) throw new DomainError($"Gemini Notebook REST v1 respondeu HTTP {(int)response.StatusCode}.");
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var root = document.RootElement;
        return new NotebookResource(Resource(root.GetProperty("name").GetString()!),
            root.TryGetProperty("state", out var state) ? state.GetString() : null);
    }

    private static string Resource(string name) => ResourcePath().IsMatch(name) ? name : throw new DomainError("Nome de recurso inválido.");
    [GeneratedRegex(@"^notebooks/[A-Za-z0-9_-]+(?:/sources/[A-Za-z0-9_-]+)?$")]
    private static partial Regex ResourcePath();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{4,62}$")]
    private static partial Regex CloudProject();
}
