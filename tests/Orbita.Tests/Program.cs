using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbita.Core;
using Orbita.Engine;
using Orbita.Pool;

if (args.Contains("acp")) { await Fixture(); return; }

var root = Directory.CreateTempSubdirectory("orbita-test-");
try
{
    var store = new StudioFiles(root.FullName);
    var project = store.CreateProject("Projeto de ensaio", null);
    store.WriteProjectFile(project.Id, "SPEC.md", "# Escopo de teste");
    Check(new StudioFiles(root.FullName).ReadProjectFile(project.Id, "SPEC.md") == "# Escopo de teste", "contexto reaberto");
    var git = new GitSnapshotService(store);
    var id = StudioFiles.NewId();
    var mission = await git.CreateWorktreeAsync(project, id, Path.Combine(store.MissionPath(id), "workspace"), "Missão de ensaio", default);
    store.Save(mission, checkpoint: true);
    store.Append(id, "estado", "Criada");
    Check(new StudioFiles(root.FullName).EventsAfter(id, 0).Count == 1, "eventos persistidos");
    await Task.WhenAll(Task.Run(() =>
    {
        for (var i = 0; i < 40; i++) store.Save(mission);
    }), Task.Run(() =>
    {
        for (var i = 0; i < 80; i++) Check(store.Get(id).Id == id, "leitura concorrente com checkpoint");
    }));

    Environment.SetEnvironmentVariable("ORBITA_AGY_PATH", Environment.ProcessPath);
    var engine = new AntigravityEngine();
    var events = new List<AgentEvent>();
    ValueTask Record(AgentEvent entry) { events.Add(entry); return ValueTask.CompletedTask; }
    var chat = await engine.RunAsync(new(mission.Workspace, "Planeje uma soma", false, null, "neon"), Record, default);
    var output = Path.Combine(mission.Workspace, "soma.mjs");
    Check(!File.Exists(output), "modo conversa não altera worktree");
    var result = await engine.RunAsync(new(mission.Workspace, "Planeje, implemente", true, chat.SessionId, "neon"), Record, default);
    Check(result.SessionId == chat.SessionId && result.NeedsUser == null && File.Exists(output), "ACP retoma e edita via CLI de teste");
    Check(events.Any(entry => entry.Text == "CLI de ensaio · auto com sandbox"), "auto do ACP escolhido antes de accept-edits");
    Check(events.Any(entry => entry.Kind == "plano") && events.Any(entry => entry.Text == "Teste Node passou"), "eventos reais do CLI de teste");
    Check(!File.Exists(Path.Combine(project.Workspace, "soma.mjs")), "pasta original isolada");
    var after = await git.CaptureAsync(mission, "Final", default);
    Check(after != mission.Baseline && (await git.DiffAsync(mission, default)).Diff.Contains("soma.mjs"), "diff Git");
    _ = await git.RestoreAsync(mission, default);
    Check(!File.Exists(output) && (await git.DiffAsync(mission, default)).Diff.Length == 0, "reversão com histórico");

    events.Clear();
    _ = await engine.RunAsync(new(mission.Workspace, "Planeje uma soma", false, chat.SessionId, "neon", FullAccess: true), Record, default);
    Check(events.Any(entry => entry.Text == "CLI de ensaio · plan com sandbox"), "conversa mantém sandbox mesmo com permissão total selecionada");
    events.Clear();
    var full = await engine.RunAsync(new(mission.Workspace, "Planeje, implemente", true, chat.SessionId, "neon", FullAccess: true), Record, default);
    Check(full.NeedsUser == null && events.Any(entry => entry.Text == "CLI de ensaio · permissão total") &&
        events.Any(entry => entry.Text == "Modo de execução · accept-edits"), "permissão total sai do modo de planejamento");
    events.Clear();
    var continued = await engine.RunAsync(new(mission.Workspace, "Aprovação de ensaio", true, chat.SessionId, "neon", FullAccess: true), Record, default);
    Check(continued.NeedsUser == null && events.Any(entry => entry.Text == "Plano registrado. Execução retomada automaticamente.") &&
        events.Any(entry => entry.Text == "Teste Node passou"), "pede o plano e continua automaticamente na mesma sessão");
    var blocked = await engine.RunAsync(new(mission.Workspace, "Intervenção de ensaio", true, chat.SessionId, "neon"), Record, default);
    Check(blocked.NeedsUser == "informe o nome de teste.", "intervenção reconhecida com texto em fragmentos ACP");
    var verification = await engine.RunAsync(new(mission.Workspace, "Ensaio de verificação do Google", false, chat.SessionId, "neon"), Record, default);
    Check(verification.VerificationRequired, "verificação do Google reconhecida mesmo em conversa");
    try { await engine.RunAsync(new(mission.Workspace, "Pedido", false, null, "opus"), Record, default); throw new Exception("Opus indisponível deveria falhar"); }
    catch (StudioException e) { Check(e.Message.Contains("opus", StringComparison.OrdinalIgnoreCase), "modelo restrito é reportado"); }

    var now = DateTimeOffset.UtcNow;
    var low = new QuotaFamily(new(7, now.AddHours(2)), new(83, now.AddDays(3)));
    var high = new QuotaFamily(new(89, now.AddHours(3)), new(91, now.AddDays(4)));
    var account = new QuotaAccount("a", "Conta A", "a@example.test", low, high, 0, 0, 0, true);
    var quotas = new QuotaPolicy();
    Check(!quotas.IsReady(account, true, 10, now) && quotas.IsReady(account, false, 10, now), "cotas independentes por família");
    Check(quotas.IsReady(account, true, 10, now.AddHours(3)), "renovação encerra reserva");
    Console.WriteLine("Orbita: persistência, ACP, Git e política de cotas verificados.");
}
finally
{
    foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
        if (file.IsReadOnly) file.IsReadOnly = false;
    root.Delete(recursive: true);
}

