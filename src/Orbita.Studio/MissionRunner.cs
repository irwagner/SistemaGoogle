using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Orbita.Core;
using Orbita.Pool;

namespace Orbita.Studio;

public sealed record TurnInput(string? Text, bool Execute = false, string? Model = null, string[]? Accounts = null,
    bool Skill = false, bool? FullAccess = null, string? Effort = null, string? Source = null, string? RequestId = null);

public sealed class MissionRunner(StudioFiles files, ICheckpointStore checkpoints, IGitSnapshotService git,
    IAgentEngine engine, AccountPool pool, WindowsDesktop desktop, MissionAttachments attachmentStore,
    IHostApplicationLifetime app, ILogger<MissionRunner> logger)
{
    private readonly SemaphoreSlim work = new(1);
    private readonly object gate = new();
    private (string Id, CancellationTokenSource Stop)? active;

    public async Task DrainAsync()
    {
        await work.WaitAsync();
        work.Release();
    }

    public void Recover()
    {
        foreach (var mission in checkpoints.List().Where(m => m.State is "executando" or "conversando"))
        {
            Save(mission, "interrompida");
            EndTurn(mission.Id, "interrompida", "O Orbita foi encerrado. O worktree e o pedido ficaram salvos.");
        }
    }

    public async Task<Mission> CreateAsync(string projectId, string name, CancellationToken ct)
    {
        name = StudioFiles.Required(name, 100, "Informe o nome da missão.");
        var project = files.Project(projectId);
        if (!work.Wait(0)) throw new StudioException("Aguarde a operação atual do Orbita.");
        try
        {
            files.EnsureContext(projectId);
            var id = StudioFiles.NewId();
            var mission = await git.CreateWorktreeAsync(project, id, Path.Combine(files.MissionPath(id), "workspace"), name, ct);
            checkpoints.Save(mission, checkpoint: true);
            checkpoints.Append(id, "git", "Worktree criado. O projeto original ficou disponível na sua pasta.");
            return mission;
        }
        finally { work.Release(); }
    }

    public Mission Start(string id, TurnInput input, bool resume = false, MissionAttachment[]? uploaded = null)
    {
        if (!engine.Available) throw new StudioException("Instale o Antigravity CLI oficial para iniciar a missão.");
        var mission = checkpoints.Get(id);
        var attachments = uploaded ?? (resume || mission.State == "precisa-de-voce" ? mission.PendingAttachments : null) ?? [];
        var typedText = !resume && string.IsNullOrWhiteSpace(input.Text) && attachments.Length > 0
            ? "Analise os anexos e resuma o que encontrou." : input.Text;
        var text = (resume ? mission.Pending : typedText)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new StudioException(resume ? "Esta missão não tem pedido pendente." : "Escreva um pedido para o Antigravity.");
        var execute = resume || input.Execute || mission.State == "precisa-de-voce" ||
            text.TrimEnd('.', '!').Equals("pode fazer", StringComparison.OrdinalIgnoreCase);
        var accounts = (input.Accounts ?? (resume ? mission.AccountIds : null) ?? []).Distinct().ToArray();
        if (accounts.Length > 10 || accounts.Any(string.IsNullOrWhiteSpace)) throw new StudioException("Selecione até dez contas do pool.");
        var model = string.IsNullOrWhiteSpace(input.Model) ? (resume ? mission.RequestedModel : null)
            : StudioFiles.Required(input.Model, 120, "Modelo inválido.");
        if (execute && !IsDevelopmentModel(model)) model = "neon";
        var effort = resume ? mission.RequestedEffort : input.Effort;
        if (effort is not (null or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new StudioException("Nível de esforço inválido.");
        var skill = resume ? mission.UseSkill : input.Skill;
        var fullAccess = input.FullAccess ?? mission.FullAccess;
        if (!work.Wait(0)) throw new StudioException("O Antigravity já está trabalhando em outra missão.");

        var stop = CancellationTokenSource.CreateLinkedTokenSource(app.ApplicationStopping);
        try
        {
            lock (gate) active = (id, stop);
            mission = Save(mission with { Pending = execute ? text : mission.Pending, RequestedModel = model, RequestedEffort = effort, AccountIds = accounts,
                UseSkill = skill, FullAccess = fullAccess, PendingAttachments = execute ? attachments : mission.PendingAttachments },
                execute ? "executando" : "conversando");
            var turn = new JsonObject();
            if (attachments.Length > 0) turn["attachments"] = JsonSerializer.SerializeToNode(attachments, StudioFiles.Json);
            var remote = input.Source is "signal" or "discord";
            if (remote) turn["source"] = input.Source;
            var receipt = remote ? input.RequestId : null;
            if (resume && receipt == null)
                receipt = (string?)checkpoints.EventsAfter(id, 0).LastOrDefault(entry => entry.Kind == "usuario")?.Data?["requestId"];
            if (Guid.TryParseExact(receipt, "N", out var requestId)) turn["requestId"] = requestId.ToString("N");
            checkpoints.Append(id, "usuario", resume ? "Retomar pedido pendente." : text, turn.Count == 0 ? null : turn);
            _ = Task.Run(() => RunAsync(mission, text, execute, model, skill, accounts, attachments, stop));
            return mission;
        }
        catch
        {
            Release(stop);
            throw;
        }
    }

    public void Cancel(string id)
    {
        lock (gate)
        {
            if (active is not { } current || current.Id != id) throw new StudioException("Esta missão não tem uma execução ativa.");
            current.Stop.Cancel();
        }
    }

    public async Task<Mission> ReviewAsync(string id, bool revert, string? expectedRevision, CancellationToken ct)
    {
        if (!work.Wait(0)) throw new StudioException("Aguarde o Antigravity terminar antes de alterar o diff.");
        try
        {
            var mission = checkpoints.Get(id);
            var current = await git.DiffAsync(mission, ct);
            if (current.Revision != expectedRevision) throw new StudioException("O worktree mudou. Atualize o diff e tente novamente.");
            if (current.Diff.Length == 0 && current.Status.Length == 0) throw new StudioException("O worktree não tem alterações para revisar.");
            var revision = revert ? await git.RestoreAsync(mission, ct) : await git.CaptureAsync(mission, "Aceitar alterações da missão", ct);
            var message = revert ? "Missão revertida. O estado anterior continua no histórico Git." : "Diff aceito e registrado na branch da missão.";
            mission = Save(mission with { Revision = revision, Accepted = revert ? null : revision, Pending = revert ? null : mission.Pending,
                PendingAttachments = revert ? null : mission.PendingAttachments },
                revert ? "revertida" : "aceita");
            checkpoints.Append(id, "git", message);
            return mission;
        }
        finally { work.Release(); }
    }

    private async Task RunAsync(Mission mission, string request, bool execute, string? model, bool skill, string[] accounts,
        MissionAttachment[] attachments, CancellationTokenSource stop)
    {
        var ct = stop.Token;
        var id = mission.Id;
        var attempted = false;
        try
        {
            var mode = mission.FullAccess ? "Permissão total" : "Autônomo";
            checkpoints.Append(id, "estado", execute ? $"Preparando execução · {mode}." : "Abrindo conversa com o Antigravity CLI.");
            var context = BuildPrompt(mission, request, execute, skill);
            var inputs = await attachmentStore.ReadAsync(id, attachments, ct);
            if (execute)
            {
                var before = await git.CaptureAsync(mission, "Checkpoint antes da execução", ct);
                mission = Save(mission with { Revision = before }, "executando");
                checkpoints.Append(id, "git", "Snapshot anterior à execução guardado.");
            }
            await using var lease = accounts.Length == 0 ? null : await pool.ReserveAsync(mission, accounts, ct);
            using var desktopTurn = mission.FullAccess && WindowsDesktop.Available ? desktop.Begin(mission, ct) : null;
            attempted = true;
            var result = await engine.RunAsync(new AgentRequest(mission.Workspace, context, execute, mission.SessionId, model,
                lease?.Address, mission.FullAccess, desktopTurn?.Server, inputs, mission.RequestedEffort), entry =>
            {
                if (entry.Kind == "sessao")
                {
                    mission = mission with { SessionId = (string?)entry.Data?["sessionId"], Model = (string?)entry.Data?["model"],
                        Effort = (string?)entry.Data?["effort"] };
                    checkpoints.Save(mission);
                }
                checkpoints.Append(id, entry.Kind, entry.Text, entry.Data);
                if (entry.Kind == "plano") mission = Save(mission, mission.State);
                return ValueTask.CompletedTask;
            }, ct);
            if (result.VerificationRequired)
            {
                var instruction = "O Google pediu verificação da conta usada no pedido. " + (accounts.Length > 0
                    ? "Abra Contas do Antigravity e clique em Verificar no Google na conta indicada."
                    : "Conclua a verificação no login oficial do Antigravity CLI.") + " Depois, reenvie o pedido nesta missão.";
                checkpoints.Append(id, "mensagem", $"\n\n{instruction}");
                await PauseAsync(mission, execute, "aguardando-verificacao", instruction);
                return;
            }
            if (result.NeedsUser != null)
            {
                await PauseAsync(mission, execute, "precisa-de-voce", result.NeedsUser);
                return;
            }
            if (result.StopReason != "end_turn")
                throw new StudioException($"O Antigravity encerrou a etapa com o estado {result.StopReason}.");
            var revision = execute ? await git.CaptureAsync(mission, "Checkpoint do resultado da missão", ct) : mission.Revision;
            var accepted = mission.Accepted;
            if (execute && mission.FullAccess && revision != accepted)
            {
                var diff = await git.DiffAsync(mission, ct);
                if (diff.Revision == revision && diff.Status.Length == 0 && diff.Diff.Length > 0) accepted = revision;
            }
            var automatic = accepted != mission.Accepted;
            mission = Save(mission with { Revision = revision, Accepted = accepted, Pending = execute ? null : mission.Pending,
                PendingAttachments = execute ? null : mission.PendingAttachments },
                execute ? "concluida" : "pronta");
            if (automatic) checkpoints.Append(id, "git", "Diff aceito automaticamente · Permissão total.");
            EndTurn(id, mission.State, execute ? "Execução concluída. Confira o diff e os eventos de teste." : "Conversa atualizada.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await PauseAsync(mission, execute && attempted, "interrompida", "Execução interrompida. O trabalho ficou salvo.");
        }
        catch (Exception error)
        {
            var reason = error is StudioException ? error.Message : "Falha ao comunicar com o Antigravity ou salvar o trabalho.";
            logger.LogWarning("Missão {Mission} interrompida ({ErrorType}).", id, error.GetType().Name);
            var quota = reason.Contains("quota", StringComparison.OrdinalIgnoreCase) || reason.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
            var needsLogin = reason.StartsWith("Entre com sua conta Google", StringComparison.OrdinalIgnoreCase);
            await PauseAsync(mission, execute && attempted, quota ? "aguardando-cota" : needsLogin ? "precisa-de-voce" : "pendente", reason);
        }
        finally { Release(stop); }
    }

    private string BuildPrompt(Mission mission, string request, bool execute, bool skill)
    {
        var builder = new StringBuilder(execute
            ? "Trabalhe a partir desta pasta. Planeje e siga imediatamente para a implementação, testes e correções até concluir o pedido. A execução e o plano já estão autorizados. Registre o plano como parte do trabalho e prossiga sem interromper para revisão. Se uma ferramenta for recusada, tente um caminho disponível e avance nas etapas independentes. Confira o trabalho existente no Git antes de repetir ações. Ao concluir, informe os arquivos e os resultados verificáveis. Se depois de tentar as alternativas faltar algo que só o usuário pode resolver, encerre com uma linha começando exatamente com 'Preciso de você:' e diga o que falta."
            : "Estamos conversando sobre o projeto. Responda sem alterar arquivos, executar comandos com efeitos ou iniciar implementação.");
        builder.Append("\nConverse em português e comece pela resposta ao pedido. Use parágrafos curtos. Quando houver vários pontos, use listas. Use blocos de código para trechos de código. Na conclusão de uma execução, diga o que foi feito, como foi verificado e o que ficou pendente, quando houver. Mostre logs ou saídas completas de comandos apenas quando o usuário pedir.");
        if (skill) builder.Append("\nSiga o procedimento do SKILL.md para este pedido.");
        foreach (var file in StudioFiles.ProjectFiles)
        {
            var text = files.ReadProjectFile(mission.ProjectId, file);
            builder.Append($"\n\nContexto de {file}:\n").Append(text.AsSpan(0, Math.Min(text.Length, 12_000)));
        }
        var timeline = checkpoints.EventsAfter(mission.Id, 0);
        var plan = timeline.LastOrDefault(entry => entry.Kind == "plano");
        if (plan?.Data is JsonArray rows)
        {
            builder.Append("\n\nÚltimo plano registrado:\n");
            foreach (var row in rows.OfType<JsonObject>().Take(20)) builder.Append("\n- ").Append((string?)row["status"]).Append(" — ").Append((string?)row["content"]);
        }
        if (mission.SessionId == null && execute)
        {
            foreach (var entry in timeline.Where(item => item.Kind == "usuario" && item.Text != request).TakeLast(4))
                builder.Append("\n\nPedido anterior:\n").Append(entry.Text.AsSpan(0, Math.Min(entry.Text.Length, 3_000)));
        }
        return builder.Append("\n\nPedido do usuário:\n").Append(request).ToString();
    }

    private async Task PauseAsync(Mission mission, bool capture, string state, string reason)
    {
        if (capture)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { mission = mission with { Revision = await git.CaptureAsync(mission, "Checkpoint da execução interrompida", timeout.Token) }; }
            catch (Exception error) { logger.LogWarning("O snapshot da missão {Mission} não concluiu ({ErrorType}).", mission.Id, error.GetType().Name); }
        }
        try { Save(mission, state); EndTurn(mission.Id, state, reason); }
        catch (Exception error) { logger.LogWarning("Não foi possível gravar o checkpoint da missão {Mission} ({ErrorType}).", mission.Id, error.GetType().Name); }
    }

    private void EndTurn(string id, string state, string text)
        => checkpoints.Append(id, "estado", text, new JsonObject { ["terminalState"] = state });

    private Mission Save(Mission mission, string state)
    {
        mission = mission with { State = state, Updated = DateTimeOffset.UtcNow };
        checkpoints.Save(mission, checkpoint: true);
        return mission;
    }

    private static bool IsDevelopmentModel(string? model) => model is "neon" or "opus" or "gemini-4-neon" ||
        model?.StartsWith("gemini-4-neon-", StringComparison.OrdinalIgnoreCase) == true ||
        model?.StartsWith("claude-opus-", StringComparison.OrdinalIgnoreCase) == true;

    private void Release(CancellationTokenSource stop)
    {
        lock (gate) { active = null; stop.Dispose(); }
        work.Release();
    }
}
