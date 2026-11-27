using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Orbita.Core;

public sealed partial class StudioFiles : ICheckpointStore
{
    public static readonly string[] ProjectFiles = ["SPEC.md", "TASKS.md", "MEMORY.md", "SKILL.md"];
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly object gate = new();
    private readonly object disk = new();
    private readonly Dictionary<string, List<MissionEvent>> timelines = new();
    public string DataRoot { get; }
    public string Root { get; }

    public StudioFiles(string root)
    {
        DataRoot = Path.GetFullPath(root);
        Root = Path.Combine(DataRoot, "orbita");
        Directory.CreateDirectory(Path.Combine(Root, "projetos"));
        Directory.CreateDirectory(Path.Combine(Root, "missoes"));
    }

    public static string ResolveRoot() => Environment.GetEnvironmentVariable("CENTRAL_DATA") is { Length: > 0 } root
        ? root : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralProducao", "dados");

    public StudioProject CreateProject(string name, string? path)
    {
        name = Required(name, 100, "Informe o nome do projeto.");
        var id = NewId();
        var managed = string.IsNullOrWhiteSpace(path);
        var workspace = managed ? Path.Combine(Root, "workspaces", id) : Path.GetFullPath(path!.Trim());
        if (managed) Directory.CreateDirectory(workspace);
        else if (!Directory.Exists(workspace)) throw new StudioException("A pasta informada não existe.");
        var project = new StudioProject(id, name, workspace, managed);
        SaveJson(Path.Combine(ProjectPath(id), "projeto.json"), project);
        EnsureContext(id);
        return project;
    }

    public void EnsureContext(string id)
    {
        foreach (var name in ProjectFiles)
        {
            var path = ProjectFile(id, name);
            if (!File.Exists(path)) WriteAtomic(path, ReadProjectFile(id, name));
        }
    }

