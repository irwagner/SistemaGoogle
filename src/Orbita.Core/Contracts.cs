using System.Text.Json.Nodes;

namespace Orbita.Core;

public static class OrbitaRelease
{
    public const string Version = "0.3.17";
}

public sealed class StudioException(string message) : Exception(message);

public sealed record StudioProject(string Id, string Name, string Workspace, bool Managed);
public sealed record Mission(string Id, string ProjectId, string Name, string Workspace, string Branch,
    string Baseline, DateTimeOffset Updated, string State = "pronta", string? SessionId = null,
    string? Model = null, string? Pending = null, string? Revision = null, string? Accepted = null,
    string? RequestedModel = null, string[]? AccountIds = null, bool UseSkill = false, bool FullAccess = false,
    MissionAttachment[]? PendingAttachments = null, string? RequestedEffort = null, string? Effort = null);
public sealed record MissionEvent(long Sequence, DateTimeOffset At, string Kind, string Text, JsonNode? Data = null);
public sealed record MissionAttachment(string Id, string Name, string MimeType, long Size, bool Truncated = false);
public sealed record GitChanges(string Status, string Diff, string Revision);

public interface ICheckpointStore
{
    IReadOnlyList<Mission> List();
    Mission Get(string id);
    IReadOnlyList<Mission> History(string id);
    void Save(Mission mission, bool checkpoint = false);
    MissionEvent Append(string missionId, string kind, string text, JsonNode? data = null);
    IReadOnlyList<MissionEvent> EventsAfter(string missionId, long sequence);
}

public interface IGitSnapshotService
{
    bool Available { get; }
    Task<Mission> CreateWorktreeAsync(StudioProject project, string id, string workspace, string name, CancellationToken ct);
    Task<string> CaptureAsync(Mission mission, string message, CancellationToken ct);
    Task<GitChanges> DiffAsync(Mission mission, CancellationToken ct);
    Task<string> RestoreAsync(Mission mission, CancellationToken ct);
}

public sealed record AgentMcpServer(string Name, Uri Url, string Key);
public sealed record AgentAttachment(string Name, string MimeType, string Path, string? Text = null);
public sealed record AgentRequest(string Workspace, string Text, bool Execute, string? SessionId, string? Model,
    Uri? PoolAddress = null, bool FullAccess = false, AgentMcpServer? DesktopTool = null,
    IReadOnlyList<AgentAttachment>? Attachments = null, string? Effort = null);
public sealed record AgentEvent(string Kind, string Text, JsonNode? Data = null);
public sealed record AgentResult(string SessionId, string? Model, string StopReason, string? NeedsUser = null,
    bool VerificationRequired = false);

public interface IAgentEngine
{
    bool Available { get; }
    Task<AgentResult> RunAsync(AgentRequest request, Func<AgentEvent, ValueTask> publish, CancellationToken ct);
}

public sealed record QuotaWindow(int Percent, DateTimeOffset? ResetTime);
public sealed record QuotaFamily(QuotaWindow? FiveHour, QuotaWindow? Weekly);
public sealed record QuotaAccount(string Id, string Label, string Email, QuotaFamily Gemini, QuotaFamily Claude,
    long BlockedUntil, long GeminiBlockedUntil, long ClaudeBlockedUntil, bool Observed, bool VerificationRequired = false);

public interface IQuotaPolicy
{
    bool IsReady(QuotaAccount account, bool gemini, int reserve, DateTimeOffset now);
}
