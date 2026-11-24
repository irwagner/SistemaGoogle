using System.Text.Json.Nodes;

namespace CentralIA;

public sealed class ReviewedWork
{
    public List<WorkRoute> Routes { get; set; } = [];
    public Dictionary<int, WorkPause> Pauses { get; set; } = [];
    public int Active { get; set; }
    public string State { get; set; } = "novo";
    public string Workspace { get; set; } = "";
    public string? Plan { get; set; }
    public string? PlanId { get; set; }
    public bool Approved { get; set; }
    public string? Issue { get; set; }
    public DateTimeOffset? Wake { get; set; }
    public string? PendingTool { get; set; }
    public string? QuestionReturnState { get; set; }
}

public sealed record WorkRoute(string ProviderId, string Name, string Model, string Endpoint);
public sealed record WorkPause(DateTimeOffset? Reset, DateTimeOffset CheckAfter);

public sealed partial class AgentService
{
    private static bool ReadOnlyTool(string name) => name is "list_files" or "read_file" or "search_files";
    private static JsonArray PlanningTools() => new(AgentTools.Schema.Where(tool =>
        ReadOnlyTool((string?)tool?["function"]?["name"] ?? "")).Select(tool => tool!.DeepClone()).ToArray());

    public bool IsReviewedConnection(ProviderConfig p)
    {
        if (!SettingsService.CanRoute(p) || p.Kind == "cli" || !Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
        if (uri.IsLoopback && uri.Scheme is "http" or "https") return true;
        return uri.Scheme == "https" && settings.HasKey(p.Id);
    }

    public ReviewedWork CreateReviewed(string pid, IEnumerable<RouteEntry> selected)
    {
        var routes = new List<WorkRoute>();
        foreach (var entry in selected)
        {
            var provider = settings.Current.Providers.FirstOrDefault(p => p.Id == entry.ProviderId && IsReviewedConnection(p));
            if (provider == null || string.IsNullOrWhiteSpace(entry.Model) ||
                (entry.Model != provider.DefaultModel && !provider.Models.Contains(entry.Model)))
                throw new UserError("Selecione uma conexão de API habilitada e um modelo cadastrado.");
            routes.Add(new WorkRoute(provider.Id, provider.Name, entry.Model, provider.BaseUrl));
        }
        if (routes.Count is < 1 or > 10 || routes.Select(r => (r.ProviderId, r.Model)).Distinct().Count() != routes.Count)
            throw new UserError("Escolha de um a dez modelos sem repetir a mesma conexão e o mesmo modelo.");
        return new ReviewedWork { Routes = routes, Workspace = store.Workspace(pid) };
    }

    private void StartReviewed(ChatThread t, string text)
    {
        StartReviewedRun(t, w =>
        {
            if (w.State == "conciliar") throw new UserError("Confira o efeito da ação anterior antes de enviar outra instrução.");
            w.Plan = null;
            w.PlanId = null;
            w.Approved = false;
            w.State = "planejando";
            AddReviewedMessage(t, text);
        });
    }

    public void AskReviewed(ChatThread t, string text) => StartReviewedRun(t, w =>
    {
        if (w.State == "conciliar") throw new UserError("Confira o efeito da ação anterior primeiro.");
        w.QuestionReturnState = w.State;
        w.State = "consultando";
        AddReviewedMessage(t, text);
    });

    private static void AddReviewedMessage(ChatThread t, string text) =>
        t.Messages.Add(new ChatMessage { Id = Store.NewId(), Role = "user", Content = text, Time = DateTime.UtcNow });

    public void ApproveReviewed(ChatThread t, string planId) => StartReviewedRun(t, w =>
    {
        if (w.State != "revisao" || w.PlanId == null || w.PlanId != planId)
            throw new UserError("Atualize a conversa e confira a versão atual do plano.");
        w.Approved = true;
        w.State = "executando";
    });

    public void ResumeReviewed(ChatThread t) => StartReviewedRun(t, w =>
    {
        if (w.State != "pausado" || w.PendingTool != null)
            throw new UserError("A retomada está disponível depois de pausar ou conferir os efeitos pendentes.");
        w.State = w.Approved ? "executando" : "planejando";
    });

    public void ReconcileReviewed(ChatThread t, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) throw new UserError("Registre o que você conferiu nos arquivos ou terminal.");
        if (!continuity.Reserve(t)) throw new UserError("Aguarde a execução atual terminar.");
        try
        {
            ChangeReviewed(t, w =>
            {
                if (w.State != "conciliar" || w.PendingTool == null) throw new UserError("A conversa não tem uma ação pendente.");
                t.Messages.Add(new ChatMessage { Id = Store.NewId(), Role = "user", Time = DateTime.UtcNow,
                    Content = "Conciliação da ação anterior: " + Store.Clean(note, 4000, "") + "\nConfira o estado atual antes de continuar." });
                w.PendingTool = null;
                w.Issue = null;
                w.State = "pausado";
            });
        }
        finally { continuity.Release(t); }
    }

