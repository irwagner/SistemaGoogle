using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CentralIA;

/// <summary>Canal de eventos em tempo real (SSE) por conversa ou storyboard.</summary>
public sealed class EventHub
{
    private static readonly JsonSerializerOptions WireJson = new(Store.Json) { WriteIndented = false };
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<string>>> subs = new();

    public (Guid Id, ChannelReader<string> Reader) Subscribe(string topic)
    {
        var ch = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        subs.GetOrAdd(topic, _ => new())[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(string topic, Guid id)
    {
        if (subs.TryGetValue(topic, out var d) && d.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public void Publish(string topic, object payload)
    {
        if (!subs.TryGetValue(topic, out var d) || d.IsEmpty) return;
        var json = JsonSerializer.Serialize(payload, WireJson);
        foreach (var ch in d.Values) ch.Writer.TryWrite(json);
    }
}

public sealed class RunState
{
    public CancellationTokenSource Cts { get; } = new();
    public string Streaming = "";
    public RouteInfo? Route;
    public string? Status;
    public PendingApproval? Pending;
}

public sealed class PendingApproval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Command { get; init; } = "";
    public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Agente de desenvolvimento com 3 modos:
/// - conversa: chat livre
/// - agente: 1 agente autônomo com ferramentas de arquivo e PowerShell
/// - equipe: Coordenador cria plano de subtarefas e dispara workers em paralelo distribuídos no Pool de contas
/// </summary>
public sealed partial class AgentService(Store store, SettingsService settings, LlmRouter router, EventHub hub, Continuity continuity, ILogger<AgentService> log)
{
    private readonly ConcurrentDictionary<string, RunState> runs = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxSteps = 60;
    private const int MaxWorkerSteps = 18;

    public RunState? Run(string tid) => runs.TryGetValue(tid, out var r) ? r : null;

    public object LiveState(string tid)
    {
        var r = Run(tid);
        if (r == null) return new { running = false };
        return new
        {
            running = true,
            streaming = r.Streaming,
            route = r.Route,
            status = r.Status,
            pending = r.Pending == null ? null : new { id = r.Pending.Id, command = r.Pending.Command },
        };
    }

    public void Start(ChatThread t, string text)
    {
        if (t.Continuation != null) { continuity.Start(t, text.Trim()); return; }
        if (t.ReviewedWork != null) { StartReviewed(t, text.Trim()); return; }
        if (!continuity.Reserve(t)) throw new UserError("Já existe uma etapa em execução neste projeto.");
        var state = new RunState();
        var msg = new ChatMessage { Id = Store.NewId(), Role = "user", Content = text.Trim(), Time = DateTime.UtcNow };
        try
        {
            if (!runs.TryAdd(t.Id, state)) throw new UserError("A IA ainda está trabalhando nesta conversa.");
            store.Mutate(t, th =>
            {
                th.Messages.Add(msg);
                if (th.Messages.Count(m => m.Role == "user") == 1 && (th.Title is "Nova conversa" or "")) th.Title = Titles.FromPrompt(text);
            });
            store.TouchProject(t.ProjectId);
            hub.Publish(t.Id, new { type = "message", message = msg });
            hub.Publish(t.Id, new { type = "run", running = true, title = t.Title });
            _ = Task.Run(() => RunEntry(t, state, text.Trim()));
        }
        catch { runs.TryRemove(t.Id, out _); continuity.Release(t); state.Cts.Dispose(); throw; }
    }

    public bool Stop(string tid)
    {
        if (!runs.TryGetValue(tid, out var r)) return false;
        r.Cts.Cancel();
        r.Pending?.Decision.TrySetResult(false);
        return true;
    }

    public bool Decide(string tid, string approvalId, bool approve)
    {
        if (!runs.TryGetValue(tid, out var r) || r.Pending?.Id != approvalId) return false;
        return r.Pending.Decision.TrySetResult(approve);
    }

    private async Task RunEntry(ChatThread t, RunState state, string userPrompt)
    {
        var ct = state.Cts.Token;
        try
        {
            if (t.Mode == "equipe") await RunTeam(t, state, userPrompt, ct);
            else await LoopSingle(t, state, ct);
        }
        catch (OperationCanceledException)
        {
            if (state.Streaming.Length > 0)
            {
                var partial = new ChatMessage { Id = Store.NewId(), Role = "assistant", Content = state.Streaming + "\n\n_(interrompido)_", Time = DateTime.UtcNow, Model = state.Route?.Model, Provider = state.Route?.ProviderName };
                store.Mutate(t, th => th.Messages.Add(partial));
                hub.Publish(t.Id, new { type = "message", message = partial });
            }
            Notice(t, "Você parou a IA.", "info");
        }
        catch (UserError e) { Notice(t, e.Message, "erro"); }
        catch (Exception e)
        {
            log.LogError(e, "Falha no agente");
            Notice(t, "Erro inesperado: " + e.Message, "erro");
        }
        finally
        {
            runs.TryRemove(t.Id, out _);
            continuity.Release(t);
            store.TouchProject(t.ProjectId);
            hub.Publish(t.Id, new { type = "run", running = false });
        }
    }

    // ---------- Modo 1 e 2: Conversa / Agente único ----------

    private async Task LoopSingle(ChatThread t, RunState state, CancellationToken ct)
    {
        var agent = t.Mode != "conversa";
        for (var step = 0; step < MaxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();
            var messages = BuildMessages(t, agent);
            state.Streaming = "";
            state.Status = "pensando";
            hub.Publish(t.Id, new { type = "status", text = "pensando" });

            var ws = store.Workspace(t.ProjectId);
            var hasCli = settings.Current.Providers.Any(p => p.Enabled && p.Kind == "cli");
            var beforeSnap = agent && hasCli ? SnapshotWorkspace(ws) : null;

            var (result, route) = await router.ChatAsync(agent ? "codigo" : "conversa", messages, agent ? AgentTools.Schema : null,
                onStart: r =>
                {
                    state.Route = r; state.Streaming = "";
                    hub.Publish(t.Id, new { type = "route", route = r });
                    return Task.CompletedTask;
                },
                onDelta: d =>
                {
                    state.Streaming += d;
                    hub.Publish(t.Id, new { type = "delta", text = d });
                    return Task.CompletedTask;
                },
                onNotice: n => { Notice(t, n, "fallback"); return Task.CompletedTask; },
                ct, t.PinnedProviderId, t.PinnedModel, agent ? ws : null);

            var assistant = new ChatMessage
            {
                Id = Store.NewId(), Role = "assistant", Content = result.Content, Time = DateTime.UtcNow,
                Model = route.Model, Provider = route.ProviderName,
                ToolCalls = result.ToolCalls.Count > 0 ? result.ToolCalls : null,
            };
            if (beforeSnap != null && settings.Current.Providers.FirstOrDefault(p => p.Id == route.ProviderId)?.Kind == "cli")
                DiffWorkspace(t, ws, beforeSnap, assistant.Id, null);
            state.Streaming = "";
            store.Mutate(t, th => th.Messages.Add(assistant));
            hub.Publish(t.Id, new { type = "message", message = assistant });

            if (result.ToolCalls.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(result.Content) && step < MaxSteps - 1)
                {
                    Notice(t, "O modelo respondeu vazio. Pedindo para continuar…", "info");
                    continue;
                }
                break;
            }

            foreach (var call in result.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                state.Status = AgentTools.Describe(call);
                hub.Publish(t.Id, new { type = "status", text = state.Status });
                var (ok, output) = await Execute(t, state, call, assistant.Id, null, ct);
                var toolMsg = new ChatMessage
                {
                    Id = Store.NewId(), Role = "tool", ToolCallId = call.Id, ToolName = call.Name,
                    Content = output, Ok = ok, Time = DateTime.UtcNow,
                };
                store.Mutate(t, th => th.Messages.Add(toolMsg));
                hub.Publish(t.Id, new { type = "message", message = toolMsg });
            }
            if (step == MaxSteps - 1) Notice(t, $"Parei após {MaxSteps} etapas para você revisar. Mande “continue” para seguir.", "info");
        }
    }

    // ---------- Modo 3: Equipe (/teamwork) com workers paralelos ----------

    private async Task RunTeam(ChatThread t, RunState state, string userPrompt, CancellationToken ct)
    {
        var ws = store.Workspace(t.ProjectId);
        var files = Store.ListFiles(ws, 120);
        state.Status = "Coordenador montando plano da equipe…";
        hub.Publish(t.Id, new { type = "status", text = state.Status });

        var planSys = """
            Você é o Coordenador de uma equipe de engenheiros de IA paralelos.
            Analise o pedido do usuário e quebre o trabalho em 2 a 6 tarefas bem delimitadas que podem rodar em paralelo quando não dependerem umas das outras.
            Regra essencial: duas tarefas paralelas (sem dependência entre si) NÃO devem editar o mesmo arquivo.
            Responda APENAS com JSON válido neste formato:
            {"overview":"resumo curto em pt-BR do plano","tasks":[{"id":"T1","title":"Título curto","description":"Instrução completa, contratos/nomes de arquivos e o que entregar","dependsOn":[]}]}
            Para sites/apps web, garanta que exista um index.html completo e funcional na raiz.
            """;
        var planUser = new StringBuilder();
        planUser.AppendLine("Pedido do usuário: " + userPrompt);
        planUser.AppendLine(files.Count == 0 ? "Workspace atual: vazio." : "Arquivos atuais:\n" + string.Join("\n", files.Select(f => "- " + f.Path)));

        var planMsgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = planSys },
            new JsonObject { ["role"] = "user", ["content"] = planUser.ToString() },
        };

        var (planRes, planRoute) = await router.ChatAsync("codigo", planMsgs, null,
            r => { state.Route = r; hub.Publish(t.Id, new { type = "route", route = r }); return Task.CompletedTask; },
            null,
            n => { Notice(t, n, "fallback"); return Task.CompletedTask; },
            ct, t.PinnedProviderId, t.PinnedModel);

        var parsed = ExtractJson(planRes.Content);
        var rawTasks = parsed?["tasks"] as JsonArray;
        if (rawTasks == null || rawTasks.Count == 0)
        {
            Notice(t, "O Coordenador optou por execução direta de agente.", "info");
            await LoopSingle(t, state, ct);
            return;
        }

        var tasks = new List<TeamTask>();
        foreach (var node in rawTasks.OfType<JsonObject>())
        {
            var id = ((string?)node["id"] ?? $"T{tasks.Count + 1}").Trim();
            var deps = (node["dependsOn"] as JsonArray ?? new()).Select(x => ((string?)x ?? "").Trim()).Where(x => x.Length > 0).ToList();
            tasks.Add(new TeamTask
            {
                Id = id,
                Title = Store.Clean((string?)node["title"], 90, "Tarefa " + id),
                Description = ((string?)node["description"] ?? "").Trim(),
                DependsOn = deps,
                Status = "pendente",
            });
        }
        var validIds = tasks.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tk in tasks) tk.DependsOn = tk.DependsOn.Where(d => validIds.Contains(d) && !d.Equals(tk.Id, StringComparison.OrdinalIgnoreCase)).ToList();

        var overview = (string?)parsed?["overview"] ?? $"Dividi o trabalho em {tasks.Count} tarefas para a equipe.";
        var planMsg = new ChatMessage
        {
            Id = Store.NewId(), Role = "assistant", Time = DateTime.UtcNow,
            Provider = planRoute.ProviderName + " (Coordenador)", Model = planRoute.Model,
            Content = $"### Plano da Equipe\n{overview}\n\n" + string.Join("\n", tasks.Select(tk =>
                $"- **{tk.Id} · {tk.Title}**" + (tk.DependsOn.Count > 0 ? $" _(após {string.Join(", ", tk.DependsOn)})_" : " _(paralela)_"))),
        };
        store.Mutate(t, th => { th.Tasks = tasks; th.Messages.Add(planMsg); });
        hub.Publish(t.Id, new { type = "message", message = planMsg });
        PublishTasks(t);

        var maxParallel = t.PinnedProviderId != null ? 1 : Math.Clamp(settings.ResolveChain("codigo").Count, 1, 4);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            List<TeamTask> ready;
            lock (t)
            {
                var doneIds = t.Tasks.Where(x => x.Status == "concluida").Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var failedIds = t.Tasks.Where(x => x.Status == "falhou").Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var tk in t.Tasks.Where(x => x.Status == "pendente"))
                {
                    if (tk.DependsOn.Any(d => failedIds.Contains(d)))
                    {
                        tk.Status = "falhou";
                        tk.Error = "Dependência falhou.";
                        tk.Finished = DateTime.UtcNow;
                    }
                }
                ready = t.Tasks.Where(x => x.Status == "pendente" && x.DependsOn.All(d => doneIds.Contains(d))).Take(maxParallel).ToList();
            }
            if (ready.Count == 0)
            {
                // Se restar ciclo/bloqueio, libera uma pendente para não travar
                TeamTask? stuck;
                lock (t) stuck = t.Tasks.FirstOrDefault(x => x.Status == "pendente");
                if (stuck == null) break;
                ready = [stuck];
            }

