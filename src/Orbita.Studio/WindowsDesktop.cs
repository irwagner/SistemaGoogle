using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Orbita.Core;

namespace Orbita.Studio;

public sealed record OpenProjectApp(string? Path);
public sealed record DesktopLaunch(string Path, int Pid, bool VisibleWindow)
{
    public string Message => VisibleWindow
        ? $"Janela de {System.IO.Path.GetFileName(Path)} aberta na área de trabalho (PID {Pid})."
        : $"{System.IO.Path.GetFileName(Path)} foi iniciado (PID {Pid}). O Orbita não identificou uma janela visível.";
}

public sealed class WindowsDesktop(StudioFiles files, Uri endpoint)
{
    private Turn? active;

    public static bool Available => OperatingSystem.IsWindows() && Environment.UserInteractive;

    public Turn Begin(Mission mission, CancellationToken ct)
    {
        var server = new AgentMcpServer("orbita_desktop", endpoint, RandomNumberGenerator.GetHexString(64));
        var turn = new Turn(this, mission, server, ct);
        Interlocked.Exchange(ref active, turn);
        return turn;
    }

    public async Task<DesktopLaunch> OpenAsync(Mission mission, string? path, CancellationToken ct)
    {
        if (!Available) throw new StudioException("Abra o Orbita em uma sessão gráfica do Windows para usar esta ferramenta.");
        var executable = Resolve(mission.Workspace, path);
        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Normal
            }) ?? throw new StudioException("O Windows não iniciou o aplicativo.");
            var pid = process.Id;
            var visible = false;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                process.Refresh();
                if (process.HasExited) break;
                if (process.MainWindowHandle != 0) { visible = true; break; }
                await Task.Delay(200, ct);
            }
            var result = new DesktopLaunch(executable, pid, visible);
            files.Append(mission.Id, "ferramenta", result.Message,
                new JsonObject { ["path"] = executable, ["pid"] = pid, ["visibleWindow"] = visible });
            return result;
        }
        catch (Win32Exception error)
        { throw new StudioException($"O Windows não conseguiu abrir {Path.GetFileName(executable)} (erro {error.NativeErrorCode})."); }
    }

    private static string Resolve(string workspace, string? input)
    {
        const string invalid = "Informe um arquivo .exe dentro do worktree da missão.";
        var value = StudioFiles.Required(input, 2_048, invalid).Trim('"');
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) throw new StudioException(invalid);
            value = uri.LocalPath;
        }
        try
        {
            var target = Path.GetFullPath(value, workspace);
            var relative = Path.GetRelativePath(workspace, target);
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(relative) || relative.Contains(':') || !File.Exists(target) ||
                (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
                throw new StudioException(invalid);
            var current = workspace;
            foreach (var part in relative.Split(Path.DirectorySeparatorChar))
            {
                if (part == ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new StudioException(invalid);
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new StudioException(invalid);
            }
            return target;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new StudioException(invalid); }
    }

    public async Task<IResult> McpAsync(HttpContext http)
    {
        var turn = Volatile.Read(ref active);
        var authorization = http.Request.Headers.Authorization.ToString();
        if (turn == null || http.Request.Headers.ContainsKey("Origin") ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization), Encoding.UTF8.GetBytes("Bearer " + turn.Server.Key)))
            return Results.Unauthorized();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, turn.Cancellation);
        var request = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: stop.Token) as JsonObject;
        if (request == null) return Results.BadRequest();
        var id = request["id"];
        if (id == null) return Results.StatusCode(202);
        var method = (string?)request["method"];
        var parameters = request["params"] as JsonObject;
        var toolName = (string?)parameters?["name"];
        object result;
        switch (method)
        {
            case "initialize":
                var requested = (string?)parameters?["protocolVersion"];
                result = new
                {
                    protocolVersion = requested is "2025-03-26" or "2025-06-18" or "2025-11-25" ? requested : "2025-03-26",
                    capabilities = new { tools = new { } }, serverInfo = new { name = turn.Server.Name, version = OrbitaRelease.Version }
                };
                break;
            case "ping": result = new { }; break;
            case "tools/list":
                result = new { tools = new[] { new
                {
                    name = "open_project_app",
                    description = "Abre um aplicativo .exe do worktree na área de trabalho visível do usuário. Use quando o usuário pedir para abrir o aplicativo. Retorna o PID e se encontrou uma janela.",
                    inputSchema = new { type = "object", properties = new { path = new { type = "string", description = "Caminho do .exe no worktree, relativo ou absoluto." } }, required = new[] { "path" }, additionalProperties = false }
                } } };
                break;
            case "tools/call" when toolName == "open_project_app":
                try
                {
                    var launch = await OpenAsync(turn.Mission, (string?)(parameters?["arguments"] as JsonObject)?["path"], stop.Token);
                    result = ToolReply(launch.Message, false);
                }
                catch (StudioException error) { result = ToolReply(error.Message, true); }
                break;
            default:
                return Results.Json(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Método não disponível." } });
        }
        return Results.Json(new { jsonrpc = "2.0", id, result });
    }

    private static object ToolReply(string text, bool isError) => new { content = new[] { new { type = "text", text } }, isError };

    public sealed class Turn(WindowsDesktop owner, Mission mission, AgentMcpServer server, CancellationToken ct) : IDisposable
    {
        public Mission Mission { get; } = mission;
        public AgentMcpServer Server { get; } = server;
        public CancellationToken Cancellation { get; } = ct;
        public void Dispose() => Interlocked.CompareExchange(ref owner.active, null, this);
    }
}
