using System.Net.Http.Json;
using System.Text.Json;
using Central.Domain;

namespace Central.Operations;

public sealed class LocalModel : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("http://127.0.0.1:11434/"),
        Timeout = TimeSpan.FromMinutes(7)
    };

    public async Task<IReadOnlyList<string>> Models(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var document = await client.GetFromJsonAsync<JsonDocument>("api/tags", deadline.Token);
            return document?.RootElement.GetProperty("models").EnumerateArray()
                .Where(entry => entry.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) && bytes > 0)
                .Select(entry => entry.GetProperty("name").GetString()!)
                .Where(name => !string.IsNullOrEmpty(name) && !name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase)).Order().ToArray() ?? [];
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            (error is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException))
        { return []; }
    }

    public async Task<string> Generate(string model, string instructions, string request, int tokens, CancellationToken cancellation, bool json = false)
    {
        if (!(await Models(cancellation)).Contains(model, StringComparer.Ordinal))
            throw new DomainError("O modelo selecionado não está instalado ou o Ollama local está indisponível. O pedido foi preservado.");
        var payload = new Dictionary<string, object>
        {
            ["model"] = model, ["system"] = instructions, ["prompt"] = request, ["stream"] = false,
            ["options"] = new { temperature = 0.15, num_ctx = 8192, num_predict = tokens },
            ["keep_alive"] = "2m"
        };
        if (json) payload["format"] = "json";
        using var reply = await client.PostAsJsonAsync("api/generate", payload, cancellation);
        if (!reply.IsSuccessStatusCode) throw new DomainError("Ollama local respondeu HTTP " + (int)reply.StatusCode + ".");
        using var document = await reply.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellation);
        var root = document?.RootElement ?? throw new DomainError("Ollama não devolveu um resultado.");
        if (root.GetProperty("model").GetString() != model || !root.GetProperty("done").GetBoolean())
            throw new DomainError("A resposta ficou incompleta ou veio de outro modelo.");
        return Store.Text(root.GetProperty("response").GetString(), 100_000, "a resposta do modelo");
    }

    public static string ValidateName(string? text)
    {
        var name = Store.Text(text, 100, "um modelo local instalado");
        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or ':' or '/')) ||
            name.Contains("..") || name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
            throw new DomainError("Selecione um modelo local instalado.");
        return name;
    }

    public void Dispose() => client.Dispose();
}
