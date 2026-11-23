using System.Net;
using System.Text;
using System.Text.Json;
using Central.Domain;
using Central.Operations;

using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "CENARIOS_DE_COTAS.json")));
var cases = document.RootElement.GetProperty("scenarios").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetString()!);
var testRoot = Path.Combine(Path.GetTempPath(), "Central testes " + Store.Id());
Directory.CreateDirectory(testRoot);
var total = 0;
var passed = 0;
var clock = new ManualClock();

foreach (var (name, data) in cases)
{
    await Run(name, async env =>
    {
        switch (name)
        {
            case "independent_groups":
            case "shared_partner_group":
            case "weekly_intersection":
            case "global_constraint":
                foreach (var model in data.GetProperty("models").EnumerateObject())
                {
                    var groups = model.Value.EnumerateArray().Select(value => value.GetString()).ToArray();
                    var limits = data.GetProperty("limits").EnumerateArray().Where(l => groups.Contains(S(l, "bucket")))
                        .Select(l => env.Limit(S(l, "bucket"), S(l, "window"), S(l, "status"))).ToArray();
                    env.Broker.Record(env.Connection("a"), model.Name, "v1", false, limits);
                    var request = env.Intent("a", model.Name);
                    var available = model.Name == "model-c" && name == "independent_groups";
                    Check(env.Broker.Reserve(request, "worker").Status == (available ? "reserved" : "cota_esgotada"));
                }
                break;
            case "shared_project_keys":
                var shared = data.GetProperty("connections").EnumerateArray().ToArray();
                foreach (var key in shared) env.Add(S(key, "id"), subject: S(key, "quotaSubject"), remaining: 1);
                Check(env.Broker.Reserve(env.Intent(S(shared[0], "id")), "worker1").Ticket != null);
                Check(env.Broker.Reserve(env.Intent(S(shared[1], "id")), "worker2").Status == "reserva_de_cota");
                Check(env.Broker.Observations().Count == 1);
                break;
            case "unknown_quota":
                var observation = data.GetProperty("observation");
                env.Broker.Record(env.Connection("a"), "model", "v1", false,
                    [env.Limit("unknown", "short", S(observation, "status"), null) with { Unit = S(observation, "unit") }]);
                var actual = env.Broker.Observations().Single();
                Check(actual.Remaining == null && actual.Reset == null && actual.Unit == "unknown");
                Check(env.Broker.Reserve(env.Intent("a"), "worker").Status == "cota_desconhecida");
                break;
            case "fixed_identity_unavailable":
                env.Add(S(data, "selectedConnection"), status: S(data, "connectionStatus"));
                env.Add(S(data, "connectorDefaultConnection"));
                Check(env.Broker.Reserve(env.Intent(S(data, "selectedConnection")), "worker").Status == "identidade_indisponivel");
                Check(env.Broker.Effects(env.Project).Count == 0);
                break;
            case "ambiguous_video_submission":
                env.Add("a");
                var video = env.Intent("a");
                var ticket = env.Broker.Reserve(video, "worker").Ticket!;
                env.Broker.BeginSend(video, ticket);
                var handle = S(data, "remoteJobId");
                env.Broker.Receive(ticket, "ambiguous", "scope-a", handle, video.Identity);
                Check(env.Broker.Reserve(video, "worker2").Status == "ambiguous");
                Throws(() => env.Broker.Reconcile(env.Project, ticket.Effect, video.Identity, "outro", false, 0, "inspeção"));
                env.Broker.Reconcile(env.Project, ticket.Effect, video.Identity, handle, false, 0, "operação original conferida");
                Check(env.Broker.Reserve(video, "worker2").Status == "completed" && env.Broker.Effects(env.Project).Single().RemoteId == handle);
                break;
            case "paid_fallback_disabled":
                env.Add("api");
                env.Broker.Wallet("paid", "BRL_centavos", 1000);
                Check(env.Broker.Reserve(env.Intent("api") with { Wallet = "paid", UpperCost = 30, AdditionalSpend = data.GetProperty("allowAdditionalSpend").GetBoolean() }, "worker").Status == "orcamento_indisponivel");
                Check(env.Broker.Effects(env.Project).Count == 0);
                break;
            case "exclusive_lease_race":
                env.Add("a");
                var which = data.GetProperty("workers").EnumerateArray().Select(w => w.GetString()!).ToArray();
                var jobs = which.Select(_ => env.Intent("a")).ToArray();
                using (var latch = new Barrier(2))
                {
                    var allocations = await Task.WhenAll(which.Select((worker, i) => Task.Run(() =>
                    {
                        latch.SignalAndWait();
                        return env.Broker.Reserve(jobs[i], worker);
                    })));
                    Check(allocations.Count(item => item.Ticket != null) == 1 && allocations.Any(x => x.Status == "perfil_ocupado"));
                    var winner = Array.FindIndex(allocations, allocation => allocation.Ticket != null);
                    var original = allocations[winner].Ticket!;
                    clock.Advance(31);
                    var replacement = env.Broker.Reserve(jobs[winner], "worker-new").Ticket!;
                    Check(replacement.Fence > original.Fence && replacement.Effect == original.Effect);
                    Throws(() => env.Broker.BeginSend(jobs[winner], original));
                    env.Broker.BeginSend(jobs[winner], replacement);
                }
                break;
            case "separate_products":
                foreach (var quota in data.GetProperty("limits").EnumerateArray())
                {
                    var product = S(quota, "product");
                    env.Broker.Record(env.Connection(product) with { Product = product }, "model", "v1", false,
                        [env.Limit(S(quota, "bucket"), "short", S(quota, "status")) with { Product = product }]);
                }
                Check(env.Broker.Reserve(env.Intent("antigravity"), "worker").Status == "cota_esgotada");
                Check(env.Broker.Reserve(env.Intent("flow"), "worker").Ticket != null);
                break;
            case "model_not_entitled":
                foreach (var model in data.GetProperty("connectionModels").EnumerateArray()) env.Add("a", model: model.GetString()!);
                env.Add("b", model: S(data, "requestedModel"));
                Check(env.Broker.Reserve(env.Intent("a", S(data, "requestedModel")), "worker").Status == "modelo_ou_catalogo_inelegivel");
                break;
            default: throw new InvalidOperationException("Fixture não executada: " + name);
        }
    });
}