    public void RecoverReviewed()
    {
        foreach (var p in store.Projects())
            foreach (var t in store.Threads(p.Id))
            {
                if (t.ReviewedWork is { State: "aguardando-cota", PendingTool: null, QuestionReturnState: null })
                {
                    try { StartReviewedRun(t, _ => { }); continue; }
                    catch (UserError) { /* The project can already be reserved by a recovered job. */ }
                }
                if (t.ReviewedWork?.State is "planejando" or "executando" or "aguardando-cota" or "consultando")
                    PauseReviewed(t, "A Central foi reiniciada. O histórico e o plano estão salvos.");
            }
    }

    private void StartReviewedRun(ChatThread t, Action<ReviewedWork> start)
    {
        if (!continuity.Reserve(t)) throw new UserError("Já existe uma etapa em execução neste projeto.");
        var state = new RunState();
        try
        {
            if (!runs.TryAdd(t.Id, state)) throw new UserError("Já existe uma execução nesta conversa.");
            ChangeReviewed(t, w =>
            {
                if (!string.Equals(store.Workspace(t.ProjectId), w.Workspace, StringComparison.OrdinalIgnoreCase))
                    throw new UserError("Restaure a pasta selecionada no início da conversa.");
                start(w);
                w.Issue = null;
                w.Wake = null;
            });
            hub.Publish(t.Id, new { type = "run", running = true });
            _ = Task.Run(() => RunReviewed(t, state));
        }
        catch { runs.TryRemove(t.Id, out _); continuity.Release(t); state.Cts.Dispose(); throw; }
    }

