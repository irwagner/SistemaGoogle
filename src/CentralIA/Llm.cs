using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralIA;

public enum LlmFailure { Limit, Transient, Auth, Bad }

public sealed class LlmException(LlmFailure kind, string message, TimeSpan? retryAfter = null, int? status = null, bool earlyHttp = false) : Exception(message)
{
    public LlmFailure Kind => kind;
    public TimeSpan? RetryAfter => retryAfter;
    public int? Status => status;
    public bool EarlyHttp => earlyHttp;
}

public sealed class LlmResult
{
    public string Content { get; set; } = "";
    public List<ToolCall> ToolCalls { get; } = new();
    public string? Finish { get; set; }
}

public sealed record RouteInfo(string ProviderId, string ProviderName, string Model);

public sealed class AccountStats
{
    public string ProviderId { get; init; } = "";
    public int Requests;
    public int Successes;
    public int RateLimits;
    public int Errors;
    public int InFlight;
    public DateTime? LastUsed;
    public string? LastError;
}

/// <summary>Cliente único para qualquer API compatível com OpenAI (Gemini, OpenRouter, OpenAI, Anthropic, Groq, DeepSeek, Ollama, gateways locais).</summary>
public static partial class LlmClient
{
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient Direct = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(20),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private sealed class PartialCall
    {
        public int Index;
        public string Id = "";
        public string Name = "";
        public readonly StringBuilder Args = new();
        public JsonNode? Extra;
    }