            state.Status = ready.Count == 1
                ? $"Equipe rodando {ready[0].Id}: {ready[0].Title}"
                : $"Equipe rodando {ready.Count} tarefas em paralelo ({string.Join(", ", ready.Select(x => x.Id))})";
            hub.Publish(t.Id, new { type = "status", text = state.Status });

            await Task.WhenAll(ready.Select(tk => RunWorker(t, state, tk, userPrompt, ct)));
            PublishTasks(t);
        }

        // Resumo final consolidado
        List<TeamTask> finalTasks;
        lock (t) finalTasks = t.Tasks.ToList();
        var sb = new StringBuilder();
        sb.AppendLine("### Entrega da Equipe");
        foreach (var tk in finalTasks)
        {
            var icon = tk.Status == "concluida" ? "✅" : "❌";
            var who = tk.Provider != null ? $" _({tk.Provider} · {tk.Model})_" : "";
            sb.AppendLine($"- {icon} **{tk.Id} · {tk.Title}**{who}: {(tk.Summary ?? tk.Error ?? tk.Status)}");
        }
        var summaryMsg = new ChatMessage
        {
            Id = Store.NewId(), Role = "assistant", Time = DateTime.UtcNow,
            Provider = "Coordenador da Equipe", Model = $"{finalTasks.Count(x => x.Status == "concluida")}/{finalTasks.Count} concluídas",
            Content = sb.ToString().Trim(),
        };
        store.Mutate(t, th => th.Messages.Add(summaryMsg));
        hub.Publish(t.Id, new { type = "message", message = summaryMsg });
    }

    private async Task RunWorker(ChatThread t, RunState state, TeamTask task, string overallPrompt, CancellationToken ct)
    {
        store.Mutate(t, _ => { task.Status = "rodando"; task.Started = DateTime.UtcNow; task.Error = null; });
        PublishTasks(t);
        var ws = store.Workspace(t.ProjectId);
        var doneNotes = new StringBuilder();
        lock (t)
        {
            foreach (var d in t.Tasks.Where(x => x.Status == "concluida"))
                doneNotes.AppendLine($"- {d.Id} ({d.Title}): {d.Summary} [arquivos: {string.Join(", ", d.Files)}]");
        }
        var files = Store.ListFiles(ws, 120);
        var sys = new StringBuilder();
        sys.AppendLine($"Você é um engenheiro especialista da equipe executando EXCLUSIVAMENTE a tarefa {task.Id}: {task.Title}.");
        sys.AppendLine("Use as ferramentas para ler/criar/editar arquivos completos e funcionais (sem TODO). Responda em pt-BR ao terminar com um resumo de 1 a 2 frases.");
        sys.AppendLine($"Objetivo geral do projeto: {overallPrompt}");
        sys.AppendLine($"Sua tarefa ({task.Id}): {task.Description}");
        if (doneNotes.Length > 0) sys.AppendLine("\nJá entregue por outros membros da equipe:\n" + doneNotes);
        sys.AppendLine(files.Count == 0 ? "\nWorkspace atual: vazio." : "\nArquivos atuais:\n" + string.Join("\n", files.Select(f => "- " + f.Path)));

        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = sys.ToString() },
            new JsonObject { ["role"] = "user", ["content"] = $"Execute a tarefa {task.Id}: {task.Title}. Crie ou edite os arquivos necessários agora." },
        };

        try
        {
            string? lastReply = null;
            for (var step = 0; step < MaxWorkerSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var hasCli = settings.Current.Providers.Any(p => p.Enabled && p.Kind == "cli");
                var beforeSnap = hasCli ? SnapshotWorkspace(ws) : null;
                var (res, route) = await router.ChatAsync("codigo", msgs, AgentTools.Schema,
                    r =>
                    {
                        store.Mutate(t, _ => { task.Provider = r.ProviderName; task.Model = r.Model; });
                        PublishTasks(t);
                        return Task.CompletedTask;
                    },
                    null,
                    n => { Notice(t, $"[{task.Id}] {n}", "fallback"); return Task.CompletedTask; },
                    ct, t.PinnedProviderId, t.PinnedModel, ws);

                if (beforeSnap != null && settings.Current.Providers.FirstOrDefault(p => p.Id == route.ProviderId)?.Kind == "cli")
                    DiffWorkspace(t, ws, beforeSnap, "team-" + task.Id, task);

                var ao = new JsonObject { ["role"] = "assistant", ["content"] = res.Content ?? "" };
                if (res.ToolCalls.Count > 0)
                {
                    var calls = new JsonArray();
                    foreach (var c in res.ToolCalls)
                    {
                        var co = new JsonObject
                        {
                            ["id"] = c.Id, ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                        };
                        if (c.Extra != null) co["extra_content"] = c.Extra.DeepClone();
                        calls.Add(co);
                    }
                    ao["tool_calls"] = calls;
                }
                msgs.Add(ao);
                if (!string.IsNullOrWhiteSpace(res.Content)) lastReply = res.Content.Trim();
                if (res.ToolCalls.Count == 0) break;

                foreach (var call in res.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var (ok, output) = await Execute(t, state, call, "team-" + task.Id, task, ct);
                    msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = output });
                }
            }
            store.Mutate(t, _ =>
            {
                task.Status = "concluida";
                task.Finished = DateTime.UtcNow;
                task.Summary = Titles.Trunc(lastReply ?? (task.Files.Count > 0 ? "Arquivos atualizados: " + string.Join(", ", task.Files) : "Concluída."), 240);
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            store.Mutate(t, _ => { task.Status = "falhou"; task.Finished = DateTime.UtcNow; task.Error = e.Message; });
        }
        PublishTasks(t);
    }

    private void PublishTasks(ChatThread t)
    {
        JsonNode? list;
        lock (t) list = JsonSerializer.SerializeToNode(t.Tasks, Store.Json);
        hub.Publish(t.Id, new { type = "tasks", tasks = list });
    }

    private static JsonObject? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(text[start..(end + 1)]) as JsonObject; } catch { return null; }
    }

    private void Notice(ChatThread t, string text, string kind)
    {
        var entry = new ActivityEntry { Time = DateTime.UtcNow, Kind = kind, Text = text };
        var msg = new ChatMessage { Id = Store.NewId(), Role = "notice", Content = text, ToolName = kind, Time = DateTime.UtcNow };
        store.Mutate(t, th => { th.Activity.Add(entry); th.Messages.Add(msg); });
        hub.Publish(t.Id, new { type = "message", message = msg });
    }

    private JsonArray BuildMessages(ChatThread t, bool agent)
    {
        var ws = store.Workspace(t.ProjectId);
        var project = store.GetProject(t.ProjectId);
        var files = Store.ListFiles(ws, 150);
        var sys = new StringBuilder();
        if (agent)
        {
            sys.AppendLine("Você é o agente de desenvolvimento da Central IA, um engenheiro de software sênior e autônomo.");
            sys.AppendLine("Trabalhe sempre em português do Brasil com o usuário. Você tem ferramentas para listar, ler, criar e editar arquivos e executar comandos no PowerShell dentro da pasta do projeto.");
            sys.AppendLine();
            sys.AppendLine("Como trabalhar:");
            sys.AppendLine("1. Para pedidos grandes, comece com um plano curto em lista (máx. 6 itens) e já comece a executar — não espere aprovação, a menos que o pedido seja ambíguo de verdade.");
            sys.AppendLine("2. Crie arquivos completos e funcionais. Nunca deixe “TODO” ou trechos a completar. Prefira edit_file para mudanças pequenas e write_file para arquivos novos.");
            sys.AppendLine("3. Para sites e apps web, a menos que o usuário peça outra tecnologia, crie HTML/CSS/JS estático com index.html na raiz: a Central mostra a prévia ao vivo automaticamente. Capriche no visual (moderno, responsivo, bonito).");
            sys.AppendLine("4. Verifique o que fez: rode testes, compile ou execute scripts quando fizer sentido. Não inicie servidores que ficam rodando para sempre.");
            sys.AppendLine("5. Ao terminar, responda com um resumo curto: o que foi feito, quais arquivos e como usar/testar. Use Markdown.");
            sys.AppendLine();
            sys.AppendLine($"Sistema: Windows, PowerShell. Pasta do projeto: {ws}");
            sys.AppendLine($"Projeto: {project?.Name}. Data de hoje: {DateTime.Now:dd/MM/yyyy}.");
            sys.AppendLine(files.Count == 0 ? "O workspace está vazio." : "Arquivos atuais:\n" + string.Join("\n", files.Select(f => "- " + f.Path)));
        }
        else
        {
            sys.AppendLine("Você é a assistente da Central IA. Responda em português do Brasil, de forma direta, útil e bem formatada em Markdown.");
            sys.AppendLine($"Projeto atual: {project?.Name}. Data de hoje: {DateTime.Now:dd/MM/yyyy}.");
        }

        var arr = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = sys.ToString() } };
        List<ChatMessage> history;
        lock (t) history = t.Messages.ToList();
        var openCalls = new HashSet<string>();
        foreach (var m in history)
        {
            switch (m.Role)
            {
                case "user":
                    arr.Add(new JsonObject { ["role"] = "user", ["content"] = m.Content ?? "" });
                    break;
                case "assistant":
                    var o = new JsonObject { ["role"] = "assistant", ["content"] = m.Content ?? "" };
                    if (agent && m.ToolCalls is { Count: > 0 })
                    {
                        var calls = new JsonArray();
                        foreach (var c in m.ToolCalls)
                        {
                            var co = new JsonObject
                            {
                                ["id"] = c.Id, ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                            };
                            if (c.Extra != null) co["extra_content"] = c.Extra.DeepClone();
                            calls.Add(co);
                            openCalls.Add(c.Id);
                        }
                        o["tool_calls"] = calls;
                    }
                    else if (!agent && m.ToolCalls is { Count: > 0 } && string.IsNullOrWhiteSpace(m.Content)) continue;
                    arr.Add(o);
                    break;
                case "tool":
                    if (!agent || m.ToolCallId == null || !openCalls.Remove(m.ToolCallId)) continue;
                    arr.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content ?? "" });
                    break;
            }
        }
        if (openCalls.Count > 0)
        {
            var fixedArr = new JsonArray();
            foreach (var node in arr)
            {
                fixedArr.Add(node!.DeepClone());
                if (node!["tool_calls"] is JsonArray calls)
                    foreach (var c in calls)
                    {
                        var id = (string?)c!["id"];
                        if (id != null && openCalls.Contains(id))
                            fixedArr.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "Não executado (interrompido)." });
                    }
            }
            arr = fixedArr;
        }
        return arr;
    }

    private async Task<(bool, string)> Execute(ChatThread t, RunState state, ToolCall call, string messageId, TeamTask? task, CancellationToken ct)
    {
        JsonNode? args;
        try { args = JsonNode.Parse(call.Arguments); }
        catch { return (false, "Argumentos inválidos (JSON malformado). Tente de novo com JSON válido."); }
        var ws = store.Workspace(t.ProjectId);
        string Arg(string name) { try { return (string?)args?[name] ?? ""; } catch { return args?[name]?.ToJsonString() ?? ""; } }
        int? IntArg(string name) { try { return args?[name]?.GetValue<int>(); } catch { return int.TryParse(Arg(name), out var v) ? v : null; } }

        try
        {
            switch (call.Name)
            {
                case "list_files":
                {
                    var dir = Store.Inside(ws, Arg("path"));
                    var list = Store.ListFiles(dir, 400);
                    return (true, list.Count == 0 ? "(vazio)" : string.Join("\n", list.Select(f => $"{f.Path}  ({f.Size} bytes)")));
                }
                case "read_file":
                {
                    var path = Store.Inside(ws, Arg("path"));
                    if (!File.Exists(path)) return (false, "Arquivo não existe: " + Arg("path"));
                    var lines = await File.ReadAllLinesAsync(path, ct);
                    var start = Math.Max(1, IntArg("start_line") ?? 1);
                    var end = Math.Min(lines.Length, IntArg("end_line") ?? lines.Length);
                    var sb = new StringBuilder();
                    for (var i = start; i <= end; i++) sb.Append(i).Append(": ").AppendLine(lines[i - 1]);
                    return (true, Clip(sb.ToString(), 30000) + (end < lines.Length ? $"\n… (arquivo tem {lines.Length} linhas)" : ""));
                }
                case "write_file":
                {
                    var rel = Arg("path");
                    var path = Store.Inside(ws, rel);
                    var content = Arg("content");
                    var sem = fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
                    await sem.WaitAsync(ct);
                    try
                    {
                        var before = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
                        Track(t, rel, before, content, messageId, task);
                        return (true, (before == null ? "Criado " : "Atualizado ") + rel + $" ({content.Split('\n').Length} linhas).");
                    }
                    finally { sem.Release(); }
                }
                case "edit_file":
                {
                    var rel = Arg("path");
                    var path = Store.Inside(ws, rel);
                    if (!File.Exists(path)) return (false, "Arquivo não existe: " + rel + ". Use write_file para criar.");
                    var sem = fileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
                    await sem.WaitAsync(ct);
                    try
                    {
                        var before = await File.ReadAllTextAsync(path, ct);
                        var oldText = Arg("old_text");
                        var newText = Arg("new_text");
                        if (oldText.Length == 0) return (false, "old_text vazio.");
                        var src = before;
                        if (!src.Contains(oldText) && src.Contains("\r\n") && !oldText.Contains("\r\n")) src = src.Replace("\r\n", "\n");
                        var count = CountOf(src, oldText);
                        if (count == 0) return (false, "Trecho old_text não encontrado. Leia o arquivo de novo e copie o trecho exato.");
                        if (count > 1) return (false, $"Trecho old_text aparece {count} vezes. Inclua mais contexto para ficar único.");
                        var after = src.Replace(oldText, newText);
                        await File.WriteAllTextAsync(path, after, new UTF8Encoding(false), ct);
                        Track(t, rel, before, after, messageId, task);
                        return (true, "Editado " + rel + ".");
                    }
                    finally { sem.Release(); }
                }
                case "search_files":
                {
                    var pattern = Arg("text");
                    if (pattern.Length == 0) return (false, "Informe o texto.");
                    var hits = new List<string>();
                    foreach (var f in Store.ListFiles(ws, 2000).Where(f => f.Size < 2_000_000))
                    {
                        string[] lines;
                        try { lines = await File.ReadAllLinesAsync(Path.Combine(ws, f.Path), ct); } catch { continue; }
                        for (var i = 0; i < lines.Length && hits.Count < 200; i++)
                            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase)) hits.Add($"{f.Path}:{i + 1}: {Titles.Trunc(lines[i].Trim(), 200)}");
                    }
                    return (true, hits.Count == 0 ? "Nada encontrado." : string.Join("\n", hits));
                }
                case "run_command":
                {
                    var command = Arg("command");
                    if (command.Trim().Length == 0) return (false, "Comando vazio.");
                    if (settings.Current.Approval != "automatico")
                    {
                        var pending = new PendingApproval { Command = command };
                        state.Pending = pending;
                        state.Status = "aguardando sua aprovação";
                        hub.Publish(t.Id, new { type = "approval", id = pending.Id, command });
                        bool ok;
                        using (ct.Register(() => pending.Decision.TrySetCanceled()))
                            ok = await pending.Decision.Task;
                        state.Pending = null;
                        hub.Publish(t.Id, new { type = "approval-done", id = pending.Id, approved = ok });
                        if (!ok) return (false, "O usuário recusou executar este comando. Pergunte ou siga outro caminho.");
                    }
                    var timeout = Math.Clamp(IntArg("timeout_seconds") ?? 120, 5, 900);
                    state.Status = "executando: " + Titles.Trunc(command, 60);
                    hub.Publish(t.Id, new { type = "status", text = state.Status });
                    return await Shell(command, ws, timeout, ct);
                }
                default:
                    return (false, "Ferramenta desconhecida: " + call.Name);
            }
        }
        catch (UserError e) { return (false, e.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return (false, "Falhou: " + e.Message); }
    }

    private void Track(ChatThread t, string rel, string? before, string after, string messageId, TeamTask? task)
    {
        rel = rel.Replace('\\', '/').TrimStart('/');
        store.Mutate(t, th =>
        {
            th.Changes.Add(new FileChange { Path = rel, Before = before, After = after, Time = DateTime.UtcNow, MessageId = messageId });
            if (task != null && !task.Files.Contains(rel)) task.Files.Add(rel);
        });
        hub.Publish(t.Id, new { type = "files", path = rel });
    }

    private static Dictionary<string, string> SnapshotWorkspace(string ws)
    {
        var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Store.ListFiles(ws, 400).Where(x => x.Size < 500_000))
        {
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(ws, f.Path));
                if (!bytes.Take(4000).Any(b => b == 0)) snap[f.Path] = Encoding.UTF8.GetString(bytes);
            }
            catch { }
        }
        return snap;
    }

    private void DiffWorkspace(ChatThread t, string ws, Dictionary<string, string> before, string messageId, TeamTask? task)
    {
        var after = SnapshotWorkspace(ws);
        foreach (var (rel, content) in after)
        {
            before.TryGetValue(rel, out var prev);
            if (prev != content) Track(t, rel, prev, content, messageId, task);
        }
    }

    private static int CountOf(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    public static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..(max / 2)] + $"\n\n… ({s.Length - max} caracteres omitidos) …\n\n" + s[^(max / 2)..];

    public static async Task<(bool, string)> Shell(string command, string cwd, int timeoutSeconds, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command);
        using var p = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) if (output.Length < 400_000) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) if (output.Length < 400_000) output.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch { }
            if (!timedOut) throw;
        }
        try { p.WaitForExit(2000); } catch { }
        string text;
        lock (gate) text = output.ToString().TrimEnd();
        if (timedOut) return (false, Clip(text, 16000) + $"\n\n[encerrado após {timeoutSeconds} s sem terminar]");
        var code = p.ExitCode;
        return (code == 0, (text.Length == 0 ? "(sem saída)" : Clip(text, 16000)) + $"\n[código de saída {code}]");
    }

    public static async Task<(int Code, string Out, string Err)> Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await o, await e);
        }
        catch { return (-1, "", "Git não encontrado no PATH."); }
    }
}