    private async Task RunReviewed(ChatThread t, RunState run)
    {
        var work = t.ReviewedWork!;
        var question = work.QuestionReturnState != null;
        var readOnly = question || !work.Approved;
        var phase = question ? "consultando" : readOnly ? "planejando" : "executando";
        var ct = run.Cts.Token;
        var tools = readOnly ? PlanningTools() : AgentTools.Schema;
        try
        {
            for (var step = 0; step < MaxSteps; step++)
            {
                ct.ThrowIfCancellationRequested();
                var (response, selected) = await CallReviewed(t, run, tools, phase, ct);
                var assistant = new ChatMessage
                {
                    Id = Store.NewId(), Role = "assistant", Content = response.Content, Time = DateTime.UtcNow,
                    ProviderId = selected.ProviderId, Provider = selected.Name, Model = selected.Model,
                    ToolCalls = response.ToolCalls.Count == 0 ? null : response.ToolCalls
                };
                run.Streaming = "";
                ChangeReviewed(t, w =>
                {
                    t.Messages.Add(assistant);
                    if (response.ToolCalls.Count != 0) return;
                    if (w.QuestionReturnState is { } previous)
                    {
                        w.State = previous;
                        w.QuestionReturnState = null;
                    }
                    else if (readOnly)
                    {
                        w.Plan = response.Content;
                        w.PlanId = assistant.Id;
                        w.State = "revisao";
                    }
                    else w.State = "concluido";
                });
                if (response.ToolCalls.Count == 0) return;

                foreach (var call in response.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    var writes = !readOnly && call.Name is ("write_file" or "edit_file" or "run_command");
                    if (writes) ChangeReviewed(t, w => w.PendingTool = AgentTools.Describe(call));
                    var (ok, output) = readOnly && !ReadOnlyTool(call.Name)
                        ? (false, "A revisão do plano permite somente leitura.")
                        : await Execute(t, run, call, assistant.Id, null, ct);
                    ChangeReviewed(t, w =>
                    {
                        t.Messages.Add(new ChatMessage
                        {
                            Id = Store.NewId(), Role = "tool", ToolCallId = call.Id, ToolName = call.Name,
                            Content = output, Ok = ok, Time = DateTime.UtcNow
                        });
                        if (writes && (ok || (!output.StartsWith("Falhou:", StringComparison.Ordinal) &&
                            !output.Contains("[encerrado após", StringComparison.Ordinal)))) w.PendingTool = null;
                    });
                    if (work.PendingTool != null)
                        throw new UserError("Confira o efeito da ação no projeto antes de continuar.");
                }
            }
            throw new UserError("A etapa atingiu o limite de operações. Confira o histórico e retome.");
        }
        catch (OperationCanceledException) { PauseReviewed(t, "Execução interrompida. O progresso confirmado foi salvo."); }
        catch (UserError e) { PauseReviewed(t, e.Message); }
        catch (Exception e)
        {
            log.LogWarning("Falha em programação com revisão ({FailureType}).", e.GetType().Name);
            PauseReviewed(t, "A resposta foi interrompida. Revise o histórico antes de retomar.");
        }
        finally
        {
            runs.TryRemove(t.Id, out _);
            continuity.Release(t);
            store.TouchProject(t.ProjectId);
            hub.Publish(t.Id, new { type = "run", running = false });
        }
    }

    private async Task<(LlmResult, WorkRoute)> CallReviewed(ChatThread t, RunState run, JsonArray tools, string phase, CancellationToken ct)
    {
        var work = t.ReviewedWork!;
        while (true)
        {
            var now = DateTimeOffset.UtcNow;
            var order = Enumerable.Range(0, work.Routes.Count).OrderByDescending(index => index == work.Active);
            var ready = order.Where(index => !work.Pauses.TryGetValue(index, out var p) || p.CheckAfter <= now).ToArray();
            if (ready.Length == 0)
            {
                var earliest = work.Pauses.Values.Min(p => p.CheckAfter);
                ChangeReviewed(t, w => { w.State = "aguardando-cota"; w.Wake = earliest; });
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp((earliest - now).TotalMilliseconds, 100, 900_000)), ct);
                continue;
            }

            var index = ready[0];
            var route = work.Routes[index];
            var provider = settings.Current.Providers.FirstOrDefault(p => p.Id == route.ProviderId && IsReviewedConnection(p));
            if (provider == null || provider.BaseUrl != route.Endpoint)
                throw new UserError($"A conexão {route.Name} mudou ou está indisponível. Restaure a conexão selecionada.");
            ChangeReviewed(t, w => { w.Active = index; w.Wake = null; w.State = phase; });
            run.Route = new RouteInfo(route.ProviderId, route.Name, route.Model);
            run.Streaming = "";
            hub.Publish(t.Id, new { type = "route", route = run.Route });

