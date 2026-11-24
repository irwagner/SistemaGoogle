using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CentralIA;

/// <summary>
/// Descoberta automática do ambiente Windows (CLIs oficiais instaladas, Ollama, gateways locais e variáveis de ambiente)
/// e execução headless de CLIs de programação (Codex, Claude Code, Antigravity CLI, Gemini CLI) dentro do Pool.
/// </summary>
public static class CliRunner
{
    public sealed record CliSpec(string Id, string Name, string Exe, string Url, string Hint);

    public static readonly CliSpec[] Specs =
    [
        new("codex", "Codex CLI (OpenAI)", "codex", "cli://codex", "Usa seu login oficial do OpenAI Codex CLI — sem API key."),
        new("claude", "Claude Code CLI", "claude", "cli://claude", "Usa seu login oficial do Claude Code — sem API key."),
    ];

    public static string? FindExe(string name)
    {
        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            foreach (var ext in pathExt)
            {
                try
                {
                    var full = Path.Combine(d, name + ext.ToLowerInvariant());
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        return null;
    }

    public static async Task<object> DiscoverAsync(SettingsService settings, CancellationToken ct)
    {
        var existingUrls = settings.Current.Providers.Select(p => p.BaseUrl.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. CLIs oficiais instaladas
        var clis = Specs.Select(s =>
        {
            var exe = FindExe(s.Exe);
            return new
            {
                id = s.Id,
                name = s.Name,
                url = s.Url,
                hint = s.Hint,
                installed = exe != null,
                path = exe,
                connected = existingUrls.Contains(s.Url),
            };
        }).ToArray();

        // 2. Ollama local
        object ollama = new { installed = FindExe("ollama") != null, running = false, models = Array.Empty<string>(), url = "http://127.0.0.1:11434/v1", connected = existingUrls.Contains("http://127.0.0.1:11434/v1") };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(900));
            using var resp = await LlmClient.Http.GetAsync("http://127.0.0.1:11434/api/tags", cts.Token);
            if (resp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                var models = (node?["models"] as JsonArray ?? new()).Select(m => (string?)m?["name"]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
                ollama = new { installed = true, running = true, models, url = "http://127.0.0.1:11434/v1", connected = existingUrls.Contains("http://127.0.0.1:11434/v1") };
            }
        }
        catch { }

        // 3. Gateways locais compatíveis com OpenAI rodando no PC
        var probePorts = new (int Port, string Label)[]
        {
            (8317, "Gateway Local (8317)"),
            (8045, "Gateway Local (8045)"),
            (4000, "LiteLLM / Gateway (4000)"),
            (1234, "LM Studio (1234)"),
            (8080, "Gateway Local (8080)"),
        };
        var gwTasks = probePorts.Select(async g =>
        {
            var url = $"http://127.0.0.1:{g.Port}/v1";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(600));
                using var resp = await LlmClient.Http.GetAsync(url + "/models", cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var txt = await resp.Content.ReadAsStringAsync(cts.Token);
                var node = JsonNode.Parse(txt);
                var models = (node?["data"] as JsonArray ?? new()).Select(m => (string?)m?["id"]).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Take(30).ToArray();
                return (object)new { label = g.Label, url, models, connected = existingUrls.Contains(url) };
            }
            catch { return null; }
        });
        var gateways = (await Task.WhenAll(gwTasks)).Where(x => x != null).ToArray();

        // 4. Variáveis de ambiente com chaves de API já presentes no Windows
        var envDefs = new (string Var, string Name, string Kind, string BaseUrl, string DefaultModel)[]
        {
            ("GEMINI_API_KEY", "Gemini (Env)", "gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash"),
            ("GOOGLE_API_KEY", "Google AI (Env)", "gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash"),
            ("OPENROUTER_API_KEY", "OpenRouter (Env)", "openrouter", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4"),
            ("OPENAI_API_KEY", "OpenAI (Env)", "openai", "https://api.openai.com/v1", "gpt-4.1"),
            ("GROQ_API_KEY", "Groq (Env)", "groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile"),
            ("DEEPSEEK_API_KEY", "DeepSeek (Env)", "deepseek", "https://api.deepseek.com/v1", "deepseek-chat"),
        };
        var envs = envDefs
            .Where(e => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(e.Var)))
            .Select(e => new { envVar = e.Var, name = e.Name, kind = e.Kind, baseUrl = e.BaseUrl, defaultModel = e.DefaultModel })
            .ToArray();

        return new { clis, ollama, gateways, envs };
    }

    public static async Task<string> RunCliAsync(ProviderConfig p, string model, string prompt, string workspace,
        Func<string, Task>? onDelta, CancellationToken ct)
    {
        var cliId = p.BaseUrl.Replace("cli://", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        var spec = Specs.FirstOrDefault(s => s.Id == cliId)
            ?? throw new LlmException(LlmFailure.Bad, "CLI desconhecida: " + p.BaseUrl);
        var exe = FindExe(spec.Exe)
            ?? throw new LlmException(LlmFailure.Auth, $"{spec.Name} não está instalada no PATH do Windows.");

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var hasModel = !string.IsNullOrWhiteSpace(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase);
        switch (cliId)
        {
            case "codex":
                psi.ArgumentList.Add("exec");
                psi.ArgumentList.Add("--skip-git-repo-check");
                psi.ArgumentList.Add("--full-auto");
                if (hasModel) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model); }
                psi.ArgumentList.Add(prompt);
                break;
            case "claude":
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                psi.ArgumentList.Add("--permission-mode");
                psi.ArgumentList.Add("acceptEdits");
                if (hasModel) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
                break;
        }

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception e) { throw new LlmException(LlmFailure.Transient, $"Não consegui iniciar {spec.Name}: {e.Message}"); }
        proc.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            var buf = new char[512];
            int n;
            while ((n = await proc.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var chunk = new string(buf, 0, n);
                stdout.Append(chunk);
                if (onDelta != null) await onDelta(chunk);
            }
        }, ct);
        var readErr = Task.Run(async () =>
        {
            var txt = await proc.StandardError.ReadToEndAsync();
            stderr.Append(txt);
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(12));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            await Task.WhenAll(readOut, readErr);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!ct.IsCancellationRequested) throw new LlmException(LlmFailure.Transient, $"{spec.Name} excedeu o tempo limite.");
            throw;
        }

        var outText = stdout.ToString().Trim();
        var errText = stderr.ToString().Trim();
        if (proc.ExitCode != 0)
        {
            var combined = (errText + "\n" + outText).ToLowerInvariant();
            var detail = Titles.Trunc(errText.Length > 0 ? errText : outText, 240);
            if (combined.Contains("429") || combined.Contains("rate limit") || combined.Contains("quota") || combined.Contains("usage limit") || combined.Contains("limit reached") || combined.Contains("too many requests"))
                throw new LlmException(LlmFailure.Limit, $"limite de uso atingido no {spec.Name} — {detail}", TimeSpan.FromMinutes(2));
            if (combined.Contains("login") || combined.Contains("auth") || combined.Contains("not logged") || combined.Contains("unauthorized"))
                throw new LlmException(LlmFailure.Auth, $"{spec.Name} precisa de login no terminal — {detail}");
            throw new LlmException(LlmFailure.Transient, $"{spec.Name} retornou erro ({proc.ExitCode}) — {detail}");
        }

        return outText.Length > 0 ? outText : "(Concluído pela CLI sem texto adicional.)";
    }

    public static async Task<(bool Ok, string Reply, long Ms)> TestCliAsync(ProviderConfig p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cliId = p.BaseUrl.Replace("cli://", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant();
        var spec = Specs.FirstOrDefault(s => s.Id == cliId);
        if (spec == null) return (false, "CLI desconhecida.", sw.ElapsedMilliseconds);
        var exe = FindExe(spec.Exe);
        if (exe == null) return (false, $"{spec.Name} não encontrada no PATH.", sw.ElapsedMilliseconds);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--version");
        try
        {
            using var proc = Process.Start(psi)!;
            var o = await proc.StandardOutput.ReadToEndAsync(ct);
            var e = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var ver = (o.Trim().Length > 0 ? o.Trim() : e.Trim()).Split('\n')[0];
            return (proc.ExitCode == 0, $"{spec.Name} pronta ({Titles.Trunc(ver, 80)})", sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { return (false, ex.Message, sw.ElapsedMilliseconds); }
    }
}