    public IReadOnlyList<StudioProject> Projects()
    {
        var projects = Directory.EnumerateFiles(Path.Combine(Root, "projetos"), "projeto.json", SearchOption.AllDirectories)
            .Select(Read<StudioProject>).OfType<StudioProject>().ToDictionary(p => p.Id);
        var previous = Path.Combine(DataRoot, "projetos");
        if (Directory.Exists(previous))
        {
            foreach (var dir in Directory.EnumerateDirectories(previous))
            {
                var id = Path.GetFileName(dir);
                if (!IdPattern().IsMatch(id) || projects.ContainsKey(id)) continue;
                try
                {
                    var metadata = Read<JsonObject>(Path.Combine(dir, "projeto.json"));
                    if (metadata?["name"]?.GetValue<string>() is not { Length: > 0 } name) continue;
                    var workspace = metadata["customPath"]?.GetValue<string>();
                    workspace = string.IsNullOrWhiteSpace(workspace) ? Path.Combine(dir, "workspace") : Path.GetFullPath(workspace);
                    projects.Add(id, new StudioProject(id, name, workspace, false));
                }
                catch (Exception error) when (error is JsonException or IOException or ArgumentException or InvalidOperationException) { }
            }
        }
        return projects.Values.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public StudioProject Project(string id) => Projects().FirstOrDefault(p => p.Id == CheckId(id))
        ?? throw new StudioException("Projeto não encontrado.");

    public string ReadProjectFile(string id, string name)
    {
        var project = Project(id);
        var path = ProjectFile(id, name);
        if (File.Exists(path)) return ReadText(path);
        return name switch
        {
            "SPEC.md" => $"# {project.Name}\n\n## Objetivo\n\nDescreva o que o Orbita deve entregar.\n",
            "TASKS.md" => "# Tarefas\n\n- [ ] Definir as etapas do trabalho.\n",
            "MEMORY.md" => "# Decisões do projeto\n",
            _ => "# Procedimentos do projeto\n\nDescreva aqui um procedimento repetível para o agente.\n"
        };
    }

    public void WriteProjectFile(string id, string name, string text)
    {
        _ = Project(id);
        if (text.Length > 64_000) throw new StudioException("O arquivo pode ter até 64 mil caracteres.");
        WriteAtomic(ProjectFile(id, name), text);
    }

    public IReadOnlyList<Mission> List() => Directory.EnumerateDirectories(Path.Combine(Root, "missoes"))
        .Select(dir => Read<Mission>(Path.Combine(dir, "missao.json"))).OfType<Mission>()
        .OrderByDescending(m => m.Updated).ToArray();

    public Mission Get(string id) => Read<Mission>(Path.Combine(MissionPath(id), "missao.json"))
        ?? throw new StudioException("Missão não encontrada.");

    public IReadOnlyList<Mission> History(string id)
    {
        _ = Get(id);
        var dir = Path.Combine(MissionPath(id), "checkpoints");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.json").OrderDescending().Take(40)
            .Select(Read<Mission>).OfType<Mission>().ToArray() : [];
    }

    public void Save(Mission mission, bool checkpoint = false)
    {
        var path = MissionPath(mission.Id);
        SaveJson(Path.Combine(path, "missao.json"), mission);
        if (checkpoint)
            SaveJson(Path.Combine(path, "checkpoints", $"{mission.Updated:yyyyMMddHHmmssfffffff}-{NewId()}.json"), mission);
    }

    public MissionEvent Append(string missionId, string kind, string text, JsonNode? data = null)
    {
        lock (gate)
        {
            var items = Timeline(missionId);
            var entry = new MissionEvent((items.LastOrDefault()?.Sequence ?? 0) + 1, DateTimeOffset.UtcNow,
                kind, text.Length > 12_000 ? text[..12_000] : text, data?.DeepClone());
            File.AppendAllText(Path.Combine(MissionPath(missionId), "eventos.jsonl"), JsonSerializer.Serialize(entry, Json) + "\n", Utf8);
            items.Add(entry);
            if (items.Count > 1200) items.RemoveRange(0, items.Count - 1200);
            return entry;
        }
    }

    public IReadOnlyList<MissionEvent> EventsAfter(string missionId, long sequence)
    {
        lock (gate) return Timeline(missionId).Where(e => e.Sequence > sequence).ToArray();
    }

    private List<MissionEvent> Timeline(string id)
    {
        if (timelines.TryGetValue(id, out var cached)) return cached;
        var path = Path.Combine(MissionPath(id), "eventos.jsonl");
        var entries = new List<MissionEvent>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path).TakeLast(1200))
            {
                try { if (JsonSerializer.Deserialize<MissionEvent>(line, Json) is { } entry) entries.Add(entry); }
                catch (JsonException) { }
            }
        }
        timelines.Add(id, entries);
        return entries;
    }

    public string MissionPath(string id) => Path.Combine(Root, "missoes", CheckId(id));
    public string LinkedProjectRepository(string id) => Path.Combine(ProjectPath(id), "repositorio-vinculado");

    private string ProjectPath(string id) => Path.Combine(Root, "projetos", CheckId(id));
    private string ProjectFile(string id, string name) => ProjectFiles.Contains(name, StringComparer.Ordinal)
        ? Path.Combine(ProjectPath(id), name) : throw new StudioException("Arquivo de contexto inválido.");

    public static string NewId() => Guid.NewGuid().ToString("N");
    private static string CheckId(string id) => IdPattern().IsMatch(id) ? id : throw new StudioException("Identificador inválido.");
    public static string Required(string? value, int max, string error)
    {
        value = value?.Trim();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= max ? value : throw new StudioException(error);
    }

    [GeneratedRegex("^[a-z0-9]{6,40}$")]
    private static partial Regex IdPattern();
    private T? Read<T>(string path)
    {
        lock (disk) return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;
    }

    private string ReadText(string path)
    {
        lock (disk) return new FileInfo(path).Length <= 256_000 ? File.ReadAllText(path)
            : throw new StudioException("O arquivo de contexto excede o limite de leitura.");
    }

    private void SaveJson<T>(string path, T data) => WriteAtomic(path, JsonSerializer.Serialize(data, Json));
    private void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + NewId() + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            // No Windows, a leitura concorrente pode impedir a substituição do arquivo.
            lock (disk) File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