            var messages = BuildReviewedMessages(t, route, phase);
            try
            {
                var result = await LlmClient.ChatAsync(provider, settings.GetKey(provider.Id), route.Model, messages, tools,
                    delta =>
                    {
                        run.Streaming += delta;
                        hub.Publish(t.Id, new { type = "delta", text = delta });
                        return Task.CompletedTask;
                    }, ct, requireFinal: true);
                if (work.Pauses.ContainsKey(index)) ChangeReviewed(t, w => w.Pauses.Remove(index));
                return (result, route);
            }
            catch (LlmException e) when (e.EarlyHttp && e.Status == 429)
            {
                var point = DateTimeOffset.UtcNow;
                DateTimeOffset? reset = e.RetryAfter is { TotalSeconds: > 0 and <= 2_592_000 } after ? point + after : null;
                ChangeReviewed(t, w => w.Pauses[index] = new WorkPause(reset, reset ?? point.AddMinutes(1)));
                Notice(t, $"{route.Name} · {route.Model} atingiu o limite. A Central vai verificar os próximos modelos selecionados.", "fallback");
            }
            catch (LlmException e)
            {
                if (run.Streaming.Length > 0)
                    Notice(t, "Resposta parcial preservada para revisão\n\n" + Store.Clean(run.Streaming, 6000, ""), "info");
                throw new UserError(e.Kind == LlmFailure.Auth ? $"Confira a autorização de {route.Name}." :
                    $"A resposta de {route.Name} terminou sem confirmação. Retome após revisar.");
            }
        }
    }

    private JsonArray BuildReviewedMessages(ChatThread t, WorkRoute route, string phase)
    {
        var w = t.ReviewedWork!;
        var instructions = phase switch
        {
            "consultando" => "Responda à última pergunta com base no projeto e no histórico. Use somente ferramentas de leitura. Preserve o plano atual:\n" + w.Plan,
            "planejando" => "Você está preparando um plano sujeito a aprovação. Use somente ferramentas de leitura. Proponha etapas, arquivos e testes em Markdown. Qualquer pedido de alteração vira proposta para revisão.",
            _ => "Execute somente o plano confirmado abaixo. Confira os arquivos atuais, use o histórico para evitar repetir ações concluídas e execute testes adequados antes do resumo final.\n\nPlano aprovado:\n" + w.Plan
        };
        var context = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = $"Projeto local na pasta {w.Workspace}. Responda em português do Brasil.\n" + instructions }
        };
        var portable = new HashSet<string>();
        lock (t)
            foreach (var message in t.Messages)
            {
                if (message.Role is "user" or "assistant")
                {
                    var contents = message.Content ?? "";
                    var node = new JsonObject { ["role"] = message.Role, ["content"] = contents };
                    if (message.ToolCalls is { Count: > 0 } calls)
                    {
                        var receipts = calls.Select(call => t.Messages.SkipWhile(m => m != message).Skip(1)
                            .TakeWhile(m => m.Role == "tool").FirstOrDefault(m => m.ToolCallId == call.Id)).ToArray();
                        if (message.ProviderId == route.ProviderId && message.Model == route.Model && receipts.All(m => m != null))
                        {
                            node["tool_calls"] = new JsonArray(calls.Select(call =>
                            {
                                var tool = new JsonObject { ["id"] = call.Id, ["type"] = "function",
                                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments } };
                                if (call.Extra != null) tool["extra_content"] = call.Extra.DeepClone();
                                return (JsonNode)tool;
                            }).ToArray());
                            foreach (var call in calls) portable.Add(call.Id);
                        }
                        else node["content"] = contents + "\nAções propostas anteriormente: " + string.Join(", ", calls.Select(c => c.Name));
                    }
                    context.Add(node);
                }
                else if (message.Role == "tool" && message.ToolCallId is { } id)
                {
                    context.Add(portable.Remove(id)
                        ? new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = message.Content }
                        : new JsonObject { ["role"] = "assistant", ["content"] = $"Resultado registrado de {message.ToolName}: {message.Content}" });
                }
            }
        return context;
    }

    private void PauseReviewed(ChatThread t, string reason) => ChangeReviewed(t, w =>
    {
        w.Wake = null;
        w.State = w.PendingTool == null ? w.QuestionReturnState ?? "pausado" : "conciliar";
        w.QuestionReturnState = null;
        w.Issue = w.PendingTool == null ? reason : "Confira o efeito de " + w.PendingTool + " antes de retomar.";
    });

    private void ChangeReviewed(ChatThread t, Action<ReviewedWork> action)
    {
        store.Mutate(t, th => action(th.ReviewedWork ?? throw new UserError("Abra um trabalho com revisão de plano.")));
        hub.Publish(t.Id, new { type = "reviewed" });
    }
}
