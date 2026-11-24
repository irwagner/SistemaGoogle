using Microsoft.Data.Sqlite;

namespace Central.Domain;

public sealed record Connection(string Id, string Account, string Product, string? Identity, string Status, string Profile,
    int Concurrency, bool Isolated, bool? OverageOff, string Version, long VerifiedUntil, string[] AllowedClasses);
public sealed record Limit(string Id, string Subject, string Product, string Bucket, string Window, string Unit,
    string Status, long? Remaining, long Expires, long? Reset, string Source, long? Observed = null);
public sealed record Intent(string Project, string Task, int Version, string Key, string PayloadHash, string Connection,
    string Identity, string Model, string Catalog, string? Wallet, long? UpperCost, bool AdditionalSpend = false,
    IReadOnlyDictionary<string, long>? Demand = null);
public sealed record ResourceCard(string Id, string Product, string Account, string Profile, string State, string Version);
public sealed record Ticket(string Effect, string Worker, int Fence, long Expires);
public sealed record Allocation(Ticket? Ticket, string Status);
public sealed record Effect(string Id, string Task, string State, string? Connection, string? Identity,
    string? Model, string? RemoteScope, string? RemoteId);
public sealed record LedgerEntry(string Effect, string Wallet, string Kind, long? Amount, string Unit, string Certainty);

public sealed class Coordinator(Store store)
{
    private const int LeaseDuration = 30_000;

    public void Record(Connection connection, string model, string catalog, bool retired, IEnumerable<Limit> observations, bool append = false)
    {
        ValidateConnection(connection);
        Store.Text(model, 120, "o modelo upstream");
        Store.Text(catalog, 120, "a versão do catálogo");
        store.Write(db =>
        {
            db.Exec("INSERT OR IGNORE INTO accounts VALUES($id,$label)", connection.Account, connection.Account);
            db.Exec("""
                INSERT INTO connections VALUES($id,$account,$product,$identity,$status,$profile,$slots,$isolated,$overage,$version,$until,$class)
                ON CONFLICT(id) DO UPDATE SET observed_identity=excluded.observed_identity,status=excluded.status,
                    profile=excluded.profile,concurrency=excluded.concurrency,isolated=excluded.isolated,
                    overage_off=excluded.overage_off,version=excluded.version,verified_until=excluded.verified_until,
                    data_classes=excluded.data_classes
                WHERE connections.account=excluded.account AND connections.product=excluded.product
                """, connection.Id, connection.Account, connection.Product, connection.Identity, connection.Status,
                connection.Profile, connection.Concurrency, connection.Isolated, connection.OverageOff,
                connection.Version, connection.VerifiedUntil, string.Join(",", connection.AllowedClasses));
            if (db.Scalar<long>("SELECT COUNT(*) FROM connections WHERE id=$i AND account=$a AND product=$p", connection.Id, connection.Account, connection.Product) != 1)
                throw new DomainError("O identificador já pertence a outro produto ou conta.");
            db.Exec("INSERT INTO entitlements VALUES($c,$m,$v,$r) ON CONFLICT(connection,model) DO UPDATE SET version=excluded.version,retired=excluded.retired",
                connection.Id, model, catalog, retired);
            if (!append) db.Exec("DELETE FROM constraints_map WHERE connection=$c AND model=$m", connection.Id, model);
            foreach (var limit in observations)
            {
                Store.Text(limit.Source, 120, "a origem da observação");
                if (limit.Remaining is < 0 || limit.Status is not ("available" or "exhausted" or "unknown")
                    || limit.Status == "unknown" && limit.Remaining.HasValue || IsPercent(limit.Unit) && limit.Remaining > 100)
                    throw new DomainError("Observação de cota inválida.");
                db.Exec("INSERT OR IGNORE INTO quota_subjects VALUES($id,'informado')", limit.Subject);
                db.Exec("INSERT OR IGNORE INTO quota_keys VALUES($id,$s,$p,$b,$u)", limit.Id, limit.Subject, limit.Product, limit.Bucket, limit.Unit);
                if (db.Scalar<long>("SELECT COUNT(*) FROM quota_keys WHERE id=$i AND subject=$s AND product=$p AND bucket=$b AND unit=$u",
                        limit.Id, limit.Subject, limit.Product, limit.Bucket, limit.Unit) != 1)
                    throw new DomainError("A chave de cota diverge do vínculo informado.");
                db.Exec("INSERT OR IGNORE INTO quota_windows VALUES($q,$w)", limit.Id, limit.Window);
                db.Exec("INSERT INTO quota_observations(quota,window,remaining,status,observed,expires,reset,source) VALUES($q,$w,$r,$s,$o,$e,$reset,$src)",
                    limit.Id, limit.Window, limit.Remaining, limit.Status, store.Millis, limit.Expires, limit.Reset, limit.Source);
                db.Exec("INSERT OR IGNORE INTO constraints_map VALUES($c,$m,$q,$w)", connection.Id, model, limit.Id, limit.Window);
            }
        });
    }

