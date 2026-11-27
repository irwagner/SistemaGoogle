using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Orbita.Core;

namespace Orbita.Engine;

public sealed class AntigravityEngine : IAgentEngine
{
    private static readonly Regex PlanApproval = new(@"\b(?:(?:aguardo|aguardando|esperando|awaiting|waiting for)\b[^\r\n]{0,55}\b(?:aprova[cç][aã]o|confirma[cç][aã]o|approval)\b[^\r\n]{0,40}\b(?:plano|plan)|(?:aprovar|aprove|confirme|approve)\s+(?:o|the)\s+(?:plano|plan))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public bool Available => LocalTools.Agy() != null;

    public async Task<AgentResult> RunAsync(AgentRequest request, Func<AgentEvent, ValueTask> publish, CancellationToken ct)
    {
        var binary = LocalTools.Agy() ?? throw new StudioException("Instale o Antigravity CLI oficial antes de iniciar uma conversa.");
        var utf8 = new UTF8Encoding(false);
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = request.Workspace,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = utf8, StandardOutputEncoding = utf8, StandardErrorEncoding = utf8
        };
        if (request.Execute || request.DesktopTool != null)
        {
            var settings = new JsonObject();
            if (request.Execute) settings["artifactReviewPolicy"] = "always-proceed";
            if (request.DesktopTool != null)
                settings["permissions"] = new JsonObject { ["allow"] = new JsonArray("mcp(orbita_desktop/open_project_app)") };
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(settings.ToJsonString());
        }
        start.ArgumentList.Add("acp");
        if (request.DesktopTool != null) start.ArgumentList.Add("--process-per-session");
        var fullAccess = request.Execute && request.FullAccess;
        start.ArgumentList.Add(fullAccess ? "--sandbox=false" : "--sandbox");
        if (fullAccess) start.ArgumentList.Add("--dangerously-skip-permissions");
        start.Environment.Remove("CLOUD_CODE_URL");
        if (request.PoolAddress != null)
        {
            foreach (var name in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS", "AGY_ADC_AUTH", "GOOGLE_GEMINI_BASE_URL" })
                start.Environment.Remove(name);
            start.Environment["CLOUD_CODE_URL"] = request.PoolAddress.GetLeftPart(UriPartial.Authority);
        }
        using var process = Process.Start(start) ?? throw new StudioException("O Antigravity CLI não iniciou.");
        process.BeginErrorReadLine();
        try
        {
            var rpc = new AgentConnection(process, request, publish);
            using var setup = CancellationTokenSource.CreateLinkedTokenSource(ct);
            setup.CancelAfter(TimeSpan.FromSeconds(35));
            var info = await rpc.Call("initialize", new { protocolVersion = 1, clientCapabilities = new { },
                clientInfo = new { name = "orbita-studio", title = "Orbita Studio", version = OrbitaRelease.Version } }, setup.Token);
            if ((int?)info?["protocolVersion"] != 1) throw new StudioException("Atualize o Antigravity CLI para usar o ACP versão 1.");
            var capabilities = info?["agentCapabilities"]?["promptCapabilities"];

            var cwd = request.Workspace;
            var session = request.SessionId;
            object[] mcpServers = request.DesktopTool is { } desktop &&
                (bool?)info?["agentCapabilities"]?["mcpCapabilities"]?["http"] == true
                ? [new { type = "http", name = desktop.Name, url = desktop.Url.AbsoluteUri,
                    headers = new[] { new { name = "Authorization", value = "Bearer " + desktop.Key } } }]
                : [];
            var text = request.Text;
            if (mcpServers.Length > 0)
                text += "\n\nFerramenta local do Orbita: quando o usuário pedir para abrir ou mostrar um aplicativo Windows, localize o .exe do worktree e use orbita_desktop/open_project_app. Em conversa, abrir o aplicativo pedido é uma ação autorizada, sem editar arquivos. Informe se a ferramenta confirmou uma janela visível.";
            var config = session == null
                ? await rpc.Call("session/new", new { cwd, mcpServers }, setup.Token)
                : await rpc.Call("session/load", new { sessionId = session, cwd, mcpServers }, setup.Token);
            session ??= (string?)config?["sessionId"] ?? throw new StudioException("O Antigravity não retornou uma sessão.");
            var (model, modelConfig) = await ConfigureModel(rpc, config, session, request.Model, setup.Token);
            var effort = await ConfigureEffort(rpc, modelConfig, session, request.Effort, setup.Token);
            await ConfigureMode(rpc, config, session, request.Execute, fullAccess, setup.Token);
            await publish(new AgentEvent("sessao", "Sessão Antigravity conectada.", new JsonObject { ["sessionId"] = session, ["model"] = model, ["effort"] = effort }));
            rpc.Live = true;
            var prompt = await BuildPromptAsync(text, request.Attachments, capabilities, ct);
            var stopReason = await rpc.Prompt(session, prompt, ct);
            if (request.Execute && stopReason == "end_turn" && rpc.WaitsForPlanApproval)
            {
                await publish(new AgentEvent("estado", "Plano registrado. Execução retomada automaticamente."));
                await publish(new AgentEvent("mensagem", "\n\n"));
                stopReason = await rpc.Prompt(session, [new { type = "text", text = "Pode fazer. A execução deste plano já foi autorizada. Implemente, teste e corrija as falhas. Peça ajuda ao usuário apenas se houver um bloqueio que ele precise resolver." }], ct);
                if (stopReason == "end_turn" && rpc.WaitsForPlanApproval)
                    throw new StudioException("O Antigravity voltou a parar na revisão do plano mesmo após a retomada automática. O trabalho ficou salvo.");
            }
            return new AgentResult(session, model, stopReason, rpc.NeedsUser, rpc.VerificationRequired);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new StudioException("O Antigravity demorou para abrir a sessão. Confira o login e a conexão do CLI."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static async Task<object[]> BuildPromptAsync(string text, IReadOnlyList<AgentAttachment>? attachments, JsonNode? capabilities, CancellationToken ct)
    {
        var blocks = new List<object> { new { type = "text", text } };
        if (attachments is not { Count: > 0 }) return [.. blocks];
        foreach (var attachment in attachments)
        {
            var label = $"Anexo enviado pelo usuário: {attachment.Name}. Use o conteúdo como referência para o pedido.";
            if (attachment.Text is { } document)
            {
                if ((bool?)capabilities?["embeddedContext"] == true)
                {
                    blocks.Add(new { type = "text", text = label });
                    blocks.Add(new { type = "resource", resource = new { uri = new Uri(attachment.Path).AbsoluteUri, mimeType = "text/plain", text = document } });
                }
                else blocks.Add(new { type = "text", text = $"{label}\n\nConteúdo do anexo:\n{document}" });
            }
            else
            {
                if ((bool?)capabilities?["image"] != true)
                    throw new StudioException("Este Antigravity CLI não anuncia suporte a imagens na conversa. Atualize o CLI e tente novamente.");
                var bytes = await File.ReadAllBytesAsync(attachment.Path, ct);
                blocks.Add(new { type = "text", text = label });
                blocks.Add(new { type = "image", mimeType = attachment.MimeType, data = Convert.ToBase64String(bytes) });
            }
        }
        return [.. blocks];
    }

    private static async Task<(string? Model, JsonNode? Config)> ConfigureModel(AgentConnection rpc, JsonNode? config, string session, string? requested, CancellationToken ct)
    {
        var option = (config?["configOptions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => (string?)item["category"] == "model" || (string?)item["id"] == "model");
        var legacy = config?["models"];
        var current = (string?)option?["currentValue"] ?? (string?)legacy?["currentModelId"];
        if (string.IsNullOrWhiteSpace(requested)) return (current, config);
        var options = option?["options"] as JsonArray ?? legacy?["availableModels"] as JsonArray ?? [];
        var matches = ModelOptions(options).Where(row => requested.ToLowerInvariant() switch
        {
            "neon" => row.Id.StartsWith("gemini-4-neon", StringComparison.OrdinalIgnoreCase),
            "opus" => row.Id.StartsWith("claude-opus", StringComparison.OrdinalIgnoreCase),
            _ => row.Id.Contains(requested, StringComparison.OrdinalIgnoreCase) || row.Label.Contains(requested, StringComparison.OrdinalIgnoreCase)
        }).ToArray();
        var model = matches.FirstOrDefault(row => row.Id.Equals(requested, StringComparison.OrdinalIgnoreCase)).Id
            ?? matches.FirstOrDefault(row => row.Id == current).Id ?? matches.FirstOrDefault().Id;
        if (model == null) throw new StudioException($"O Antigravity não ofereceu {requested} para esta sessão ACP. Confira o plano da conta e o catálogo do cliente.");
        if (model == current) return (model, config);
        var reply = option != null
            ? await rpc.Call("session/set_config_option", new { sessionId = session, configId = (string?)option["id"], value = model }, ct)
            : await rpc.Call("session/set_model", new { sessionId = session, modelId = model }, ct);
        return (model, reply?["configOptions"] is JsonArray ? reply : config);
    }

    private static JsonObject? EffortOption(JsonNode? config) => (config?["configOptions"] as JsonArray)?.OfType<JsonObject>()
        .FirstOrDefault(item => (string?)item["id"] is "thought_level" or "effort" || (string?)item["category"] is "thought_level" or "effort");

    private static async Task<string?> ConfigureEffort(AgentConnection rpc, JsonNode? config, string session, string? requested, CancellationToken ct)
    {
        var option = EffortOption(config);
        var current = (string?)option?["currentValue"];
        if (requested == null || requested == current) return current;
        var supported = ModelOptions(option?["options"] as JsonArray ?? []).Select(row => row.Id).ToArray();
        if (!supported.Contains(requested, StringComparer.OrdinalIgnoreCase))
            throw new StudioException(supported.Length == 0 ? "O modelo atual não oferece ajuste de esforço. Selecione Padrão do modelo."
                : $"O modelo atual não oferece o esforço {requested}. Níveis disponíveis: {string.Join(", ", supported)}.");
        var reply = await rpc.Call("session/set_config_option", new { sessionId = session, configId = (string?)option!["id"], value = requested }, ct);
        return (string?)EffortOption(reply)?["currentValue"] ?? requested;
    }

    private static IEnumerable<(string Id, string Label)> ModelOptions(JsonArray options)
    {
        foreach (var item in options.OfType<JsonObject>())
        {
            if (item["options"] is JsonArray child)
            {
                foreach (var row in ModelOptions(child)) yield return row;
            }
            else if (((string?)item["value"] ?? (string?)item["modelId"]) is { } id)
                yield return (id, (string?)item["name"] ?? id);
        }
    }

    private static async Task ConfigureMode(AgentConnection rpc, JsonNode? config, string session, bool execute, bool fullAccess, CancellationToken ct)
    {
        string[] preferred = !execute ? ["plan", "planning", "read-only", "readonly"]
            : fullAccess ? ["accept-edits", "default", "auto"] : ["auto", "accept-edits", "default"];
        var option = (config?["configOptions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(item => (string?)item["category"] == "mode" || (string?)item["id"] == "mode");
        if (option?["options"] is JsonArray options)
        {
            var modes = ModelOptions(options).Select(row => row.Id).ToArray();
            var mode = preferred.FirstOrDefault(value => modes.Contains(value, StringComparer.OrdinalIgnoreCase));
            if (mode != null && mode != (string?)option["currentValue"])
                await rpc.Call("session/set_config_option", new { sessionId = session, configId = (string?)option["id"], value = mode }, ct);
            return;
        }
        var oldModes = config?["modes"];
        var oldOptions = (oldModes?["availableModes"] as JsonArray)?.OfType<JsonObject>()
            .Select(item => (string?)item["id"]).ToArray() ?? [];
        var oldMode = preferred.FirstOrDefault(value => oldOptions.Contains(value, StringComparer.OrdinalIgnoreCase));
        if (oldMode != null && oldMode != (string?)oldModes?["currentModeId"])
            await rpc.Call("session/set_mode", new { sessionId = session, modeId = oldMode }, ct);
    }

    private sealed class AgentConnection(Process process, AgentRequest request, Func<AgentEvent, ValueTask> publish)
    {
        private int sequence;
        private readonly StringBuilder response = new();
        public bool Live { get; set; }
        public bool VerificationRequired => response.ToString().Trim() is "⚠ Verification Required" or "⚠️ Verification Required";
        public bool WaitsForPlanApproval
        {
            get
            {
                var blocker = NeedsUser;
                if (blocker != null) return PlanApproval.IsMatch(blocker);
                var closing = response.ToString().TrimEnd().Split('\n').TakeLast(3);
                return closing.Any(line => PlanApproval.IsMatch(line));
            }
        }
        public string? NeedsUser
        {
            get
            {
                if (!request.Execute) return null;
                const string heading = "Preciso de você:";
                var reply = response.ToString();
                foreach (var line in reply.Split('\n').Reverse())
                {
                    var text = line.Trim().TrimStart('*', '#', '>', ' ');
                    if (!text.StartsWith(heading, StringComparison.OrdinalIgnoreCase)) continue;
                    var reason = text[heading.Length..].Trim('*', ' ');
                    return reason.Length == 0 ? "O Antigravity solicitou uma intervenção. Confira a última resposta no chat." : reason;
                }
                return null;
            }
        }

        public async Task<string> Prompt(string sessionId, object[] prompt, CancellationToken ct)
        {
            response.Clear();
            var result = await Call("session/prompt", new { sessionId, prompt }, ct);
            return (string?)result?["stopReason"] ?? "unknown";
        }

        public async Task<JsonNode?> Call(string method, object arguments, CancellationToken ct)
        {
            var id = ++sequence;
            await Write(new { jsonrpc = "2.0", id, method, @params = arguments }, ct);
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                if (line.Length > 4_000_000) throw new StudioException("O Antigravity enviou uma mensagem maior que o limite ACP.");
                var rpc = JsonNode.Parse(line) as JsonObject;
                if (rpc == null) continue;
                if (rpc["method"] is JsonValue incoming)
                {
                    await Handle(incoming.GetValue<string>(), rpc, ct);
                    continue;
                }
                if ((int?)rpc["id"] != id) continue;
                if (rpc["error"] is JsonNode error)
                {
                    var message = (string?)error["message"] ?? "Falha na comunicação com o Antigravity.";
                    if (message.Contains("Authentication required", StringComparison.OrdinalIgnoreCase))
                        throw new StudioException("Entre com sua conta Google no Antigravity CLI e retome a missão.");
                    throw new StudioException(message[..Math.Min(message.Length, 500)]);
                }
                return rpc["result"];
            }
            throw new StudioException("A sessão do Antigravity foi encerrada antes da resposta.");
        }

        private async ValueTask Handle(string method, JsonObject rpc, CancellationToken ct)
        {
            if (method == "session/update" && Live && rpc["params"]?["update"] is JsonObject update)
            {
                var kind = (string?)update["sessionUpdate"];
                if (kind == "agent_message_chunk" && (string?)update["content"]?["text"] is { } text)
                {
                    response.Append(text);
                    if (response.Length > 32_000) response.Remove(0, response.Length - 32_000);
                    await publish(new AgentEvent("mensagem", text));
                }
                else if (kind == "plan" && update["entries"] is JsonArray entries)
                    await publish(new AgentEvent("plano", "Plano atualizado pelo Antigravity.", entries));
                else if (kind is "tool_call" or "tool_call_update")
                    await publish(new AgentEvent("ferramenta", (string?)update["title"] ?? "Ferramenta do Antigravity",
                        new JsonObject { ["status"] = (string?)update["status"], ["tipo"] = (string?)update["kind"] }));
                return;
            }
            if (rpc["id"] == null) return;
            var id = rpc["id"]!.DeepClone();
            if (method == "session/request_permission")
            {
                var permission = rpc["params"];
                var options = (permission?["options"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
                var tool = permission?["toolCall"];
                var kind = (string?)tool?["kind"];
                var paths = (tool?["locations"] as JsonArray)?.OfType<JsonObject>().Select(item => (string?)item["path"]).ToArray() ?? [];
                var full = request.Execute && request.FullAccess;
                var allowed = full || paths.Length > 0 && paths.All(path => InsideWorkspace(request.Workspace, path)) &&
                    (kind is "read" or "search" || request.Execute && kind == "edit");
                var selected = options.FirstOrDefault(choice => (string?)choice["kind"] == (allowed ? "allow_once" : "reject_once"));
                var option = (string?)selected?["optionId"];
                var approved = allowed && option != null;
                await publish(new AgentEvent("permissao", approved
                    ? full ? "Ferramenta autorizada pelo modo Permissão total." : "Ferramenta autorizada dentro da missão."
                    : "Ação recusada pelo modo atual. O Antigravity pode seguir por outro caminho."));
                await Write(new { jsonrpc = "2.0", id, result = new
                { outcome = option != null ? new JsonObject { ["outcome"] = "selected", ["optionId"] = option } : new JsonObject { ["outcome"] = "cancelled" } } }, ct);
                return;
            }
            await Write(new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Método não disponível no Orbita Studio." } }, ct);
        }

        private async Task Write(object message, CancellationToken ct)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, StudioFiles.Json).AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
    }

    private static bool InsideWorkspace(string workspace, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var normalized = Path.GetFullPath(path, workspace);
            var relative = Path.GetRelativePath(workspace, normalized);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return false;
            var current = workspace;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar))
            {
                if (component.Equals(".git", StringComparison.OrdinalIgnoreCase)) return false;
                current = Path.Combine(current, component);
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
}
