using Central.Domain;
using Microsoft.Data.Sqlite;

namespace Central.Operations;

public sealed record WorkItem(string Id, string Project, string Area, string Mode, string Title, string Request,
    string Plan, string PlanSource, string Executor, string Model, string State, string Phase, string Note,
    int Revision, int Priority, string? Follows, string Updated);
public sealed record WorkMessage(string Author, string Body, string Created);
public sealed record WorkFile(Asset Asset, string Role);
public sealed record WorkRun(string Id, string Phase, string State, string Worker, string Queued, string Started, string? Ended);
public sealed record Job(WorkItem Work, WorkRun Run);
public sealed record WorkMetrics(int Accepted, int Rework, double? AverageWaitSeconds, double ActiveMinutes,
    double? Utilization, double? CostPerAcceptedCents, int CostsKnown);

public sealed class Workbench(Store store)
{
    public static IReadOnlyDictionary<string, string> Areas { get; } = new Dictionary<string, string>
    {
        ["programacao"] = "Programação", ["video"] = "Vídeo e minisséries", ["pesquisa"] = "Pesquisa",
        ["audio"] = "Áudio", ["distribuicao"] = "Distribuição"
    };

    public static IReadOnlyDictionary<string, string> Modes(string area) => area switch
    {
        "programacao" => new Dictionary<string, string>
        {
            ["criar"] = "Criar sistema", ["continuar"] = "Continuar um repositório",
            ["corrigir"] = "Corrigir um problema", ["revisar"] = "Revisar código"
        },
        "video" => new Dictionary<string, string> { ["avulso"] = "Vídeo avulso", ["serie"] = "Série ou minissérie" },
        "pesquisa" => new Dictionary<string, string> { ["investigar"] = "Investigar uma pergunta" },
        "audio" => new Dictionary<string, string> { ["montar"] = "Organizar faixas" },
        "distribuicao" => new Dictionary<string, string> { ["preparar"] = "Preparar entrega" },
        _ => throw new DomainError("Área inexistente.")
    };

    private const string Columns = """
        id,project,area,mode,title,request,plan,plan_source,executor,model,state,phase,note,revision,priority,follows,updated
        """;

    public WorkItem Get(string id) => store.Read(db => Find(db, id));
    private static WorkItem Find(SqliteConnection db, string id) => db.One(
        $"SELECT {Columns} FROM work_items WHERE id=$i", ReadWork, id) ?? throw new DomainError("Trabalho inexistente.");

    public IReadOnlyList<WorkItem> List(string? area = null, string? project = null) => store.Read(db =>
        db.List($"SELECT {Columns} FROM work_items WHERE ($a IS NULL OR area=$a) AND ($p IS NULL OR project=$p) ORDER BY updated DESC",
            ReadWork, area, project));

    public IReadOnlyList<WorkMessage> Messages(string id) => store.Read(db => db.List(
        "SELECT author,body,created FROM work_messages WHERE work=$i ORDER BY sequence",
        r => new WorkMessage(r.GetString(0), r.GetString(1), r.GetString(2)), id));

    public IReadOnlyList<WorkRun> Runs(string id) => store.Read(db => db.List("""
        SELECT id,phase,state,worker,queued_at,started_at,ended_at FROM work_runs WHERE work=$i ORDER BY started_at DESC
        """, ReadRun, id));

    public IReadOnlyList<WorkFile> Files(WorkItem item) => store.Read(db =>
        db.List("SELECT asset,role FROM work_files WHERE work=$i AND project=$p ORDER BY rowid",
            r => (r.GetString(0), r.GetString(1)), item.Id, item.Project)
        .Select(r => new WorkFile(Store.AssetIn(db, item.Project, r.Item1), r.Item2)).ToArray());

    public string? Resource(string work) => store.Read(db => db.Scalar<string>("SELECT connection_id FROM work_items WHERE id=$i", work));