await Run("catalogo_e_snapshot_mudaram", env =>
{
    env.Add("a");
    var intent = env.Intent("a");
    var ticket = env.Broker.Reserve(intent, "worker").Ticket!;
    env.Broker.Record(env.Connection("a"), "model", "v2", true, [env.Limit("third-party", "another", "available")]);
    Throws(() => env.Broker.BeginSend(intent, ticket));
    clock.Advance(31);
    env.Broker.Record(env.Connection("a"), "model", "v2", false, [env.Limit("third-party", "another", "available") with { Expires = env.Store.Millis }]);
    Check(env.Broker.Reserve(env.Intent("a") with { Catalog = "v2" }, "worker").Status == "snapshot_vencido");
    return Task.CompletedTask;
});
await Run("carteira_compartilhada_e_cobranca_revalidada", env =>
{
    env.Add("a");
    env.Add("b");
    env.Broker.Wallet("family", "credits", 10);
    var a = env.Intent("a") with { Wallet = "family", AdditionalSpend = true, UpperCost = 6 };
    var b = env.Intent("b") with { Wallet = "family", AdditionalSpend = true, UpperCost = 6 };
    var lease = env.Broker.Reserve(a, "A").Ticket!;
    Check(env.Broker.Reserve(b, "B").Status == "teto_financeiro");
    env.Broker.Wallet("family", "credits", null);
    Throws(() => env.Broker.BeginSend(a, lease));
    env.Broker.Wallet("family", "credits", 10);
    env.Broker.BeginSend(a, lease);
    env.Broker.Receive(lease, "accepted", "family", "referencia", a.Identity);
    env.Broker.RequestCancellation(a.Project, lease.Effect, "Cancelamento local");
    Check(env.Broker.Reserve(b, "B").Status == "teto_financeiro");
    env.Broker.Reconcile(a.Project, lease.Effect, a.Identity, "referencia", false, 6, "Valor confirmado");
    Check(env.Broker.Ledger(a.Project).Any(entry => entry.Kind == "debito" && entry.Amount == 6));
    Check(env.Broker.Reserve(b, "B").Status == "teto_financeiro");
    return Task.CompletedTask;
});
await Run("queda_antes_e_depois_do_envio_sem_handle", env =>
{
    env.Add("a");
    var intent = env.Intent("a");
    var first = env.Broker.Reserve(intent, "primeiro").Ticket!;
    clock.Advance(31);
    var replacement = new Coordinator(new Store(env.Store.Root, clock));
    var second = replacement.Reserve(intent, "segundo").Ticket!;
    Check(first.Effect == second.Effect);
    replacement.BeginSend(intent, second);
    clock.Advance(31);
    var restarted = new Coordinator(new Store(env.Store.Root, clock));
    Check(restarted.Reserve(intent, "terceiro").Status == "ambiguous");
    Check(restarted.Effects(env.Project).Single().RemoteId == null);
    Throws(() => restarted.Reconcile(env.Project, second.Effect, intent.Identity, null, false, null, "busca inconclusiva"));
    Check(restarted.Reserve(intent with { Key = "duplicado" }, "terceiro").Status == "reconciliar_efeito_anterior");
    return Task.CompletedTask;
});
await Run("reenvio_upload_preserva_handle_original", env =>
{
    env.Add("a");
    var upload = env.Intent("a") with { Key = "video-privado-versao-1" };
    var lease = env.Broker.Reserve(upload, "A").Ticket!;
    env.Broker.BeginSend(upload, lease);
    env.Broker.Receive(lease, "accepted", "canal-sintetico", "id-localmente-observado", upload.Identity);
    clock.Advance(31);
    Check(env.Broker.Reserve(upload, "B").Status == "ambiguous");
    Check(env.Broker.Effects(env.Project).Count == 1);
    Check(env.Broker.Effects(env.Project).Single().RemoteId == "id-localmente-observado");
    return Task.CompletedTask;
});
await Run("restore_bloqueia_efeito_posterior", env =>
{
    env.Add("a");
    var intent = env.Intent("a");
    var backup = Path.Combine(testRoot, Store.Id());
    Backup.Create(env.Store, backup);
    var afterSnapshot = env.Broker.Reserve(intent, "worker").Ticket!;
    env.Broker.BeginSend(intent, afterSnapshot);
    var target = Path.Combine(testRoot, Store.Id());
    Backup.Restore(backup, target);
    var restored = new Coordinator(new Store(target, clock));
    Check(restored.Reserve(intent, "worker").Status == "reconciliar_restore");
    return Task.CompletedTask;
});
await Run("isolamento_hash_idempotencia_e_versao", async env =>
{
    var second = env.Store.CreateProject("Outro", "software", "restrito", "outra-chave");
    var data = Encoding.UTF8.GetBytes("briefing real");
    var first = await env.Store.Import(env.Project, null, "briefing.txt", new MemoryStream(data), "origem", "fictício", "import");
    var same = await env.Store.Import(env.Project, null, "briefing.txt", new MemoryStream(data), "origem", "fictício", "import");
    Check(same == first);
    Throws(() => env.Store.GetAsset(second, first.Id));
    Throws(() => env.Store.CreateTask(second, "cruzar", [first.Task]));
    var again = new Store(env.Store.Root, clock);
    Check(Store.HashFile(again.Verify(again.GetAsset(env.Project, first.Id))) == Store.Hash(data));
    var task = again.CreateTask(env.Project, "Pausar", []);
    again.Advance(env.Project, task, 1, "pausar", "Retomar do checkpoint após reinício");
    Throws(() => again.Advance(env.Project, task, 1, "retomar", "versão antiga"));
    Check(again.Checkpoints(env.Project).First().Next.StartsWith("Retomar"));
});
await Run("notebook_transporte_processing_failed", async _ =>
{
    var source = "notebooks/n-1/sources/s-1";
    using var handler = new FixtureHttp(source);
    using var http = new HttpClient(handler);
    var notebook = new Notebook(http);
    var result = await notebook.WaitForSource(source, "demo-project", "fixture", 2, CancellationToken.None, new ImmediateClock());
    Check(result.State == "FAILED" && result.Name == source && handler.Count == 2);
});