public static class AgentTools
{
    public static readonly JsonArray Schema = Build();

    private static JsonObject Fn(string name, string description, JsonObject props, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            },
        },
    };

    private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static JsonArray Build() => new()
    {
        Fn("list_files", "Lista os arquivos do projeto (ou de uma subpasta).", new JsonObject { ["path"] = P("string", "Subpasta relativa. Vazio = raiz.") }),
        Fn("read_file", "Lê um arquivo de texto do projeto, com números de linha.", new JsonObject
        {
            ["path"] = P("string", "Caminho relativo à raiz do projeto."),
            ["start_line"] = P("integer", "Primeira linha (opcional)."),
            ["end_line"] = P("integer", "Última linha (opcional)."),
        }, "path"),
        Fn("write_file", "Cria ou substitui um arquivo inteiro com o conteúdo completo.", new JsonObject
        {
            ["path"] = P("string", "Caminho relativo à raiz do projeto."),
            ["content"] = P("string", "Conteúdo completo do arquivo."),
        }, "path", "content"),
        Fn("edit_file", "Substitui um trecho exato e único de um arquivo existente.", new JsonObject
        {
            ["path"] = P("string", "Caminho relativo."),
            ["old_text"] = P("string", "Trecho exato atual (deve aparecer uma única vez)."),
            ["new_text"] = P("string", "Novo trecho."),
        }, "path", "old_text", "new_text"),
        Fn("search_files", "Procura um texto em todos os arquivos do projeto.", new JsonObject { ["text"] = P("string", "Texto a procurar.") }, "text"),
        Fn("run_command", "Executa um comando PowerShell na raiz do projeto e devolve a saída. Use para instalar dependências, compilar, testar e rodar scripts que terminam.", new JsonObject
        {
            ["command"] = P("string", "Comando PowerShell."),
            ["timeout_seconds"] = P("integer", "Tempo máximo (padrão 120)."),
        }, "command"),
    };

    public static string Describe(ToolCall c)
    {
        string a(string k) { try { return (string?)JsonNode.Parse(c.Arguments)?[k] ?? ""; } catch { return ""; } }
        return c.Name switch
        {
            "list_files" => "olhando os arquivos",
            "read_file" => "lendo " + a("path"),
            "write_file" => "escrevendo " + a("path"),
            "edit_file" => "editando " + a("path"),
            "search_files" => "procurando “" + a("text") + "”",
            "run_command" => "executando: " + Titles.Trunc(a("command"), 60),
            _ => c.Name,
        };
    }
}