    public static void Authorize(HttpRequestMessage req, ProviderConfig p, string? key)
    {
        if (p.Kind == "openrouter")
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "http://127.0.0.1");
            req.Headers.TryAddWithoutValidation("X-Title", "Central IA");
        }
        if (string.IsNullOrEmpty(key)) return;
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (p.Kind == "anthropic")
        {
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
    }

    public static async Task<LlmResult> ChatAsync(ProviderConfig p, string? key, string model, JsonArray messages,
        JsonArray? tools, Func<string, Task>? onDelta, CancellationToken ct, bool requireFinal = false)
    {
        var msgs = (JsonArray)messages.DeepClone();
        foreach (var m in msgs)
        {
            if (m?["tool_calls"] is not JsonArray tcs) continue;
            foreach (var tc in tcs.OfType<JsonObject>())
            {
                if (p.Kind != "gemini") tc.Remove("extra_content");
                else if (tc["extra_content"] == null)
                    tc["extra_content"] = new JsonObject { ["google"] = new JsonObject { ["thought_signature"] = "skip_thought_signature_validator" } };
            }
        }
        var body = new JsonObject { ["model"] = model, ["messages"] = msgs, ["stream"] = true };
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();
        using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(req, p, key);

        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(TimeSpan.FromSeconds(150));
        HttpResponseMessage resp;
        try { resp = await (requireFinal ? Direct : Http).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, watchdog.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LlmException(LlmFailure.Transient, "tempo esgotado esperando resposta"); }
        catch (HttpRequestException e) { throw new LlmException(LlmFailure.Transient, "sem conexão com o serviço (" + e.Message + ")"); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var text = await resp.Content.ReadAsStringAsync(watchdog.Token);
                var failure = Classify((int)resp.StatusCode, text, resp.Headers.RetryAfter);
                throw new LlmException(failure.Kind, failure.Message, failure.RetryAfter, (int)resp.StatusCode, earlyHttp: true);
            }

            var result = new LlmResult();
            var sb = new StringBuilder();
            var calls = new List<PartialCall>();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (true)
            {
                watchdog.CancelAfter(TimeSpan.FromSeconds(180));
                string? line;
                try { line = await reader.ReadLineAsync(watchdog.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new LlmException(LlmFailure.Transient, "o modelo parou de responder no meio da resposta"); }
                catch (IOException e) { throw new LlmException(LlmFailure.Transient, "conexão interrompida (" + e.Message + ")"); }
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data); } catch { continue; }
                if (chunk is null) continue;
                if (chunk["error"] is JsonNode err)
                {
                    int code = 500;
                    try { code = err["code"]?.GetValue<int>() ?? 500; } catch { }
                    throw Classify(code, data, null);
                }
                var choice = chunk["choices"]?[0];
                if (choice is null) continue;
                var delta = choice["delta"];
                string? content = null;
                try { content = (string?)delta?["content"]; } catch { }
                if (!string.IsNullOrEmpty(content))
                {
                    sb.Append(content);
                    if (onDelta != null) await onDelta(content);
                }
                if (delta?["tool_calls"] is JsonArray tcs)
                {
                    foreach (var node in tcs)
                    {
                        if (node is not JsonObject tc) continue;
                        int? index = null;
                        try { if (tc["index"] is JsonValue iv) index = iv.GetValue<int>(); } catch { }
                        string? id = null;
                        try { id = (string?)tc["id"]; } catch { }
                        PartialCall? pc = index != null ? calls.LastOrDefault(c => c.Index == index)
                            : !string.IsNullOrEmpty(id) ? calls.LastOrDefault(c => c.Id == id)
                            : calls.LastOrDefault();
                        if (pc != null && !string.IsNullOrEmpty(id) && pc.Id.Length > 0 && pc.Id != id) pc = null;
                        if (pc == null) { pc = new PartialCall { Index = index ?? calls.Count }; calls.Add(pc); }
                        if (!string.IsNullOrEmpty(id)) pc.Id = id;
                        var fn = tc["function"];
                        string? name = null;
                        try { name = (string?)fn?["name"]; } catch { }
                        if (!string.IsNullOrEmpty(name) && pc.Name.Length == 0) pc.Name = name;
                        var args = fn?["arguments"];
                        if (args is JsonValue av && av.TryGetValue<string>(out var s)) pc.Args.Append(s);
                        else if (args is JsonObject ao) pc.Args.Append(ao.ToJsonString());
                        if (tc["extra_content"] is JsonNode extra) pc.Extra = extra.DeepClone();
                    }
                }
                try { var fr = (string?)choice["finish_reason"]; if (fr != null) result.Finish = fr; } catch { }
            }

            if (requireFinal && (result.Finish is not ("stop" or "tool_calls" or "function_call") ||
                (result.Finish != "stop" && calls.Count == 0) || (calls.Count == 0 && sb.Length == 0)))
                throw new LlmException(LlmFailure.Transient, "A resposta terminou sem um resultado utilizável.");
            result.Content = sb.ToString();
            foreach (var c in calls.Where(c => c.Name.Length > 0))
            {
                var args = c.Args.ToString();
                result.ToolCalls.Add(new ToolCall
                {
                    Id = c.Id.Length > 0 ? c.Id : "call_" + Guid.NewGuid().ToString("N")[..12],
                    Name = c.Name,
                    Arguments = string.IsNullOrWhiteSpace(args) ? "{}" : args,
                    Extra = c.Extra,
                });
            }
            return result;
        }
    }

    public static async Task<List<string>> FetchModels(ProviderConfig p, string? key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/models");
        Authorize(req, p, key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, cts.Token); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { throw new UserError("Não consegui falar com " + p.BaseUrl + ". Verifique o endereço e sua internet."); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new UserError("O serviço recusou a listagem de modelos: " + Classify((int)resp.StatusCode, text, null).Message);
            var node = JsonNode.Parse(text);
            var list = (node?["data"] as JsonArray ?? node?["models"] as JsonArray ?? new JsonArray())
                .Select(m => { try { return (string?)m?["id"] ?? (string?)m?["name"]; } catch { return null; } })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!.StartsWith("models/") ? id[7..] : id)
                .Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            return list;
        }
    }

    [GeneratedRegex("\"retryDelay\"\\s*:\\s*\"([\\d.]+)s\"")]
    private static partial Regex RetryDelay();

    public static LlmException Classify(int status, string body, RetryConditionHeaderValue? ra)
    {
        var msg = ExtractMessage(body);
        TimeSpan? retry = ra?.Delta ?? (ra?.Date is DateTimeOffset d ? d - DateTimeOffset.UtcNow : null);
        var m = RetryDelay().Match(body);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            retry = TimeSpan.FromSeconds(secs);
        var lower = body.ToLowerInvariant();
        var quota = lower.Contains("quota") || lower.Contains("resource_exhausted") || lower.Contains("rate limit")
            || lower.Contains("rate_limit") || lower.Contains("insufficient") || lower.Contains("credit") || lower.Contains("billing");
        var detail = msg.Length > 0 ? $" — {msg}" : "";
        if (status is 429 or 402 || (quota && status is 400 or 403))
        {
            if (lower.Contains("per day") || lower.Contains("perday") || lower.Contains("daily")) retry ??= TimeSpan.FromHours(1);
            return new LlmException(LlmFailure.Limit, "limite de uso atingido" + detail, retry);
        }
        if (status is 401 or 403) return new LlmException(LlmFailure.Auth, "chave inválida ou sem permissão" + detail);
        if (status is 408 or 409 || status >= 500) return new LlmException(LlmFailure.Transient, $"instabilidade no serviço ({status}){detail}");
        if (status == 404) return new LlmException(LlmFailure.Bad, "modelo não encontrado" + detail);
        return new LlmException(LlmFailure.Bad, $"pedido recusado ({status}){detail}");
    }

    private static string ExtractMessage(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            var e = n is JsonArray a ? a[0]?["error"] : n?["error"];
            string? m = null;
            if (e is JsonObject eo) m = (string?)eo["message"];
            else if (e is JsonValue ev && ev.TryGetValue<string>(out var s)) m = s;
            return Titles.Trunc((m ?? body).Trim(), 240);
        }
        catch { return Titles.Trunc(body.Trim(), 240); }
    }
}

