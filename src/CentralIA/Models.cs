using System.Text.Json.Nodes;

namespace CentralIA;

public sealed class UserError(string message) : Exception(message);

public sealed class Project
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "geral";
    public string? CustomPath { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
}

public sealed class ChatThread
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Mode { get; set; } = "agente"; // agente | equipe | conversa
    public string? PinnedProviderId { get; set; }
    public string? PinnedModel { get; set; }
    public Continuation? Continuation { get; set; }
    public ReviewedWork? ReviewedWork { get; set; }
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public List<FileChange> Changes { get; set; } = new();
    public List<ActivityEntry> Activity { get; set; } = new();
    public List<TeamTask> Tasks { get; set; } = new();
}

public sealed class TeamTask
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> DependsOn { get; set; } = new();
    public string Status { get; set; } = "pendente"; // pendente | rodando | concluida | falhou
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Summary { get; set; }
    public string? Error { get; set; }
    public List<string> Files { get; set; } = new();
    public DateTime? Started { get; set; }
    public DateTime? Finished { get; set; }
}

public sealed class ChatMessage
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "user";
    public string? Content { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public bool? Ok { get; set; }
    public string? Model { get; set; }
    public string? Provider { get; set; }
    public DateTime Time { get; set; }
    public string? ProviderId { get; set; }
}

public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
    public JsonNode? Extra { get; set; }
}

public sealed class FileChange
{
    public string Path { get; set; } = "";
    public string? Before { get; set; }
    public string After { get; set; } = "";
    public DateTime Time { get; set; }
    public string? MessageId { get; set; }
}

public sealed class ActivityEntry
{
    public DateTime Time { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

// ---------- Configuração ----------

public sealed class AppSettings
{
    public List<ProviderConfig> Providers { get; set; } = new();
    public Dictionary<string, List<RouteEntry>> Chains { get; set; } = new();
    public Dictionary<string, string> Strategies { get; set; } = new(); // failover | roundrobin | leastused
    public MediaSettings Media { get; set; } = new();
    public string Approval { get; set; } = "perguntar";
}

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string DefaultModel { get; set; } = "";
    public List<string> Models { get; set; } = new();
    public bool HasKey { get; set; }
    public string? DeclaredIdentity { get; set; }
}

public sealed class RouteEntry
{
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class MediaSettings
{
    public string? ProviderId { get; set; }
    public string ImageModel { get; set; } = "gemini-2.5-flash-image";
    public string VideoModel { get; set; } = "veo-3.1-generate-preview";
    public string TtsModel { get; set; } = "gemini-2.5-flash-preview-tts";
    public string Voice { get; set; } = "Kore";
    public string Resolution { get; set; } = "720p";
    public bool VideoUsesFrame { get; set; } = true;
    public decimal VideoCostPerSecond { get; set; } = 0.40m;
    public string Currency { get; set; } = "US$";
}

// ---------- Flow ----------

public sealed class FlowOptions
{
    public string Format { get; set; } = "video";
    public int Episodes { get; set; } = 1;
    public int Scenes { get; set; } = 3;
    public int Seconds { get; set; } = 8;
    public string Aspect { get; set; } = "16:9";
    public string? Style { get; set; }
}

public sealed class Storyboard
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Format { get; set; } = "video";
    public int EpisodeCount { get; set; } = 1;
    public int ScenesPerEpisode { get; set; } = 3;
    public int SceneSeconds { get; set; } = 8;
    public string Aspect { get; set; } = "16:9";
    public string Style { get; set; } = "";
    public string Status { get; set; } = "rascunho";
    public string? Error { get; set; }
    public string? Rendering { get; set; }
    public string Logline { get; set; } = "";
    public string? ScriptModel { get; set; }
    public decimal EstimatedSpent { get; set; }
    public string? Music { get; set; }
    public double MusicVolume { get; set; } = 0.28;
    public double TransitionSeconds { get; set; } = 0.4;
    public List<Character> Characters { get; set; } = new();
    public List<Episode> EpisodeList { get; set; } = new();
    public List<ExportItem> Exports { get; set; } = new();
    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }

    public IEnumerable<Scene> AllScenes() => EpisodeList.SelectMany(e => e.Scenes);
}

public sealed class Character
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Image { get; set; }
    public bool Busy { get; set; }
}

public sealed class Episode
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public List<Scene> Scenes { get; set; } = new();
}

public sealed class TakeItem
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "video"; // video | imagem
    public string Path { get; set; } = "";
    public string Origin { get; set; } = "";
    public DateTime Time { get; set; }
}

public sealed class Scene
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Visual { get; set; } = "";
    public string Narration { get; set; } = "";
    public int Seconds { get; set; } = 8;
    public string? Image { get; set; }
    public string? Video { get; set; }
    public string? VideoOrigin { get; set; }
    public string? Audio { get; set; }
    public List<TakeItem> Takes { get; set; } = new();
    public string? ActiveTakeId { get; set; }
    public string Status { get; set; } = "rascunho";
    public string? Busy { get; set; }
    public string? Error { get; set; }
    public string? Operation { get; set; }
    public VideoIntent? VideoIntent { get; set; }
}

public sealed record VideoIntent(string Provider, string Endpoint, string Credential, string Model, string InputHash, decimal Estimate);

public sealed class ExportItem
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public double Seconds { get; set; }
    public DateTime Time { get; set; }
}

public static class Titles
{
    public static string FromPrompt(string prompt)
    {
        var line = prompt.Trim().Split('\n')[0].Trim().TrimEnd('.', ':');
        if (line.Length > 48)
        {
            var cut = line[..48];
            var sp = cut.LastIndexOf(' ');
            line = (sp > 20 ? cut[..sp] : cut) + "…";
        }
        return line.Length == 0 ? "Sem título" : char.ToUpper(line[0]) + line[1..];
    }

    public static string Trunc(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