static void Check(bool test, string scenario)
{
    if (!test) throw new Exception("Falhou: " + scenario);
}

static async Task Fixture()
{
    const string sessionId = "orbita-sessao-ensaio";
    var arguments = Environment.GetCommandLineArgs();
    var full = arguments.Contains("--dangerously-skip-permissions");
    Check(arguments.Contains("--sandbox") != full && arguments.Contains("--sandbox=false") == full,
        "política de sandbox passada ao CLI");
    var approvalFlag = Array.IndexOf(arguments, "--settings");
    var review = approvalFlag >= 0 && (string?)JsonNode.Parse(arguments[approvalFlag + 1])?["artifactReviewPolicy"] == "always-proceed";
    var mode = "default";
    var effort = "high";
    JsonObject? desktop = null;
    object Settings() => new
    {
        sessionId,
        configOptions = new[]
        {
            new { id = "model", category = "model", currentValue = "gemini-4-neon", options = new[] { new { value = "gemini-4-neon", name = "Gemini 4 Neon" } } },
            new { id = "thought_level", category = "thought_level", currentValue = effort,
                options = new[] { "low", "medium", "high", "xhigh" }.Select(value => new { value, name = value }).ToArray() },
            new { id = "mode", category = "mode", currentValue = mode,
                options = new[] { "accept-edits", "auto", "plan" }.Select(value => new { value, name = value }).ToArray() }
        }
    };
    async Task Send(object frame) => await Console.Out.WriteLineAsync(JsonSerializer.Serialize(frame, StudioFiles.Json));
    Task Update(object update) => Send(new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId, update } });
    async Task AskEdit(string path, string expected)
    {
        await Send(new { jsonrpc = "2.0", id = "permission", method = "session/request_permission", @params = new
        {
            sessionId, toolCall = new { kind = "edit", locations = new[] { new { path } } },
            options = new[] { new { kind = "allow_once", optionId = "allow" }, new { kind = "reject_once", optionId = "reject" } }
        } });
        var permission = JsonNode.Parse(await Console.In.ReadLineAsync() ?? "{}");
        Check((string?)permission?["result"]?["outcome"]?["optionId"] == expected, "política de edição retornada pelo cliente ACP");
    }
    async Task<string> OpenDesktopApp()
    {
        Check(desktop != null && arguments.Contains("--process-per-session"), "Orbita registrou MCP HTTP no ACP");
        var url = (string)desktop!["url"]!;
        var header = desktop["headers"]![0]!;
        using var http = new HttpClient();
        async Task<JsonNode?> Call(string method, object? parameters, bool authorized = true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), System.Text.Encoding.UTF8, "application/json");
            if (authorized) request.Headers.TryAddWithoutValidation((string)header["name"]!, (string)header["value"]!);
            using var response = await http.SendAsync(request);
            if (!authorized) { Check(response.StatusCode == System.Net.HttpStatusCode.Unauthorized, "MCP exige chave da execução"); return null; }
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())?["result"];
        }
        await Call("tools/list", null, authorized: false);
        var tools = await Call("tools/list", null);
        Check((string?)tools?["tools"]?[0]?["name"] == "open_project_app", "ferramenta de desktop publicada");
        Task<JsonNode?> Open(string path) => Call("tools/call", new { name = "open_project_app", arguments = new { path } });
        var invalid = await Open(Path.Combine(Environment.SystemDirectory, "where.exe"));
        Check((bool?)invalid?["isError"] == true, "ferramenta bloqueou executável fora da missão");
        var opened = await Open("orbita-launch-probe.exe");
        Check((bool?)opened?["isError"] == false, "ferramenta iniciou executável da missão");
        return (string)opened!["content"]![0]!["text"]!;
    }
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var message = JsonNode.Parse(line)!;
        var id = (int?)message["id"];
        object response = new { };
        switch ((string?)message["method"])
        {
            case "initialize": response = new { protocolVersion = 1, agentCapabilities = new
                { loadSession = true, mcpCapabilities = new { http = true }, promptCapabilities = new { image = true, embeddedContext = true } } }; break;
            case "session/new":
                desktop = (message["params"]?["mcpServers"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault();
                response = Settings();
                break;
            case "session/load":
                desktop = (message["params"]?["mcpServers"] as JsonArray)?.OfType<JsonObject>().SingleOrDefault();
                Check((string?)message["params"]?["sessionId"] == sessionId, "ID da sessão retomada");
                await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Mensagem já recebida" } });
                mode = "plan";
                response = Settings();
                break;
            case "session/set_config_option":
                if ((string?)message["params"]?["configId"] == "mode") mode = (string?)message["params"]?["value"] ?? "default";
                if ((string?)message["params"]?["configId"] == "thought_level") effort = (string?)message["params"]?["value"] ?? "high";
                response = Settings();
                break;
            case "session/prompt":
                var prompt = (string?)message["params"]?["prompt"]?[0]?["text"] ?? "";
                if (prompt.Contains("ORB-SENTINELA-PEDIDO-LONGO", StringComparison.Ordinal))
                    Check(prompt.Length > 32_000 && effort == "xhigh", "pedido longo completo e esforço Extra recebidos pelo ACP");
                await Update(new { sessionUpdate = "tool_call_update", title = full ? "CLI de ensaio · permissão total" : $"CLI de ensaio · {mode} com sandbox", status = "completed", kind = "other" });
                await Update(new { sessionUpdate = "tool_call_update", title = $"Modo de execução · {mode}", status = "completed", kind = "other" });
                await Update(new { sessionUpdate = "plan", entries = new[] { new { content = "Criar e verificar soma.mjs", status = "in_progress" } } });
                if (prompt.Contains("Ensaio de arquivos anexados", StringComparison.Ordinal) ||
                    prompt.Contains("Analise os anexos e resuma o que encontrou.", StringComparison.Ordinal))
                {
                    var blocks = (message["params"]?["prompt"] as JsonArray)!.OfType<JsonObject>().ToArray();
                    var image = blocks.Single(block => (string?)block["type"] == "image");
                    Check((string?)image["mimeType"] == "image/png" && Convert.FromBase64String((string)image["data"]!).Length > 50,
                        "imagem enviada como dados visuais ACP");
                    var mixed = prompt.Contains("Ensaio de arquivos anexados", StringComparison.Ordinal);
                    if (mixed)
                    {
                        var resources = blocks.Where(block => (string?)block["type"] == "resource").Select(block => (string)block["resource"]!["text"]!).ToArray();
                        Check(resources.Length == 4, "quatro documentos incorporados ao ACP");
                        foreach (var expected in new[] { "DOCX-SENTINELA", "PDF-SENTINELA", "XLSX-SENTINELA", "CSV-SENTINELA" })
                            Check(resources.Any(resource => resource.Contains(expected, StringComparison.Ordinal)), "leitura do " + expected);
                    }
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new
                        { text = mixed ? "Imagem e quatro documentos recebidos." : "Captura de tela recebida." } });
                }
                else if (prompt.Contains("Ensaio de retomar com anexo", StringComparison.Ordinal))
                {
                    var blocks = (message["params"]?["prompt"] as JsonArray)!.OfType<JsonObject>();
                    var resource = blocks.Single(block => (string?)block["type"] == "resource");
                    Check(((string)resource["resource"]!["text"]!).Contains("CSV-SENTINELA", StringComparison.Ordinal), "anexo preservado na retomada");
                    const string marker = ".retomada-ensaio";
                    var previous = File.Exists(marker);
                    if (!previous) await File.WriteAllTextAsync(marker, "esperando retomada");
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new
                        { text = previous ? "Retomei com o CSV original." : "Preciso de você: retome o pedido de ensaio." } });
                }
                else if (prompt.Contains("Ensaio de abrir app no Windows", StringComparison.Ordinal))
                {
                    var text = await OpenDesktopApp();
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text } });
                }
                else if (prompt.Contains("Ensaio de leitura do chat", StringComparison.Ordinal))
                {
                    if (prompt.Contains("Ensaio de leitura do chat pelo Signal", StringComparison.Ordinal))
                    {
                        var blocks = (message["params"]?["prompt"] as JsonArray)!.OfType<JsonObject>().ToArray();
                        Check(blocks.Any(block => (string?)block["text"] == "Anexo enviado pelo usuário: extrato do Signal.csv. Use o conteúdo como referência para o pedido."), "nome original do anexo Signal");
                        Check(blocks.Any(block => (string?)block["type"] == "resource" && ((string?)block["resource"]?["text"])?.Contains("SIGNAL-CSV-SENTINELA", StringComparison.Ordinal) == true), "documento do Signal entregue ao ACP");
                    }
                    var intro = "### Resposta organizada\n\n**Resultado parcial**\n\n- Arquivo criado\n- Teste executado\n\n```js\nconsole.log('ok');\n```\n\n<img src=invalid onerror=alert(1)>\n\n";
                    var history = string.Join("\n\n", Enumerable.Range(1, 32).Select(n => $"Parágrafo inicial {n} da resposta longa."));
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = intro + history } });
                    await Task.Delay(2600);
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "\n\nChegou outra parte da resposta." } });
                    await Task.Delay(1600);
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "\n\nFim do streaming." } });
                }
                else if (prompt.Contains("Aprovação de ensaio", StringComparison.Ordinal))
                {
                    Check(review && mode != "plan", "execução aplica política de revisão de plano");
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Aguardo a aprovação do plano para implementar." } });
                }
                else if (prompt.Contains("Intervenção de ensaio", StringComparison.Ordinal))
                {
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Preciso de " } });
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "você: informe o nome de teste." } });
                }
                else if (prompt.Contains("Ensaio de verificação do Google", StringComparison.Ordinal))
                {
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "\n⚠ " } });
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Verification Required\n" } });
                }
                else if (prompt.Contains("Planeje, implemente", StringComparison.Ordinal) || prompt.Contains("Planeje e siga imediatamente", StringComparison.Ordinal) ||
                    prompt.StartsWith("Pode fazer. A execução deste plano", StringComparison.Ordinal))
                {
                    Check(review && mode != "plan", "turno de execução prossegue sem revisar plano");
                    if (prompt.Contains("Pode fazer.", StringComparison.Ordinal)) await Task.Delay(1500);
                    var path = Path.Combine(Environment.CurrentDirectory, "soma.mjs");
                    await AskEdit(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", "fora-do-worktree.txt")), full ? "allow" : "reject");
                    await AskEdit(path, "allow");
                    await File.WriteAllTextAsync(path, "export const soma = (a, b) => a + b;\nif (soma(2, 3) !== 5) throw new Error('soma');\n");
                    var node = LocalTools.Locate("node") ?? throw new Exception("Node é necessário para o teste do artefato.");
                    var test = new ProcessStartInfo(node) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    test.ArgumentList.Add(path);
                    using (var process = Process.Start(test)!) { await process.WaitForExitAsync(); Check(process.ExitCode == 0, "teste de soma"); }
                    await Update(new { sessionUpdate = "tool_call_update", title = "Teste Node passou", status = "completed", kind = "execute" });
                    await Update(new { sessionUpdate = "plan", entries = new[] { new { content = "Criar e verificar soma.mjs", status = "completed" } } });
                    await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Criei soma.mjs e o teste passou." } });
                }
                else await Update(new { sessionUpdate = "agent_message_chunk", content = new { text = "Proponho criar uma função soma e testar o resultado." } });
                response = new { stopReason = "end_turn" };
                break;
        }
        await Send(new { jsonrpc = "2.0", id, result = response });
    }
}