/// <summary>
/// Pool e roteador inteligente de contas/modelos com 3 estratégias (failover, roundrobin, leastused),
/// telemetria por conta, cooldown automático e suporte a conta travada (pinned) sem troca silenciosa.
/// </summary>
public sealed class LlmRouter(SettingsService settings)
{
    private readonly ConcurrentDictionary<string, DateTime> cooldown = new();
    private readonly ConcurrentDictionary<string, AccountStats> stats = new();
    private readonly ConcurrentDictionary<string, int> cursors = new();

    public AccountStats Stat(string providerId) => stats.GetOrAdd(providerId, id => new AccountStats { ProviderId = id });

    public bool IsPaused(string key, out DateTime until) => cooldown.TryGetValue(key, out until) && until > DateTime.UtcNow;

    public void MarkCooldown(string key, string providerId, LlmException e)
    {
        var wait = e.RetryAfter ?? e.Kind switch
        {
            LlmFailure.Limit => TimeSpan.FromMinutes(1),
            LlmFailure.Auth => TimeSpan.FromMinutes(15),
            LlmFailure.Bad => TimeSpan.FromSeconds(40),
            _ => TimeSpan.FromSeconds(45),
        };
        if (wait < TimeSpan.FromSeconds(5)) wait = TimeSpan.FromSeconds(5);
        cooldown[key] = DateTime.UtcNow + wait;
        var st = Stat(providerId);
        if (e.Kind == LlmFailure.Limit) Interlocked.Increment(ref st.RateLimits);
        else Interlocked.Increment(ref st.Errors);
        st.LastError = e.Message;
    }

    public void ClearCooldown(string? providerId = null)
    {
        if (string.IsNullOrEmpty(providerId)) { cooldown.Clear(); return; }
        foreach (var k in cooldown.Keys.Where(k => k == providerId || k.StartsWith(providerId + "|")).ToList())
            cooldown.TryRemove(k, out _);
    }

    public int NextIndex(string bucket, int count)
    {
        if (count <= 1) return 0;
        var v = cursors.AddOrUpdate(bucket, 0, (_, cur) => (cur + 1) % count);
        return Math.Abs(v) % count;
    }

    public object Status() => cooldown.Where(c => c.Value > DateTime.UtcNow)
        .Select(c => new { key = c.Key, until = c.Value }).ToArray();

    public object PoolSnapshot()
    {
        var now = DateTime.UtcNow;
        return settings.Current.Providers.Select(p =>
        {
            var st = Stat(p.Id);
            var paused = cooldown.Where(c => (c.Key == p.Id || c.Key.StartsWith(p.Id + "|")) && c.Value > now)
                .Select(c => new { key = c.Key, until = c.Value }).ToArray();
            return new
            {
                providerId = p.Id,
                name = p.Name,
                kind = p.Kind,
                enabled = p.Enabled,
                requests = st.Requests,
                successes = st.Successes,
                rateLimits = st.RateLimits,
                errors = st.Errors,
                inFlight = st.InFlight,
                lastUsed = st.LastUsed,
                lastError = st.LastError,
                paused,
            };
        }).ToArray();
    }

    public List<T> OrderByStrategy<T>(string role, List<T> items, Func<T, string> getProviderId, Func<T, string> getKey)
    {
        if (items.Count <= 1) return items;
        var strategy = settings.StrategyFor(role);
        if (strategy == "roundrobin")
        {
            var start = NextIndex(role, items.Count);
            return items.Skip(start).Concat(items.Take(start)).ToList();
        }
        if (strategy == "leastused")
        {
            return items
                .OrderBy(x => IsPaused(getKey(x), out _) ? 1 : 0)
                .ThenBy(x => Stat(getProviderId(x)).InFlight)
                .ThenBy(x => Stat(getProviderId(x)).Requests)
                .ThenBy(x => Stat(getProviderId(x)).LastUsed ?? DateTime.MinValue)
                .ToList();
        }
        return items;
    }