    public IReadOnlyList<ResourceCard> Connections() => store.Read(db => db.List("""
        SELECT id,product,account,profile,status,version FROM connections ORDER BY product,id
        """, r => new ResourceCard(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5))));

    private static void ValidateConnection(Connection connection)
    {
        Store.Text(connection.Id, 100, "a conexão");
        Store.Text(connection.Account, 100, "a conta mascarada");
        Store.Text(connection.Product, 80, "o produto");
        Store.Text(connection.Profile, 100, "o perfil");
        Store.Text(connection.Version, 100, "a versão do cliente");
        if (connection.Concurrency is < 1 or > 32 || connection.AllowedClasses == null ||
            connection.AllowedClasses.Any(value => value is not ("publico" or "interno" or "restrito")))
            throw new DomainError("Concorrência ou classificação inválida.");
    }

    public void Wallet(string id, string unit, long? ceiling)
    {
        Store.Text(id, 100, "a carteira");
        Store.Text(unit, 40, "a moeda ou unidade");
        if (ceiling is < 0) throw new DomainError("Teto inválido.");
        store.Write(db =>
        {
            if (db.Exec("INSERT INTO wallets VALUES($id,$unit,$max,0) ON CONFLICT(id) DO UPDATE SET ceiling=excluded.ceiling WHERE wallets.unit=excluded.unit",
                id, unit, ceiling) != 1) throw new DomainError("A unidade da carteira é imutável.");
        });
    }

    public IReadOnlyList<Effect> Effects(string project) => store.Read(db => db.List("""
        SELECT id,task,state,connection,identity,model,remote_scope,remote_id FROM effects WHERE project=$p ORDER BY created
        """, MapEffect, project));
    public IReadOnlyList<LedgerEntry> Ledger(string project) => store.Read(db => db.List("""
        SELECT l.effect,l.wallet,l.kind,l.amount,l.unit,l.certainty FROM ledger l JOIN effects e ON e.id=l.effect WHERE e.project=$p ORDER BY l.seq
        """, r => new LedgerEntry(r.GetString(0), r.GetString(1), r.GetString(2), r.NullableLong(3), r.GetString(4), r.GetString(5)), project));
    public IReadOnlyList<Limit> Observations() => store.Read(db => db.List("""
        SELECT k.id,k.subject,k.product,k.bucket,o.window,k.unit,o.status,o.remaining,o.expires,o.reset,o.source,o.observed
        FROM quota_keys k JOIN quota_observations o ON o.quota=k.id
        WHERE o.seq=(SELECT MAX(recent.seq) FROM quota_observations recent WHERE recent.quota=k.id AND recent.window=o.window)
        ORDER BY k.product,k.subject,k.id,o.window
        """, MapLimit));

    public Allocation Reserve(Intent request, string worker)
    {
        Store.Text(request.Key, 100, "a intenção lógica");
        Store.Text(worker, 100, "o executor");
        if (request.PayloadHash.Length != 64 || request.PayloadHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainError("Hash de entrada inválido.");
        var demandSignature = DemandSignature(request);
        return store.Write(db =>
        {
            Sweep(db);
            var task = Store.Task(db, request.Project, request.Task);
            var existing = db.One("""
                SELECT id,state,payload_hash,task,input_version,connection,identity,model,catalog_version,demand_signature
                FROM effects WHERE project=$p AND logical_key=$k
                """, r => new Existing(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.NullableText(5), r.NullableText(6), r.NullableText(7), r.NullableText(8), r.NullableText(9)), request.Project, request.Key);
            if (existing != null && (existing.Hash != request.PayloadHash || existing.Task != request.Task || existing.Version != request.Version ||
                existing.Connection != request.Connection || existing.Identity != request.Identity || existing.Model != request.Model ||
                existing.Catalog != request.Catalog || existing.Demand != null && existing.Demand != demandSignature))
                throw new DomainError("A intenção já está vinculada a outras entradas.");
            if (existing != null && existing.State is not ("not_sent" or "expired")) return new Allocation(null, existing.State);
            if (task.Version != request.Version || task.Policy != "fixa" || task.State is not ("pronta" or "espera"))
                return new Allocation(null, "tarefa_indisponivel");
            if (db.Scalar<long>("SELECT COUNT(*) FROM effects WHERE task=$t AND state IN ('reserved','sending','accepted','ambiguous','identity_mismatch')", request.Task) != 0)
                return new Allocation(null, "reconciliar_efeito_anterior");
            var reason = Eligibility(db, request, out var connection, out var limits);
            if (reason != null) return Wait(db, request, reason);
            if (db.Scalar<long>("SELECT COUNT(*) FROM leases l JOIN effects e ON e.id=l.effect WHERE l.profile=$p AND e.state IN ('reserved','sending','accepted','ambiguous','identity_mismatch')", connection!.Profile) >= connection.Slots)
                return Wait(db, request, "perfil_ocupado");
            var quantities = Quantities(request, limits);
            if (quantities == null) return Wait(db, request, "quantidade_por_unidade_indisponivel");
            foreach (var (limit, amount) in quantities)
            {
                var reserved = db.Scalar<long>("SELECT COALESCE(SUM(amount),0) FROM reservations WHERE resource=$r AND window=$w",
                    "quota:" + limit.Id, limit.Window);
                if (amount > limit.Remaining!.Value || reserved > limit.Remaining.Value - amount)
                    return Wait(db, request, "reserva_de_cota");
            }
            var cost = request.UpperCost;
            var wallet = request.Wallet == null ? null : db.One("SELECT unit,ceiling,spent FROM wallets WHERE id=$w",
                r => new Budget(r.GetString(0), r.NullableLong(1), r.GetInt64(2)), request.Wallet);
            if (cost is null or < 0 || cost > 0 && !request.AdditionalSpend || request.Wallet != null && wallet?.Ceiling == null
                || cost > 0 && wallet == null)
                return Wait(db, request, "orcamento_indisponivel");
            if (wallet != null)
            {
                var reserved = db.Scalar<long>("SELECT COALESCE(SUM(amount),0) FROM reservations WHERE resource=$r", "wallet:" + request.Wallet);
                if (wallet.Spent > wallet.Ceiling || reserved > wallet.Ceiling - wallet.Spent || cost > wallet.Ceiling - wallet.Spent - reserved)
                    return Wait(db, request, "teto_financeiro");
            }
            var effect = existing?.Id ?? Store.Id();
            if (existing == null)
                db.Exec("""
                    INSERT INTO effects(id,project,task,logical_key,payload_hash,input_version,connection,identity,model,catalog_version,state,created,demand_signature)
                    VALUES($id,$p,$t,$k,$hash,$version,$c,$identity,$model,$catalog,'reserved',$date,$sig)
                    """, effect, request.Project, request.Task, request.Key, request.PayloadHash, request.Version, request.Connection,
                    request.Identity, request.Model, request.Catalog, store.Now, demandSignature);
            else db.Exec("UPDATE effects SET state='reserved' WHERE id=$e", effect);
            var fence = checked(1 + (int)db.Scalar<long>("SELECT COALESCE(MAX(fence),0) FROM attempts WHERE effect=$e", effect));
            var expiry = store.Millis + LeaseDuration;
            db.Exec("INSERT INTO leases VALUES($e,$o,$f,$x,$h,$p) ON CONFLICT(effect) DO UPDATE SET owner=excluded.owner,fence=excluded.fence,expires=excluded.expires,heartbeat=excluded.heartbeat,profile=excluded.profile",
                effect, worker, fence, expiry, store.Millis, connection.Profile);
            db.Exec("INSERT INTO attempts VALUES($e,$f,$w,'reserved',$n)", effect, fence, worker, store.Millis);
            foreach (var (limit, amount) in quantities)
                db.Exec("INSERT INTO reservations VALUES($e,$r,$w,$u,$a,$x)", effect, "quota:" + limit.Id, limit.Window, limit.Unit, amount, expiry);
            if (wallet != null)
            {
                db.Exec("INSERT INTO reservations VALUES($e,$r,'budget',$u,$a,$x)", effect, "wallet:" + request.Wallet, wallet.Unit, cost, expiry);
                AddLedger(db, effect, request.Wallet!, "reserva", cost, wallet.Unit, "estimado");
            }
            db.Exec("UPDATE tasks SET state='execucao',reason=NULL WHERE id=$t", request.Task);
            store.SaveCheckpoint(db, request.Project, request.Task, "Confirmar entradas e identidade na fronteira do envio.", Store.Json(new { effect, request.PayloadHash }));
            store.Event(db, request.Project, "efeito.reservado", effect);
            return new Allocation(new Ticket(effect, worker, fence, expiry), "reserved");
        });
    }

    private string? Eligibility(SqliteConnection db, Intent request, out Resource? connection, out IReadOnlyList<Limit> limits)
    {
        limits = [];
        connection = db.One("""
            SELECT observed_identity,status,profile,concurrency,isolated,overage_off,verified_until,data_classes
            FROM connections WHERE id=$c
            """, r => new Resource(r.NullableText(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetBoolean(5), r.NullableLong(6), r.GetString(7)), request.Connection);
        if (connection == null || connection.Status != "ready" || string.IsNullOrEmpty(connection.Identity) ||
            connection.Identity != request.Identity || !connection.Isolated || !(connection.Until > store.Millis))
            return "identidade_indisponivel";
        var classification = db.Scalar<string>("SELECT classification FROM projects WHERE id=$p", request.Project);
        if (!connection.Classes.Split(',').Contains(classification)) return "dados_sem_autorizacao";
        if (connection.OverageOff != true) return "excedente_nao_confirmado";
        if (db.Scalar<long>("SELECT COUNT(*) FROM service_flags WHERE key='restore_hold'") != 0) return "reconciliar_restore";
        if (db.Scalar<long>("SELECT COUNT(*) FROM entitlements WHERE connection=$c AND model=$m AND version=$v AND retired=0",
            request.Connection, request.Model, request.Catalog) == 0) return "modelo_ou_catalogo_inelegivel";
        limits = db.List("""
            SELECT k.id,k.subject,k.product,k.bucket,m.window,k.unit,o.status,o.remaining,o.expires,o.reset,o.source,o.observed
            FROM constraints_map m JOIN quota_keys k ON k.id=m.quota JOIN quota_observations o ON o.quota=m.quota AND o.window=m.window
            WHERE m.connection=$c AND m.model=$m AND o.seq=(SELECT MAX(latest.seq) FROM quota_observations latest WHERE latest.quota=m.quota AND latest.window=m.window)
            """, MapLimit, request.Connection, request.Model);
        if (limits.Count == 0 || limits.Any(limit => limit.Status == "unknown")) return "cota_desconhecida";
        if (limits.Any(limit => limit.Expires <= store.Millis)) return "snapshot_vencido";
        return limits.Any(limit => limit.Status == "exhausted" || limit.Remaining == 0) ? "cota_esgotada" : null;
    }

    private static bool IsPercent(string unit) => unit is "%" or "percentual" or "percent";
    private static string DemandSignature(Intent request)
    {
        if (request.Demand?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 40 || pair.Value <= 0 || IsPercent(pair.Key)) == true)
            throw new DomainError("Demanda deve ser positiva e usar unidade contável.");
        return Store.Json(new { units = request.Demand?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            request.Wallet, request.UpperCost, request.AdditionalSpend });
    }
    private static List<(Limit Limit, long Amount)>? Quantities(Intent request, IReadOnlyList<Limit> limits)
    {
        var result = new List<(Limit, long)>();
        foreach (var limit in limits.Where(limit => limit.Remaining.HasValue && !IsPercent(limit.Unit)))
        {
            if (request.Demand?.TryGetValue(limit.Unit, out var amount) == true) result.Add((limit, amount));
            else if (limit.Unit == "requests") result.Add((limit, 1));
            else return null;
        }
        return result;
    }

    public void BeginSend(Intent intent, Ticket ticket)
    {
        store.Write(db =>
        {
            var effect = Owned(db, ticket, "reserved");
            if (effect.Connection != intent.Connection || effect.Identity != intent.Identity || effect.Model != intent.Model ||
                db.Scalar<long>("SELECT COUNT(*) FROM effects WHERE id=$e AND project=$p AND task=$t AND input_version=$v AND payload_hash=$h AND catalog_version=$c",
                    ticket.Effect, intent.Project, intent.Task, intent.Version, intent.PayloadHash, intent.Catalog) != 1 ||
                Store.Task(db, intent.Project, intent.Task).Version != intent.Version)
                throw new DomainError("A entrada ou identidade diverge da reserva.");
            var reason = Eligibility(db, intent, out var resource, out var limits);
            if (reason != null) throw new DomainError(reason);
            if (db.Scalar<string>("SELECT profile FROM leases WHERE effect=$e", ticket.Effect) != resource!.Profile)
                throw new DomainError("O perfil mudou após a reserva.");
            var demand = db.Scalar<string>("SELECT demand_signature FROM effects WHERE id=$e", ticket.Effect);
            var quantities = Quantities(intent, limits);
            var reservedLimits = db.List("SELECT resource,window,amount FROM reservations WHERE effect=$e AND resource LIKE 'quota:%'",
                r => (r.GetString(0), r.GetString(1), r.GetInt64(2)), ticket.Effect);
            if (demand != null && demand != DemandSignature(intent) || quantities == null ||
                !reservedLimits.ToHashSet().SetEquals(quantities.Select(q => ("quota:" + q.Limit.Id, q.Limit.Window, q.Amount))))
                throw new DomainError("Os limites mudaram após a reserva.");
            foreach (var (limit, _) in quantities)
                if (db.Scalar<long>("SELECT COALESCE(SUM(amount),0) FROM reservations WHERE resource=$r AND window=$w", "quota:" + limit.Id, limit.Window) > limit.Remaining)
                    throw new DomainError("A capacidade observada caiu após a reserva.");
            var financial = db.One("""
                SELECT r.resource,r.amount,w.ceiling,w.spent FROM reservations r JOIN wallets w ON r.resource='wallet:'||w.id
                WHERE r.effect=$e AND r.window='budget'
                """, r => new Financial(r.GetString(0), r.GetInt64(1), r.NullableLong(2), r.GetInt64(3)), ticket.Effect);
            if ((financial?.Resource != (intent.Wallet == null ? null : "wallet:" + intent.Wallet)) ||
                intent.UpperCost is null or < 0 || intent.UpperCost != (financial?.Amount ?? 0) ||
                intent.UpperCost > 0 && !intent.AdditionalSpend ||
                financial != null && (financial.Ceiling == null || financial.Spent > financial.Ceiling ||
                    db.Scalar<long>("SELECT COALESCE(SUM(amount),0) FROM reservations WHERE resource=$r", financial.Resource) > financial.Ceiling - financial.Spent))
                throw new DomainError("O orçamento mudou após a reserva.");
            Change(db, ticket, "sending");
            db.Exec("UPDATE reservations SET expires=NULL WHERE effect=$e", ticket.Effect);
            store.Event(db, intent.Project, "efeito.intencao_persistida", ticket.Effect);
        });
    }

    public void Heartbeat(Ticket ticket) => store.Write(db =>
    {
        Owned(db, ticket, "reserved", "sending", "accepted");
        var expiry = store.Millis + LeaseDuration;
        db.Exec("UPDATE leases SET heartbeat=$h,expires=$x WHERE effect=$e", store.Millis, expiry, ticket.Effect);
        db.Exec("UPDATE reservations SET expires=$x WHERE effect=$e AND expires IS NOT NULL", expiry, ticket.Effect);
    });

    public void Receive(Ticket ticket, string state, string? scope, string? remote, string? identity)
    {
        if (state is not ("accepted" or "ambiguous" or "not_sent") || (scope == null) != (remote == null) ||
            state == "accepted" && remote == null || state == "not_sent" && remote != null)
            throw new DomainError("Recibo inválido.");
        store.Write(db =>
        {
            var effect = Owned(db, ticket, "sending", "accepted");
            if (effect.RemoteId != null && remote != null && (effect.RemoteId != remote || effect.RemoteScope != scope))
                throw new DomainError("O handle remoto original mudou.");
            if (state == "not_sent" && effect.RemoteId != null) throw new DomainError("Reconcilie o handle original.");
            if (state == "accepted" && identity != effect.Identity || identity != null && identity != effect.Identity)
            {
                state = "identity_mismatch";
                db.Exec("UPDATE connections SET status='suspended' WHERE id=$c", effect.Connection);
            }
            db.Exec("UPDATE effects SET remote_scope=COALESCE(remote_scope,$scope),remote_id=COALESCE(remote_id,$remote) WHERE id=$e", scope, remote, effect.Id);
            db.Exec("INSERT INTO receipts(effect,identity,kind,remote_id,created) VALUES($e,$i,$k,$r,$now)", effect.Id, identity, state, remote, store.Now);
            Change(db, ticket, state);
            var project = db.Scalar<string>("SELECT project FROM effects WHERE id=$e", effect.Id)!;
            if (state == "not_sent") Release(db, effect, "pronta", null, "liberacao");
            else
            {
                db.Exec("UPDATE tasks SET state='espera',reason=$r WHERE id=$t", state == "accepted" ? "coletar_operacao_original" : "reconciliacao", effect.Task);
                store.SaveCheckpoint(db, project, effect.Task, "Conferir a execução original antes de qualquer reenvio.",
                    Store.Json(new { effect = effect.Id, handle = remote ?? effect.RemoteId }));
            }
            store.Event(db, project, "efeito." + state, effect.Id);
        });
    }

    public void Sweep() => store.Write(Sweep);

    public void RequestCancellation(string project, string id, string evidence)
    {
        evidence = Store.Text(evidence, 300, "o motivo do cancelamento");
        store.Write(db =>
        {
            var effect = db.One("""
                SELECT id,task,state,connection,identity,model,remote_scope,remote_id FROM effects WHERE project=$p AND id=$i
                """, MapEffect, project, id) ?? throw new DomainError("Efeito não encontrado.");
            if (effect.State is not ("reserved" or "sending" or "accepted" or "ambiguous"))
                throw new DomainError("O efeito não está em uma etapa cancelável.");
            var state = effect.State == "reserved" ? "cancelled_locally" : "ambiguous";
            db.Exec("UPDATE effects SET state=$s WHERE id=$i", state, id);
            db.Exec("UPDATE attempts SET state=$s WHERE effect=$i AND fence=(SELECT fence FROM leases WHERE effect=$i)", state, id);
            if (state == "cancelled_locally") Release(db, effect, "cancelada", null, "liberacao");
            else db.Exec("UPDATE tasks SET state='espera',reason='cancelamento_sem_confirmacao' WHERE id=$t", effect.Task);
            store.SaveCheckpoint(db, project, effect.Task, state == "ambiguous" ? "Conferir cancelamento e cobrança com o provedor." : "Cancelamento local antes do envio.", evidence);
            store.Event(db, project, "efeito." + state, id);
        });
    }

    private void Sweep(SqliteConnection db)
    {
        var effects = db.List("""
            SELECT e.id,e.task,e.state,e.connection,e.identity,e.model,e.remote_scope,e.remote_id
            FROM effects e JOIN leases l ON l.effect=e.id
            WHERE l.expires<=$n AND e.state IN ('reserved','sending','accepted')
            """, MapEffect, store.Millis);
        foreach (var effect in effects)
        {
            var state = effect.State == "reserved" ? "expired" : "ambiguous";
            db.Exec("UPDATE effects SET state=$s WHERE id=$e", state, effect.Id);
            db.Exec("UPDATE attempts SET state=$s WHERE effect=$e AND fence=(SELECT fence FROM leases WHERE effect=$e)", state, effect.Id);
            if (state == "expired") Release(db, effect, "pronta", null, "expiracao");
            else db.Exec("UPDATE tasks SET state='espera',reason='reconciliacao' WHERE id=$t", effect.Task);
            var project = db.Scalar<string>("SELECT project FROM effects WHERE id=$e", effect.Id)!;
            store.SaveCheckpoint(db, project, effect.Task, state == "expired" ? "Voltar a reservar." : "Reconciliar a operação original.",
                Store.Json(new { effect = effect.Id, handle = effect.RemoteId }));
            store.Event(db, project, "efeito." + state, effect.Id);
        }
    }

    public void Reconcile(string project, string effectId, string identity, string? handle, bool confirmedNotExecuted, long? confirmedCost, string evidence)
    {
        Store.Text(evidence, 300, "a referência sanitizada da prova");
        if (confirmedCost < 0 || confirmedNotExecuted && confirmedCost.HasValue) throw new DomainError("Custo inválido.");
        store.Write(db =>
        {
            var effect = db.One("""
                SELECT id,task,state,connection,identity,model,remote_scope,remote_id FROM effects WHERE project=$p AND id=$e
                """, MapEffect, project, effectId) ?? throw new DomainError("Efeito não encontrado.");
            if (effect.State is not ("ambiguous" or "accepted") || effect.Identity != identity || effect.RemoteId != handle ||
                handle == null && !confirmedNotExecuted)
                throw new DomainError("A prova deve corresponder à identidade e à operação original.");
            var state = confirmedNotExecuted ? "not_sent" : "completed";
            db.Exec("UPDATE effects SET state=$s WHERE id=$e", state, effectId);
            db.Exec("UPDATE attempts SET state=$s WHERE effect=$e AND fence=(SELECT fence FROM leases WHERE effect=$e)", state, effectId);
            db.Exec("INSERT INTO receipts(effect,identity,kind,remote_id,created) VALUES($e,$i,$k,$r,$now)",
                effectId, identity, state, handle, store.Now);
            if (state == "completed")
            {
                var wallet = db.One("SELECT substr(resource,8),unit FROM reservations WHERE effect=$e AND window='budget'",
                    r => Tuple.Create(r.GetString(0), r.GetString(1)), effectId);
                if (wallet != null && confirmedCost.HasValue)
                {
                    db.Exec("UPDATE wallets SET spent=spent+$a WHERE id=$w", confirmedCost, wallet.Item1);
                    AddLedger(db, effectId, wallet.Item1, "debito", confirmedCost, wallet.Item2, "confirmado");
                }
                else if (wallet != null)
                {
                    AddLedger(db, effectId, wallet.Item1, "debito", null, wallet.Item2, "desconhecido");
                    db.Exec("DELETE FROM reservations WHERE effect=$e AND window<>'budget'", effectId);
                    db.Exec("UPDATE tasks SET state='revisao',reason='custo_pendente' WHERE id=$t", effect.Task);
                }
                if (wallet == null || confirmedCost.HasValue) Release(db, effect, "revisao", null, "liberacao");
            }
            else
            {
                db.Exec("UPDATE effects SET remote_id=NULL,remote_scope=NULL WHERE id=$e", effectId);
                Release(db, effect, "pronta", null, "liberacao");
            }
            store.SaveCheckpoint(db, project, effect.Task, state == "completed" ? "Validar os artefatos e o custo antes do aceite." : "Alocar a mesma intenção.", evidence);
            store.Event(db, project, "efeito.reconciliado", effectId);
        });
    }

    private void Release(SqliteConnection db, Effect effect, string state, string? reason, string ledger)
    {
        foreach (var item in db.List("SELECT substr(resource,8),unit,amount FROM reservations WHERE effect=$e AND window='budget'",
            r => Tuple.Create(r.GetString(0), r.GetString(1), r.GetInt64(2)), effect.Id))
            AddLedger(db, effect.Id, item.Item1, ledger, item.Item3, item.Item2, "confirmado");
        db.Exec("DELETE FROM reservations WHERE effect=$e", effect.Id);
        db.Exec("UPDATE tasks SET state=$s,reason=$r WHERE id=$t", state, reason, effect.Task);
    }

    private void AddLedger(SqliteConnection db, string effect, string wallet, string kind, long? cost, string unit, string certainty) =>
        db.Exec("INSERT INTO ledger(effect,wallet,kind,amount,unit,certainty,created) VALUES($e,$w,$k,$a,$u,$q,$now)",
            effect, wallet, kind, cost, unit, certainty, store.Now);

    private Allocation Wait(SqliteConnection db, Intent intent, string reason)
    {
        db.Exec("UPDATE tasks SET state='espera',reason=$r WHERE id=$t", reason, intent.Task);
        store.SaveCheckpoint(db, intent.Project, intent.Task, "Resolver " + reason + " e reavaliar a mesma identidade.",
            Store.Json(new { intent.Connection, intent.Model, intent.Catalog }));
        store.Event(db, intent.Project, "alocacao.espera", reason);
        return new Allocation(null, reason);
    }

    private Effect Owned(SqliteConnection db, Ticket ticket, params string[] states)
    {
        var effect = db.One("""
            SELECT e.id,e.task,e.state,e.connection,e.identity,e.model,e.remote_scope,e.remote_id
            FROM effects e JOIN leases l ON l.effect=e.id WHERE e.id=$e AND l.owner=$o AND l.fence=$f AND l.expires>$n
            """, MapEffect, ticket.Effect, ticket.Worker, ticket.Fence, store.Millis);
        if (effect == null || !states.Contains(effect.State)) throw new DomainError("Ticket expirado, substituído ou incompatível.");
        return effect;
    }

    private static void Change(SqliteConnection db, Ticket ticket, string state)
    {
        db.Exec("UPDATE effects SET state=$s WHERE id=$e", state, ticket.Effect);
        db.Exec("UPDATE attempts SET state=$s WHERE effect=$e AND fence=$f", state, ticket.Effect, ticket.Fence);
    }

    private static Effect MapEffect(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
        row.NullableText(3), row.NullableText(4), row.NullableText(5), row.NullableText(6), row.NullableText(7));
    private static Limit MapLimit(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.NullableLong(7), row.GetInt64(8), row.NullableLong(9), row.GetString(10), row.GetInt64(11));
    private sealed record Existing(string Id, string State, string Hash, string Task, int Version, string? Connection, string? Identity, string? Model, string? Catalog, string? Demand);
    private sealed record Resource(string? Identity, string Status, string Profile, int Slots, bool Isolated, bool? OverageOff, long? Until, string Classes);
    private sealed record Budget(string Unit, long? Ceiling, long Spent);
    private sealed record Financial(string Resource, long Amount, long? Ceiling, long Spent);
}