Console.WriteLine($"Resultado | {passed}/{total} casos | {cases.Count} fixtures fornecidas | teste local de domínio");
if (passed == total) Directory.Delete(testRoot, recursive: true);
return passed == total ? 0 : 1;

async Task Run(string name, Func<TestCase, Task> test)
{
    total++;
    try
    {
        await test(new TestCase(new Store(Path.Combine(testRoot, name), clock)));
        Console.WriteLine("OK " + name);
        passed++;
    }
    catch (Exception error) { Console.WriteLine($"FALHOU {name}\n{error}"); }
}
static string S(JsonElement item, string key) => item.GetProperty(key).GetString()!;
static void Check(bool condition)
{
    if (!condition) throw new InvalidOperationException("Aceite divergente.");
}
static void Throws(Action action)
{
    try { action(); }
    catch (DomainError) { return; }
    throw new InvalidOperationException("A operação indevida foi aceita.");
}

sealed class ManualClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(int seconds) => now = now.AddSeconds(seconds);
}
sealed class ImmediateClock : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}
sealed class FixtureHttp(string source) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Count++;
        if (request.RequestUri?.ToString() != $"https://notebook.googleapis.com/v1/{source}" ||
            request.Headers.Authorization?.Scheme != "Bearer" || !request.Headers.Contains("x-goog-user-project"))
            throw new InvalidOperationException("Contrato REST divergente.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Store.Json(new { name = source, state = Count == 1 ? "PROCESSING" : "FAILED" }))
        });
    }
}
sealed class TestCase(Store store)
{
    public Store Store => store;
    public Coordinator Broker { get; } = new(store);
    public string Project { get; } = store.CreateProject("Dados fictícios", "software", "interno", Store.Id());
    public Connection Connection(string id, string status = "ready") =>
        new(id, "owner-" + id, "test", "mask-" + id, status, "profile-" + id, 1, true, true, "1", store.Millis + 3600000, ["interno"]);
    public Limit Limit(string bucket, string window, string status, long? remaining = 5, string subject = "subject") =>
        new(subject + "-" + bucket, subject, "test", bucket, window, "requests", status, remaining, store.Millis + 300000, null, "fixture sintética");
    public void Add(string id, string model = "model", string? subject = null, string status = "ready", long remaining = 100) =>
        Broker.Record(Connection(id, status), model, "v1", false, [Limit("base", "short", "available", remaining, subject ?? id)]);
    public Intent Intent(string id, string model = "model") =>
        new(Project, store.CreateTask(Project, "Tarefa " + id, [], "fixa"), 1, Store.Id(), new string('a', 64),
            id, "mask-" + id, model, "v1", null, 0);
}