    public async Task<(LlmResult Result, RouteInfo Route)> ChatAsync(string role, JsonArray messages, JsonArray? tools,
        Func<RouteInfo, Task>? onStart, Func<string, Task>? onDelta, Func<string, Task>? onNotice, CancellationToken ct,
        string? pinnedProviderId = null, string? pinnedModel = null, string? cliWorkspace = null)
    {
        List<(RouteEntry Route, ProviderConfig Provider)> chain;
        var pinned = !string.IsNullOrWhiteSpace(pinnedProviderId);
        if (pinned)
        {
            var p = settings.Current.Providers.FirstOrDefault(x => x.Id == pinnedProviderId && SettingsService.CanRoute(x))
                ?? throw new UserError("A conta travada para esta conversa não está mais disponível ou está desativada.");
            var m = !string.IsNullOrWhiteSpace(pinnedModel) ? pinnedModel! : p.DefaultModel;
            if (string.IsNullOrWhiteSpace(m)) throw new UserError($"A conta {p.Name} não tem modelo configurado.");
            chain = [(new RouteEntry { ProviderId = p.Id, Model = m }, p)];
        }
        else
        {
            chain = settings.ResolveChain(role);
            if (chain.Count == 0) throw new UserError("Nenhum modelo configurado. Abra “Contas e modelos” e conecte uma IA.");
            chain = OrderByStrategy(role, chain, x => x.Provider.Id, x => x.Provider.Id + "|" + x.Route.Model);
        }

        var failures = new List<string>();
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < chain.Count; i++)
            {
                var (route, p) = chain[i];
                var info = new RouteInfo(p.Id, p.Name, route.Model);
                var key = p.Id + "|" + route.Model;
                if (IsPaused(key, out var until))
                {
                    if (round == 0) failures.Add($"{p.Name} · {route.Model}: em pausa até {until.ToLocalTime():HH:mm:ss}");
                    continue;
                }
                for (var attempt = 0; ; attempt++)
                {
                    var st = Stat(p.Id);
                    Interlocked.Increment(ref st.Requests);
                    Interlocked.Increment(ref st.InFlight);
                    st.LastUsed = DateTime.UtcNow;
                    try
                    {
                        if (onStart != null) await onStart(info);
                        LlmResult r;
                        if (p.Kind == "cli")
                        {
                            var prompt = BuildCliPrompt(messages);
                            var cwd = cliWorkspace ?? Path.GetTempPath();
                            var text = await CliRunner.RunCliAsync(p, route.Model, prompt, cwd, onDelta, ct);
                            r = new LlmResult { Content = text, Finish = "stop" };
                        }
                        else
                        {
                            r = await LlmClient.ChatAsync(p, settings.GetKey(p.Id), route.Model, messages, tools, onDelta, ct);
                        }
                        cooldown.TryRemove(key, out _);
                        Interlocked.Increment(ref st.Successes);
                        st.LastError = null;
                        return (r, info);
                    }
                    catch (LlmException e)
                    {
                        if (p.Kind == "cli")
                        {
                            MarkCooldown(key, p.Id, e);
                            throw new UserError($"A execução em {p.Name} terminou sem confirmação. Confira os arquivos e testes antes de tentar de novo.");
                        }
                        if (e.Kind == LlmFailure.Transient && attempt == 0)
                        {
                            if (onNotice != null) await onNotice($"{p.Name} · {route.Model}: {e.Message}. Tentando de novo…");
                            await Task.Delay(2500, ct);
                            continue;
                        }
                        MarkCooldown(key, p.Id, e);
                        failures.Add($"{p.Name} · {route.Model}: {e.Message}");
                        var next = chain.Skip(i + 1).FirstOrDefault(c => !IsPaused(c.Provider.Id + "|" + c.Route.Model, out _));
                        var tail = next.Provider != null ? $" → continuando com {next.Provider.Name} · {next.Route.Model}"
                            : pinned ? " (conta travada — sem troca automática)" : "";
                        if (onNotice != null) await onNotice($"{p.Name} · {route.Model}: {e.Message}{tail}");
                        break;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref st.InFlight);
                    }
                }
            }

            if (pinned) break;
            var soonest = chain.Select(c => cooldown.TryGetValue(c.Provider.Id + "|" + c.Route.Model, out var u) ? u : DateTime.UtcNow).Min();
            var delay = soonest - DateTime.UtcNow;
            if (round == 0 && delay <= TimeSpan.FromSeconds(90))
            {
                if (onNotice != null) await onNotice($"Todos os modelos da fila estão em pausa. Aguardando {Math.Ceiling(Math.Max(delay.TotalSeconds, 1))} s para tentar de novo…");
                if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
                continue;
            }
            break;
        }
        throw new UserError((pinned ? "A conta travada falhou (sem troca silenciosa):\n• " : "Nenhum modelo da fila conseguiu responder:\n• ") + string.Join("\n• ", failures.Distinct().TakeLast(8)));
    }

    private static string BuildCliPrompt(JsonArray messages)
    {
        var sb = new StringBuilder();
        foreach (var node in messages)
        {
            var role = (string?)node?["role"];
            var content = (string?)node?["content"];
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (role == "system") sb.AppendLine("[Instruções do projeto]\n" + content.Trim() + "\n");
            else if (role == "user") sb.AppendLine("[Usuário]\n" + content.Trim() + "\n");
            else if (role == "assistant") sb.AppendLine("[Assistente anterior]\n" + Titles.Trunc(content.Trim(), 1500) + "\n");
        }
        return sb.ToString().Trim();
    }
}