    public void SelectResource(string id, string? connection)
    {
        store.Write(db =>
        {
            var item = Find(db, id);
            if (item.State is "fila" or "rodando" or "aguardando" or "revisao" or "aceito" or "cancelado")
                throw new DomainError("A seleção está congelada nesta etapa.");
            if (!string.IsNullOrWhiteSpace(connection) &&
                !SupportsResource(item.Area, db.Scalar<string>("SELECT product FROM connections WHERE id=$c", connection)))
                throw new DomainError("Selecione uma conexão cadastrada para esta área.");
            db.Exec("UPDATE work_items SET connection_id=$c WHERE id=$i", string.IsNullOrWhiteSpace(connection) ? null : connection, id);
            Touch(db, id);
            Log(db, id, "Você", string.IsNullOrWhiteSpace(connection) ? "Usará o executor local ou assistido selecionado." :
                "Fixou a conexão " + connection + " para este trabalho.");
        });
    }
    public static bool SupportsResource(string area, string? product) => area switch
    {
        "programacao" => product is "antigravity" or "ollama",
        "video" => product == "flow",
        "audio" => product is "ollama" or "flow",
        _ => false
    };

    public string Create(string project, string area, string mode, string title, string request, int priority, string? follows)
    {
        store.GetProject(project);
        if (!Modes(area).ContainsKey(mode) || priority is < 0 or > 9) throw new DomainError("Modo ou prioridade inválida.");
        title = Store.Text(title, 120, "um título");
        request = Store.Text(request, 14000, "o pedido");
        if (area == "pesquisa") Store.Text(request, 2000, "a pergunta");
        return store.Write(db =>
        {
            if (db.Scalar<long>("SELECT COUNT(*) FROM work_scopes WHERE scope=$p", project) != 0)
                throw new DomainError("Adicione trabalhos no projeto do episódio.");
            if (!string.IsNullOrWhiteSpace(follows) && Find(db, follows).Project != project)
                throw new DomainError("A dependência deve estar no mesmo projeto.");
            var id = Store.Id();
            db.Exec("""
                INSERT INTO work_items(id,project,area,mode,title,request,priority,follows,created,updated)
                VALUES($i,$p,$a,$m,$t,$r,$priority,$f,$n,$n)
                """, id, project, area, mode, title, request, priority, string.IsNullOrWhiteSpace(follows) ? null : follows, store.Now);
            Log(db, id, "Você", request);
            if (area == "pesquisa")
            {
                var question = Store.Id();
                db.Exec("INSERT INTO research VALUES($id,$p,$q,$c,$n)", question, project, request,
                    "Identificar fontes e revisar as afirmações.", store.Now);
                db.Exec("INSERT INTO work_questions VALUES($i,$p,$q)", id, project, question);
            }
            store.Event(db, project, "trabalho.criado", id);
            return id;
        });
    }

    public void SaveRequest(string id, int revision, string body)
    {
        body = Store.Text(body, 14000, "o pedido");
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            if (item.State is not ("rascunho" or "plano" or "falhou"))
                throw new DomainError("O pedido fica congelado durante a execução.");
            if (body == item.Request) return;
            if (item.Area == "pesquisa")
                db.Exec("UPDATE research SET question=$q WHERE id=(SELECT question FROM work_questions WHERE work=$w)",
                    Store.Text(body, 2000, "a pergunta"), id);
            db.Exec("UPDATE work_items SET request=$r,plan='',plan_source='',state='rascunho' WHERE id=$i", body, id);
            Touch(db, id);
            Log(db, id, "Você", body);
        });
    }

    public string Question(string work) => store.Read(db => db.Scalar<string>("SELECT question FROM work_questions WHERE work=$w", work))
        ?? throw new DomainError("Abra uma investigação para registrar as fontes.");

    public void Comment(string id, string message)
    {
        message = Store.Text(message, 5000, "a mensagem");
        store.Write(db =>
        {
            Find(db, id);
            Log(db, id, "Você", message);
            db.Exec("UPDATE work_items SET updated=$n WHERE id=$i", store.Now, id);
        });
    }

    public string Draft(WorkItem item, string field) => store.Read(db =>
        db.Scalar<string>("SELECT body FROM work_drafts WHERE work=$i AND field=$f AND revision=$v", item.Id, field, item.Revision))
        ?? (field == "pedido" ? item.Request : item.Plan);

    public void SaveDraft(string id, int revision, string field, string? body)
    {
        body ??= "";
        if (field is not ("pedido" or "plano") || body.Length > (field == "pedido" ? 14000 : 16000))
            throw new DomainError("Rascunho inválido.");
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            if (field == "pedido" && item.State is not ("rascunho" or "plano" or "falhou") ||
                field == "plano" && item.State != "plano") throw new DomainError("O rascunho está congelado nesta etapa.");
            db.Exec("""
                INSERT INTO work_drafts VALUES($i,$f,$r,$b)
                ON CONFLICT(work,field) DO UPDATE SET revision=excluded.revision,body=excluded.body
                """, id, field, revision, body);
        });
    }

    public void Template(string id, int revision)
    {
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            RequireDraft(item);
            var steps = item.Area == "video"
                ? "1. Escrever e aprovar roteiro, personagens e storyboard.\n2. Revisar os planos e escolher tomadas e som.\n3. Montar, conferir a continuidade e exportar."
                : "1. Delimitar os requisitos e critérios de teste.\n2. Implementar numa árvore isolada e registrar o diff.\n3. Conferir os testes e aprovar a entrega.";
            SetPlan(db, item, $"{item.Request}\n\n{steps}", "Modelo de fluxo editável");
        });
    }

    public void PlanWithModel(string id, int revision, string? model)
    {
        var selected = LocalModel.ValidateName(model);
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            RequireDraft(item);
            db.Exec("UPDATE work_items SET model=$m WHERE id=$i", selected, id);
            Enqueue(db, item, "planejar");
        });
    }

    public void SavePlan(string id, int revision, string plan)
    {
        plan = Store.Text(plan, 16000, "o plano");
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            if (item.State != "plano") throw new DomainError("Abra um plano para editar.");
            SetPlan(db, item, plan, plan == item.Plan ? item.PlanSource : item.PlanSource + (item.PlanSource.Contains("editado") ? "" : " · editado por você"));
        });
    }

    public void Approve(string id, int revision, string plan, string executor, string? model)
    {
        plan = Store.Text(plan, 16000, "o plano");
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            if (item.State != "plano") throw new DomainError("O plano precisa estar disponível para aprovação.");
            var source = plan == item.Plan || item.PlanSource.Contains("editado") ? item.PlanSource : item.PlanSource + " · editado por você";
            var selected = executor == "ollama" ? LocalModel.ValidateName(model) : "";
            if (executor is not ("ollama" or "assistido") || executor == "ollama" && (item.Area != "programacao" || item.Mode != "criar"))
                throw new DomainError("Selecione um executor disponível para este tipo de trabalho.");
            var pinned = db.Scalar<string>("SELECT product FROM connections c JOIN work_items w ON w.connection_id=c.id WHERE w.id=$i", id);
            if (item.Area == "programacao" && pinned != null && (pinned == "ollama") != (executor == "ollama"))
                throw new DomainError("O executor também precisa corresponder à conexão fixada.");
            db.Exec("UPDATE work_items SET plan=$p,plan_source=$s,executor=$e,model=$m WHERE id=$i", plan, source, executor, selected, id);
            Log(db, id, "Você", "Plano aprovado.");
            if (item.Area == "programacao") Enqueue(db, item, executor == "ollama" ? "implementar" : "preparar");
            else
            {
                db.Exec("UPDATE work_items SET state='revisao',note='Continue na área deste trabalho.' WHERE id=$i", id);
                Touch(db, id);
            }
        });
    }

    private void Enqueue(SqliteConnection db, WorkItem item, string phase)
    {
        db.Exec("UPDATE work_items SET state='fila',phase=$f,note=$n,queued_at=$q,started_at=NULL,finished_at=NULL WHERE id=$i",
            phase, item.Follows == null ? "Aguardando executor." : "Aguardando executor e aprovação da dependência.", store.Now, item.Id);
        Touch(db, item.Id);
        Log(db, item.Id, "Fila", "Em fila para " + phase + ".");
    }

    public void SetState(string id, int revision, string action)
    {
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            switch (action)
            {
                case "pausar" when item.State is "fila" or "rodando":
                    Mark(db, item, "pausado", "Trabalho pausado. Retome da etapa registrada.");
                    break;
                case "retomar" when item.State is "pausado" or "falhou" && item.Phase.Length > 0:
                    Enqueue(db, item, item.Phase);
                    break;
                case "cancelar" when item.State is "fila" or "rodando" or "pausado" or "aguardando":
                    Mark(db, item, "cancelado", "Trabalho cancelado.");
                    break;
                case "aceitar" when item.State == "revisao":
                    Mark(db, item, "aceito", "Entrega aceita por você.");
                    break;
                default: throw new DomainError("A ação não está disponível nesta etapa.");
            }
        });
    }

    public void Attach(string id, string asset, string role)
    {
        if (role is not ("referencia" or "repositorio" or "patch" or "recibo" or "arquivo"))
            throw new DomainError("Tipo de vínculo inválido.");
        store.Write(db =>
        {
            var item = Find(db, id);
            if (item.State is "fila" or "rodando" or "aceito" or "cancelado")
                throw new DomainError("Pause o trabalho antes de alterar seus vínculos.");
            var file = Store.AssetIn(db, item.Project, asset);
            if (role == "repositorio" && file.Type != ".bundle" || role == "patch" && file.Type != ".patch")
                throw new DomainError("Use um Git bundle para o repositório e um .patch para o diff.");
            db.Exec("INSERT INTO work_files VALUES($project,$work,$asset,$role) ON CONFLICT(work,asset) DO UPDATE SET role=excluded.role",
                item.Project, item.Id, file.Id, role);
            Touch(db, id);
            Log(db, id, "Você", "Vinculou " + file.Name + " como " + role + ".");
        });
    }

    public void ReceiveAssisted(string id, int revision)
    {
        store.Write(db =>
        {
            var item = Changeable(db, id, revision);
            if (item.State != "aguardando" || item.Area != "programacao" ||
                db.Scalar<long>("SELECT COUNT(*) FROM work_files WHERE work=$i AND role='recibo'", id) == 0 ||
                item.Mode != "revisar" && db.Scalar<long>("SELECT COUNT(*) FROM work_files WHERE work=$i AND role='patch'", id) == 0)
                throw new DomainError("Vincule o recibo e, para mudanças de código, o patch devolvido.");
            Enqueue(db, item, "verificar");
        });
    }

    public Job? Claim(string worker) => store.Write(db =>
    {
        var item = db.One($"""
            SELECT {Columns} FROM work_items WHERE state='fila' AND
            (follows IS NULL OR EXISTS (SELECT 1 FROM work_items d WHERE d.id=work_items.follows AND d.state='aceito'))
            ORDER BY priority DESC,queued_at,id LIMIT 1
            """, ReadWork);
        if (item == null) return null;
        var queued = db.Scalar<string>("SELECT queued_at FROM work_items WHERE id=$i", item.Id)!;
        var run = new WorkRun(Store.Id(), item.Phase, "rodando", worker, queued, store.Now, null);
        db.Exec("INSERT INTO work_runs VALUES($r,$i,$p,'rodando',$w,$q,$n,NULL)", run.Id, item.Id, run.Phase, worker, queued, store.Now);
        db.Exec("UPDATE work_items SET state='rodando',started_at=$n,note=$m WHERE id=$i", store.Now,
            "Em execução · " + item.Phase, item.Id);
        Touch(db, item.Id);
        Log(db, item.Id, "Executor", $"Iniciou {item.Phase} em {worker}.");
        return new Job(item, run);
    });

    public bool Active(Job job) => store.Read(db => Active(db, job));
    private static bool Active(SqliteConnection db, Job job) => db.Scalar<long>("""
        SELECT COUNT(*) FROM work_items w JOIN work_runs r ON r.work=w.id
        WHERE w.id=$i AND w.state='rodando' AND r.id=$r AND r.state='rodando'
        """, job.Work.Id, job.Run.Id) == 1;

    public void Finish(Job job, string state, string note, string? plan = null, IReadOnlyList<(Asset Asset, string Role)>? files = null)
    {
        store.Write(db =>
        {
            if (!Active(db, job)) return;
            var item = job.Work;
            if (plan != null) SetPlan(db, item, plan, "Ollama local · " + item.Model);
            if (files != null)
                foreach (var file in files)
                    db.Exec("INSERT OR IGNORE INTO work_files VALUES($p,$i,$a,$r)", item.Project, item.Id, file.Asset.Id, file.Role);
            Mark(db, item, state, note);
        });
    }

    public void Recover()
    {
        store.Write(db =>
        {
            foreach (var item in db.List($"SELECT {Columns} FROM work_items WHERE state='rodando'", ReadWork))
                Mark(db, item, "pausado", "A Central reiniciou durante a etapa. Retome do último ponto registrado.");
        });
    }

    public WorkMetrics Metrics(string? project = null) => store.Read(db =>
    {
        var items = db.List("SELECT id FROM work_items WHERE state='aceito' AND ($p IS NULL OR project=$p)", r => r.GetString(0), project);
        var runs = db.List("""
            SELECT r.id,r.phase,r.state,r.worker,r.queued_at,r.started_at,r.ended_at FROM work_runs r
            JOIN work_items w ON w.id=r.work WHERE ($p IS NULL OR w.project=$p)
            """, ReadRun, project);
        var rework = db.Scalar<long>("""
            SELECT COALESCE(SUM(repetitions-1),0) FROM (SELECT COUNT(*) repetitions FROM work_runs r
            JOIN work_items w ON w.id=r.work WHERE ($p IS NULL OR w.project=$p) GROUP BY r.work,r.phase)
            """, project);
        rework += db.Scalar<long>("""
            SELECT COUNT(*) FROM events e JOIN work_scopes s ON e.project=s.scope
            WHERE e.kind='estudio.take_substituido' AND ($p IS NULL OR s.project=$p)
            """, project);
        var costs = db.List("""
            SELECT c.cents FROM work_costs c JOIN work_items w ON w.id=c.work
            WHERE w.state='aceito' AND ($p IS NULL OR w.project=$p)
            """, r => r.GetInt64(0), project);
        var elapsed = runs.Count == 0 ? 0 : Math.Max(1, Seconds(runs.Min(r => r.Queued)!, store.Now));
        var active = runs.Sum(r => Seconds(r.Started, r.Ended ?? store.Now));
        return new WorkMetrics(items.Count, (int)rework,
            runs.Count == 0 ? null : runs.Average(r => Seconds(r.Queued, r.Started)),
            active / 60, elapsed == 0 ? null : Math.Min(1, active / (2 * elapsed)),
            costs.Count == 0 || costs.Count != items.Count ? null : costs.Average(), costs.Count);
    });

    public void Cost(string id, long? cents, string? note)
    {
        if (cents is < 0 or > 10_000_000_000) throw new DomainError("Informe o custo em centavos de real.");
        store.Write(db =>
        {
            Find(db, id);
            if (cents == null) { db.Exec("DELETE FROM work_costs WHERE work=$i", id); return; }
            db.Exec("""
                INSERT INTO work_costs VALUES($i,$c,$m,$n)
                ON CONFLICT(work) DO UPDATE SET cents=excluded.cents,note=excluded.note,updated=excluded.updated
                """, id, cents, Store.Text(note, 500, "a origem e o que o custo inclui"), store.Now);
        });
    }

    private static double Seconds(string a, string b) => Math.Max(0, (DateTimeOffset.Parse(b) - DateTimeOffset.Parse(a)).TotalSeconds);
    private void Mark(SqliteConnection db, WorkItem item, string state, string note)
    {
        db.Exec("UPDATE work_items SET state=$s,note=$m,finished_at=$n WHERE id=$i", state, note, store.Now, item.Id);
        db.Exec("UPDATE work_runs SET state=$s,ended_at=$n WHERE work=$i AND state='rodando'", state, store.Now, item.Id);
        Touch(db, item.Id);
        Log(db, item.Id, "Central", note);
        store.Event(db, item.Project, "trabalho." + state, item.Id);
    }

    private void SetPlan(SqliteConnection db, WorkItem item, string plan, string source)
    {
        db.Exec("UPDATE work_items SET plan=$p,plan_source=$s,state='plano',note='Revise o plano e confirme a execução.' WHERE id=$i",
            plan, source, item.Id);
        Touch(db, item.Id);
        Log(db, item.Id, source, "Plano registrado. Revise o conteúdo na seção Plano.");
    }

    private void Touch(SqliteConnection db, string id) =>
        db.Exec("UPDATE work_items SET updated=$n,revision=revision+1 WHERE id=$i", store.Now, id);
    private void Log(SqliteConnection db, string id, string author, string message) =>
        db.Exec("INSERT INTO work_messages(work,author,body,created) VALUES($i,$a,$m,$n)", id, author, message, store.Now);
    private static WorkItem Changeable(SqliteConnection db, string id, int revision)
    {
        var item = Find(db, id);
        if (item.Revision != revision) throw new DomainError("Este trabalho mudou. Reabra a página para ver a versão atual.");
        return item;
    }
    private static void RequireDraft(WorkItem item)
    {
        if (item.State is not ("rascunho" or "plano" or "falhou")) throw new DomainError("Encerre a etapa atual antes de planejar.");
    }
    private static WorkItem ReadWork(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
        r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),
        r.GetString(11),r.GetString(12),r.GetInt32(13),r.GetInt32(14),r.NullableText(15),r.GetString(16));
    private static WorkRun ReadRun(SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
        r.GetString(4),r.GetString(5),r.NullableText(6));
}
