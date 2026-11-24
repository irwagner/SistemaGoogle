using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CentralIA;

/// <summary>
/// Estúdio Flow v2:
/// - Bíblia de personagens com retratos de referência (consistência visual)
/// - Roteiro via LlmRouter
/// - Múltiplas tomadas (Takes) por cena para comparar e aprovar
/// - Geração em paralelo distribuída entre contas Gemini pelo Pool (roundrobin/leastused/failover)
/// - Trilha sonora com ducking automático + transições xfade + legendas no FFmpeg
/// </summary>
public sealed class FlowService(Store store, SettingsService settings, LlmRouter router, EventHub hub, ILogger<FlowService> log)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(2) };

    // ---------- Roteiro ----------

    public Storyboard Create(string pid, string prompt, FlowOptions o)
    {
        if (store.GetProject(pid) == null) throw new UserError("Projeto não encontrado.");
        if (string.IsNullOrWhiteSpace(prompt)) throw new UserError("Descreva a ideia do vídeo.");
        var f = new Storyboard
        {
            Id = Store.NewId(), ProjectId = pid, Prompt = prompt.Trim(), Title = Titles.FromPrompt(prompt),
            Format = o.Format == "serie" ? "serie" : "video",
            EpisodeCount = o.Format == "serie" ? Math.Clamp(o.Episodes, 2, 12) : 1,
            ScenesPerEpisode = Math.Clamp(o.Scenes, 1, 12),
            SceneSeconds = NormalizeSeconds(o.Seconds),
            Aspect = o.Aspect == "9:16" ? "9:16" : "16:9",
            Style = (o.Style ?? "").Trim(),
            Status = "roteirizando", Created = DateTime.UtcNow, Updated = DateTime.UtcNow,
        };
        store.SaveNewFlow(f);
        _ = Task.Run(() => WriteScript(f, null));
        return f;
    }

    public void Rewrite(Storyboard f, string? feedback)
    {
        store.MutateFlow(f, x =>
        {
            if (x.Status == "roteirizando") throw new UserError("O roteiro ainda está sendo escrito.");
            if (HasPendingVideo(x)) throw new UserError("Concilie as gerações de vídeo antes de reescrever o roteiro.");
            x.Status = "roteirizando"; x.Error = null;
        });
        Publish(f);
        _ = Task.Run(() => WriteScript(f, feedback));
    }

    private static int NormalizeSeconds(int s) => s <= 4 ? 4 : s <= 6 ? 6 : 8;

    private async Task WriteScript(Storyboard f, string? feedback)
    {
        try
        {
            var sys = """
                Você é roteirista e diretor de fotografia de vídeos curtos e minisséries gerados por IA (Veo, Flow).
                Responda APENAS com um JSON válido, sem comentários, neste formato:
                {"title":"...","logline":"...","style":"...","characters":[{"name":"...","description":"aparência física detalhada e roupa, em inglês"}],
                 "episodes":[{"title":"...","synopsis":"...","scenes":[{"title":"...","visual":"...","narration":"...","seconds":8}]}]}
                Regras:
                - "visual": prompt cinematográfico EM INGLÊS para o gerador de vídeo: enquadramento, movimento de câmera, ação, cenário, luz, clima e estilo. Repita a descrição física dos personagens em toda cena para manter consistência. Uma ação clara por cena.
                - "narration": fala ou narração em português do Brasil, curta o bastante para caber na duração da cena (cerca de 2,5 palavras por segundo). Pode ser vazia.
                - "style": estilo visual único para todo o projeto (em inglês), ex.: "cinematic, 35mm film, warm teal and orange grade".
                - Títulos, logline e sinopses em português do Brasil.
                - Cada episódio deve ter começo, meio e gancho/fim. Na série, mantenha continuidade entre episódios.
                """;
            var user = new StringBuilder();
            user.AppendLine("Ideia: " + f.Prompt);
            user.AppendLine(f.Format == "serie"
                ? $"Formato: minissérie com {f.EpisodeCount} episódios de {f.ScenesPerEpisode} cenas cada."
                : $"Formato: vídeo único com {f.ScenesPerEpisode} cenas.");
            user.AppendLine($"Cada cena dura {f.SceneSeconds} segundos. Proporção {f.Aspect}.");
            if (f.Style.Length > 0) user.AppendLine("Estilo desejado: " + f.Style);
            if (!string.IsNullOrWhiteSpace(feedback) && f.EpisodeList.Count > 0)
            {
                user.AppendLine("\nRoteiro atual (JSON):");
                user.AppendLine(System.Text.Json.JsonSerializer.Serialize(new { f.Title, f.Logline, f.Style, f.Characters, episodes = f.EpisodeList.Select(e => new { e.Title, e.Synopsis, scenes = e.Scenes.Select(s => new { s.Title, s.Visual, s.Narration, s.Seconds }) }) }, Store.Json));
                user.AppendLine("\nReescreva aplicando este pedido: " + feedback);
            }
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = sys },
                new JsonObject { ["role"] = "user", ["content"] = user.ToString() },
            };
            var (result, route) = await router.ChatAsync("roteiro", messages, null, null, null,
                n => { hub.Publish("flow:" + f.Id, new { type = "notice", text = n }); return Task.CompletedTask; }, CancellationToken.None);
            var json = ExtractJson(result.Content) ?? throw new UserError("O modelo não devolveu um roteiro em JSON. Tente de novo.");
            store.MutateFlow(f, x =>
            {
                var oldChars = x.Characters.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
                var oldScenes = x.AllScenes().ToList();
                x.Title = Store.Clean((string?)json["title"], 120, x.Title);
                x.Logline = (string?)json["logline"] ?? "";
                var style = (string?)json["style"];
                if (!string.IsNullOrWhiteSpace(style)) x.Style = style;
                x.Characters = (json["characters"] as JsonArray ?? new()).OfType<JsonObject>()
                    .Select(c =>
                    {
                        var name = (string?)c["name"] ?? "";
                        return new Character
                        {
                            Name = name, Description = (string?)c["description"] ?? "",
                            Image = oldChars.TryGetValue(name, out var prev) ? prev.Image : null,
                        };
                    }).ToList();
                var episodes = new List<Episode>();
                var idx = 0;
                foreach (var e in (json["episodes"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var ep = new Episode { Id = Store.NewId(), Title = (string?)e["title"] ?? $"Episódio {episodes.Count + 1}", Synopsis = (string?)e["synopsis"] ?? "" };
                    foreach (var s in (e["scenes"] as JsonArray ?? new()).OfType<JsonObject>())
                    {
                        var keep = idx < oldScenes.Count ? oldScenes[idx] : null;
                        idx++;
                        int secs = x.SceneSeconds;
                        try { secs = NormalizeSeconds(s["seconds"]?.GetValue<int>() ?? x.SceneSeconds); } catch { }
                        var scene = new Scene
                        {
                            Id = Store.NewId(), Title = (string?)s["title"] ?? $"Cena {idx}", Visual = (string?)s["visual"] ?? "",
                            Narration = (string?)s["narration"] ?? "", Seconds = secs, Status = "rascunho",
                        };
                        if (keep != null && keep.Visual == scene.Visual)
                        {
                            scene.Image = keep.Image; scene.Video = keep.Video; scene.VideoOrigin = keep.VideoOrigin;
                            scene.Status = keep.Status; scene.Takes = keep.Takes; scene.ActiveTakeId = keep.ActiveTakeId;
                        }
                        if (keep != null && keep.Narration == scene.Narration) scene.Audio = keep.Audio;
                        ep.Scenes.Add(scene);
                    }
                    if (ep.Scenes.Count > 0) episodes.Add(ep);
                }
                if (episodes.Count == 0) throw new UserError("O roteiro veio sem cenas. Tente de novo.");
                x.EpisodeList = episodes;
                x.ScriptModel = $"{route.ProviderName} · {route.Model}";
                x.Status = "pronto";
                x.Error = null;
            });
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Roteiro falhou");
            store.MutateFlow(f, x => { x.Status = x.EpisodeList.Count > 0 ? "pronto" : "erro"; x.Error = e is UserError ? e.Message : "Falha ao escrever o roteiro: " + e.Message; });
        }
        Publish(f);
    }

    private static JsonObject? ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(text[start..(end + 1)]) as JsonObject; } catch { return null; }
    }

    public void Publish(Storyboard f) => hub.Publish("flow:" + f.Id, new { type = "flow", flow = JsonNode.Parse(store.SerializeFlow(f)) });

    // ---------- Edição, Takes e Trilha ----------

    public void EditScene(Storyboard f, string sceneId, JsonObject body)
    {
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            if (body["title"] is JsonValue t) s.Title = Store.Clean((string?)t, 120, s.Title);
            if (body["visual"] is JsonValue v) s.Visual = ((string?)v ?? "").Trim();
            if (body["narration"] is JsonValue n)
            {
                var nn = ((string?)n ?? "").Trim();
                if (nn != s.Narration) s.Audio = null;
                s.Narration = nn;
            }
            if (body["seconds"] is JsonValue sec) { try { s.Seconds = NormalizeSeconds(sec.GetValue<int>()); } catch { } }
        });
        Publish(f);
    }

    public void AddScene(Storyboard f, string episodeId)
    {
        store.MutateFlow(f, x =>
        {
            var ep = x.EpisodeList.FirstOrDefault(e => e.Id == episodeId) ?? throw new UserError("Episódio não encontrado.");
            ep.Scenes.Add(new Scene { Id = Store.NewId(), Title = $"Cena {ep.Scenes.Count + 1}", Seconds = x.SceneSeconds });
        });
        Publish(f);
    }

    public void RemoveScene(Storyboard f, string sceneId)
    {
        store.MutateFlow(f, x =>
        {
            var scene = Find(x, sceneId);
            if (scene.Busy != null || scene.VideoIntent != null || scene.Operation != null)
                throw new UserError("A cena tem uma operação pendente.");
            foreach (var ep in x.EpisodeList) ep.Scenes.Remove(scene);
        });
        Publish(f);
    }

    public void MoveScene(Storyboard f, string sceneId, int delta)
    {
        store.MutateFlow(f, x =>
        {
            foreach (var ep in x.EpisodeList)
            {
                var i = ep.Scenes.FindIndex(s => s.Id == sceneId);
                if (i < 0) continue;
                var j = Math.Clamp(i + delta, 0, ep.Scenes.Count - 1);
                if (i == j) return;
                (ep.Scenes[i], ep.Scenes[j]) = (ep.Scenes[j], ep.Scenes[i]);
            }
        });
        Publish(f);
    }

    private static void RecordTake(Scene s, string kind, string rel, string origin)
    {
        var take = new TakeItem { Id = Store.NewId(), Kind = kind, Path = rel, Origin = origin, Time = DateTime.UtcNow };
        s.Takes.Add(take);
        if (kind == "video") { s.Video = rel; s.VideoOrigin = origin; s.ActiveTakeId = take.Id; s.Status = "video"; }
        else { s.Image = rel; if (s.Video == null) { s.ActiveTakeId = take.Id; s.Status = "quadro"; } }
    }

    public void SelectTake(Storyboard f, string sceneId, string takeId)
    {
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            var tk = s.Takes.FirstOrDefault(t => t.Id == takeId) ?? throw new UserError("Tomada não encontrada.");
            if (tk.Kind == "video") { s.Video = tk.Path; s.VideoOrigin = tk.Origin; s.Status = "video"; }
            else { s.Image = tk.Path; if (s.Video == null) s.Status = "quadro"; }
            s.ActiveTakeId = tk.Id;
        });
        Publish(f);
    }

    public void DeleteTake(Storyboard f, string sceneId, string takeId)
    {
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            var tk = s.Takes.FirstOrDefault(t => t.Id == takeId);
            if (tk == null) return;
            s.Takes.Remove(tk);
            if (s.Video == tk.Path)
            {
                var alt = s.Takes.LastOrDefault(t => t.Kind == "video");
                s.Video = alt?.Path; s.VideoOrigin = alt?.Origin;
                s.Status = s.Video != null ? "video" : s.Image != null ? "quadro" : "rascunho";
                if (s.ActiveTakeId == takeId) s.ActiveTakeId = alt?.Id;
            }
            else if (s.Image == tk.Path)
            {
                var alt = s.Takes.LastOrDefault(t => t.Kind == "imagem");
                s.Image = alt?.Path;
                if (s.Video == null) s.Status = s.Image != null ? "quadro" : "rascunho";
            }
        });
        Publish(f);
    }

    public async Task UploadMusic(Storyboard f, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".mp3" or ".wav" or ".m4a" or ".ogg" or ".flac"))
            throw new UserError("Trilha sonora deve ser mp3, wav, m4a, ogg ou flac.");
        var rel = $"media/trilha-{DateTime.Now:HHmmss}{ext}";
        var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full)) await file.CopyToAsync(fs);
        store.MutateFlow(f, x => x.Music = rel);
        Publish(f);
    }

    public void ClearMusic(Storyboard f)
    {
        store.MutateFlow(f, x => x.Music = null);
        Publish(f);
    }

    private static Scene Find(Storyboard f, string sceneId) =>
        f.AllScenes().FirstOrDefault(s => s.Id == sceneId) ?? throw new UserError("Cena não encontrada.");

    // ---------- Google Pool (estratégia + telemetria + rotação automática) ----------

    public int GoogleAccountCount() =>
        settings.Current.Providers.Count(x => x.Kind == "gemini" && x.Enabled && settings.HasKey(x.Id));

    private List<(ProviderConfig P, string Key, string Base)> GoogleAccounts()
    {
        var s = settings.Current;
        var list = s.Providers
            .Where(x => x.Kind == "gemini" && x.Enabled && settings.HasKey(x.Id))
            .OrderByDescending(x => x.Id == s.Media.ProviderId)
            .Select(p =>
            {
                var key = settings.GetKey(p.Id)!;
                var b = p.BaseUrl.TrimEnd('/');
                if (b.EndsWith("/openai", StringComparison.Ordinal)) b = b[..^7];
                if (!Uri.TryCreate(b, UriKind.Absolute, out var uri) || !GoogleAddress(uri) ||
                    uri.AbsolutePath.TrimEnd('/') != "/v1beta") b = "https://generativelanguage.googleapis.com/v1beta";
                return (p, key, b);
            }).ToList();
        if (list.Count == 0)
            throw new UserError("Para gerar imagem, vídeo ou voz, conecte ao menos uma chave da API do Gemini (Google AI Studio) em “Contas e modelos”.");
        return router.OrderByStrategy("midia", list, x => x.p.Id, x => x.p.Id + "|media");
    }

    private (ProviderConfig P, string Key, string Base) Google(string? providerId = null)
    {
        var list = GoogleAccounts();
        if (!string.IsNullOrEmpty(providerId))
            return list.FirstOrDefault(x => x.P.Id == providerId) is { P: not null } found ? found :
                throw new UserError("A conexão original está indisponível. Restaure o mesmo perfil e a mesma credencial.");
        return list[0];
    }

    private static bool GoogleAddress(Uri uri) => uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        ((uri.Scheme == "https" && uri.IsDefaultPort && uri.Host == "generativelanguage.googleapis.com") ||
         (Continuity.LabEndpoint is { } lab && uri.Scheme == lab.Scheme && uri.Host == lab.Host && uri.Port == lab.Port));

    private async Task<(JsonNode Res, ProviderConfig Used, string Key, string Base)> PostGoogleWithRotation(
        string relativeEndpoint, JsonObject body, Storyboard f, CancellationToken ct)
    {
        var accounts = GoogleAccounts();
        var errors = new List<string>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var (p, key, b) = accounts[i];
            var st = router.Stat(p.Id);
            Interlocked.Increment(ref st.Requests);
            Interlocked.Increment(ref st.InFlight);
            st.LastUsed = DateTime.UtcNow;
            try
            {
                var res = await PostJson($"{b}/{relativeEndpoint.TrimStart('/')}", key, body, ct);
                Interlocked.Increment(ref st.Successes);
                st.LastError = null;
                return (res, p, key, b);
            }
            catch (LlmException e)
            {
                router.MarkCooldown(p.Id + "|media", p.Id, e);
                errors.Add($"{p.Name}: {e.Message}");
                if (e.Kind == LlmFailure.Limit && i < accounts.Count - 1)
                {
                    hub.Publish("flow:" + f.Id, new { type = "notice", text = $"{p.Name}: {e.Message} → tentando com {accounts[i + 1].P.Name}" });
                    continue;
                }
                throw new UserError("Google recusou o pedido:\n• " + string.Join("\n• ", errors));
            }
            finally { Interlocked.Decrement(ref st.InFlight); }
        }
        throw new UserError("Nenhuma conta Google disponível conseguiu atender:\n• " + string.Join("\n• ", errors));
    }

    private static async Task<JsonNode> PostJson(string url, string key, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw LlmClient.Classify((int)resp.StatusCode, text, resp.Headers.RetryAfter);
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    // ---------- Personagens (Bíblia visual) ----------

    public void GenerateCharacterImage(Storyboard f, string charName)
    {
        GoogleAccounts();
        lock (f)
        {
            var c = f.Characters.FirstOrDefault(x => x.Name.Equals(charName, StringComparison.OrdinalIgnoreCase)) ?? throw new UserError("Personagem não encontrado.");
            if (c.Busy) throw new UserError("Retrato já está sendo gerado.");
            c.Busy = true;
        }
        Publish(f);
        _ = Task.Run(async () =>
        {
            try
            {
                string desc, style;
                lock (f)
                {
                    var c = f.Characters.First(x => x.Name.Equals(charName, StringComparison.OrdinalIgnoreCase));
                    desc = $"{c.Name}: {c.Description}"; style = f.Style;
                }
                var prompt = $"Character reference sheet portrait, clean medium close-up, front view, neutral studio background, sharp facial details. {desc}. Visual style: {style}. No text, no watermark.";
                var body = new JsonObject
                {
                    ["contents"] = new JsonArray { new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } } } },
                    ["generationConfig"] = new JsonObject
                    {
                        ["responseModalities"] = new JsonArray("TEXT", "IMAGE"),
                        ["imageConfig"] = new JsonObject { ["aspectRatio"] = "1:1" },
                    },
                };
                var (res, _, _, _) = await PostGoogleWithRotation($"models/{settings.Current.Media.ImageModel}:generateContent", body, f, CancellationToken.None);
                var data = (res["candidates"]?[0]?["content"]?["parts"] as JsonArray)?
                    .Select(x => x?["inlineData"]?["data"] ?? x?["inline_data"]?["data"]).FirstOrDefault(x => x != null)
                    ?? throw new UserError("O modelo não devolveu imagem para este personagem.");
                var safe = new string(charName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
                var rel = $"media/char-{safe}-{DateTime.Now:HHmmss}.png";
                var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, Convert.FromBase64String((string)data!));
                store.MutateFlow(f, x =>
                {
                    var c = x.Characters.FirstOrDefault(q => q.Name.Equals(charName, StringComparison.OrdinalIgnoreCase));
                    if (c != null) { c.Image = rel; c.Busy = false; }
                });
            }
            catch (Exception e)
            {
                store.MutateFlow(f, x =>
                {
                    var c = x.Characters.FirstOrDefault(q => q.Name.Equals(charName, StringComparison.OrdinalIgnoreCase));
                    if (c != null) c.Busy = false;
                    x.Error = Friendly(e);
                });
            }
            Publish(f);
        });
    }

    public async Task ImportCharacterImage(Storyboard f, string charName, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) throw new UserError("Use PNG, JPG ou WEBP.");
        var safe = new string(charName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        var rel = $"media/char-{safe}-{DateTime.Now:HHmmss}{ext}";
        var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full)) await file.CopyToAsync(fs);
        store.MutateFlow(f, x =>
        {
            var c = x.Characters.FirstOrDefault(q => q.Name.Equals(charName, StringComparison.OrdinalIgnoreCase)) ?? throw new UserError("Personagem não encontrado.");
            c.Image = rel;
        });
        Publish(f);
    }

    // ---------- Cenas: Imagem, Vídeo Veo, Voz, Importação ----------

    private bool TryBegin(Storyboard f, string sceneId, string what)
    {
        var ok = false;
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            if (s.Busy != null) return;
            s.Busy = what; s.Error = null; ok = true;
        });
        if (ok) Publish(f);
        return ok;
    }

    private void End(Storyboard f, string sceneId, Action<Scene>? change, string? error = null)
    {
        store.MutateFlow(f, x =>
        {
            var s = x.AllScenes().FirstOrDefault(c => c.Id == sceneId);
            if (s == null) return;
            change?.Invoke(s);
            s.Busy = null; s.Error = error;
        });
        Publish(f);
    }

    public Task GenerateImageAsync(Storyboard f, string sceneId)
    {
        GoogleAccounts();
        if (!TryBegin(f, sceneId, "imagem")) throw new UserError("Essa cena já está ocupada.");
        return Task.Run(async () =>
        {
            try
            {
                Scene scene; string? prevFrame; string style; string chars; string aspect;
                List<(string Name, string Path)> charRefs;
                var dir = store.FlowDir(f.ProjectId, f.Id);
                lock (f)
                {
                    scene = Find(f, sceneId);
                    prevFrame = f.AllScenes().Where(s => s.Id != sceneId && s.Image != null).Select(s => s.Image).FirstOrDefault();
                    style = f.Style; aspect = f.Aspect;
                    chars = string.Join("; ", f.Characters.Select(c => $"{c.Name}: {c.Description}"));
                    charRefs = f.Characters.Where(c => c.Image != null && File.Exists(Path.Combine(dir, c.Image!)))
                        .Take(3).Select(c => (c.Name, Path.Combine(dir, c.Image!))).ToList();
                }
                var prompt = $"Cinematic film still, storyboard keyframe. {scene.Visual}\nStyle: {style}.\nCharacters: {chars}\nNo text, no captions, no watermark.";
                var parts = new JsonArray { new JsonObject { ["text"] = prompt } };
                foreach (var (name, imgPath) in charRefs)
                {
                    parts.Add(new JsonObject { ["text"] = $"Character reference portrait for {name} (preserve exact face, hair and outfit):" });
                    parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(imgPath)) } });
                }
                if (charRefs.Count == 0 && prevFrame != null && File.Exists(Path.Combine(dir, prevFrame)))
                {
                    parts.Add(new JsonObject { ["text"] = "Reference frame for character design and visual style consistency:" });
                    parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(dir, prevFrame))) } });
                }
                var body = new JsonObject
                {
                    ["contents"] = new JsonArray { new JsonObject { ["parts"] = parts } },
                    ["generationConfig"] = new JsonObject
                    {
                        ["responseModalities"] = new JsonArray("TEXT", "IMAGE"),
                        ["imageConfig"] = new JsonObject { ["aspectRatio"] = aspect },
                    },
                };
                var (res, used, _, _) = await PostGoogleWithRotation($"models/{settings.Current.Media.ImageModel}:generateContent", body, f, CancellationToken.None);
                var data = (res["candidates"]?[0]?["content"]?["parts"] as JsonArray)?
                    .Select(x => x?["inlineData"]?["data"] ?? x?["inline_data"]?["data"]).FirstOrDefault(x => x != null);
                if (data == null) throw new UserError("O modelo de imagem não devolveu imagem (pode ter sido bloqueada pelo filtro de segurança).");
                var rel = $"media/{sceneId}-img-{DateTime.Now:HHmmssfff}.png";
                var full = Store.Inside(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, Convert.FromBase64String((string)data!));
                End(f, sceneId, s => RecordTake(s, "imagem", rel, $"Quadro · {used.Name}"));
            }
            catch (Exception e) { End(f, sceneId, null, Friendly(e)); }
        });
    }

    public decimal VideoEstimate(int seconds) => seconds * settings.Current.Media.VideoCostPerSecond;

    public bool HasPendingVideo(Storyboard f)
    {
        lock (f) return f.AllScenes().Any(s => s.Busy == "video" || s.VideoIntent != null || s.Operation != null);
    }

    public Task GenerateVideoAsync(Storyboard f, string sceneId, string? providerId = null)
    {
        var (provider, key, endpoint) = Google(providerId);
        var m = settings.Current.Media;
        if (!Regex.IsMatch(m.VideoModel, @"\A[a-zA-Z0-9._-]{1,120}\z")) throw new UserError("Modelo de vídeo inválido.");
        lock (f)
        {
            var scene = Find(f, sceneId);
            if (f.Status == "roteirizando") throw new UserError("Aguarde o roteiro.");
            if (scene.VideoIntent != null || scene.Operation != null) throw new UserError("Concilie a geração original antes de pedir outro vídeo.");
            if (!TryBegin(f, sceneId, "video")) throw new UserError("Essa cena já está ocupada.");
        }
        return Task.Run(async () =>
        {
            try
            {
                Scene scene; string style; string chars; string aspect;
                lock (f) { scene = Find(f, sceneId); style = f.Style; aspect = f.Aspect; chars = string.Join("; ", f.Characters.Select(c => $"{c.Name}: {c.Description}")); }
                var prompt = $"{scene.Visual}\nStyle: {style}.";
                if (chars.Length > 0) prompt += $"\nCharacters: {chars}";
                if (!string.IsNullOrWhiteSpace(scene.Narration)) prompt += $"\nDialogue/narration (Brazilian Portuguese): \"{scene.Narration}\"";
                var instance = new JsonObject { ["prompt"] = prompt };
                var dir = store.FlowDir(f.ProjectId, f.Id);
                if (m.VideoUsesFrame && scene.Image != null && File.Exists(Path.Combine(dir, scene.Image)))
                    instance["image"] = new JsonObject { ["bytesBase64Encoded"] = Convert.ToBase64String(await File.ReadAllBytesAsync(Path.Combine(dir, scene.Image))), ["mimeType"] = "image/png" };
                var parameters = new JsonObject { ["aspectRatio"] = aspect, ["durationSeconds"] = scene.Seconds };
                if (!string.IsNullOrWhiteSpace(m.Resolution)) parameters["resolution"] = m.Resolution;
                var body = new JsonObject { ["instances"] = new JsonArray(instance), ["parameters"] = parameters };
                var estimate = VideoEstimate(scene.Seconds);
                var intent = new VideoIntent(provider.Id, endpoint, Digest(key), m.VideoModel, Digest(body.ToJsonString()), estimate);
                store.MutateFlow(f, x => Find(x, sceneId).VideoIntent = intent);
                Publish(f);
                var res = await PostJson($"{endpoint}/models/{m.VideoModel}:predictLongRunning", key, body, CancellationToken.None);
                var op = (string?)res["name"] ?? throw new UserError("O Google não devolveu o identificador da geração.");
                if (!ValidOperation(op)) throw new UserError("O Google devolveu um identificador de operação inesperado.");
                store.MutateFlow(f, x => { Find(x, sceneId).Operation = provider.Id + "|" + op; x.EstimatedSpent += estimate; });
                Publish(f);
                await PollVideo(f, sceneId, provider.Id + "|" + op, intent);
            }
            catch (Exception e)
            {
                End(f, sceneId, null, e is UserError ? e.Message :
                    "O envio ou a consulta terminou sem confirmação. Confira a operação na conexão original.");
            }
        });
    }

    public void ResumeVideo(Storyboard f, string sceneId)
    {
        VideoIntent intent;
        string operation;
        lock (f)
        {
            var s = Find(f, sceneId);
            intent = s.VideoIntent ?? throw new UserError("A intenção original não identifica a credencial. Confira no provedor.");
            operation = s.Operation ?? throw new UserError("Sem recibo da operação. Confira o envio na conexão original.");
            if (!TryBegin(f, sceneId, "video")) throw new UserError("A cena está ocupada.");
        }
        _ = Task.Run(async () =>
        {
            try { await PollVideo(f, sceneId, operation, intent); }
            catch (Exception e) { End(f, sceneId, null, e is UserError ? e.Message : "A consulta perdeu a confirmação. Consulte novamente pela conexão original."); }
        });
    }

    public void ResumePending()
    {
        foreach (var p in store.Projects())
            foreach (var f in store.Flows(p.Id))
            {
                if (f.Status == "roteirizando") store.MutateFlow(f, x => { x.Status = x.EpisodeList.Count > 0 ? "pronto" : "erro"; x.Error = "A escrita do roteiro foi interrompida. Clique em reescrever."; });
                if (f.Rendering != null) store.MutateFlow(f, x => x.Rendering = null);
                foreach (var c in f.Characters.Where(c => c.Busy)) store.MutateFlow(f, _ => c.Busy = false);
                foreach (var s in f.AllScenes().Where(s => s.Busy != null).ToList())
                {
                    if (s.Busy == "video")
                    {
                        End(f, s.Id, null, "A Central foi fechada durante a geração. Confira o recibo original.");
                        if (s.Operation != null && s.VideoIntent != null) ResumeVideo(f, s.Id);
                    }
                    else End(f, s.Id, null, "Interrompido quando a Central foi fechada. Tente de novo.");
                }
            }
    }

    private static string Digest(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool ValidOperation(string path) =>
        Regex.IsMatch(path, @"\A(?:models/[a-zA-Z0-9._-]+/)?operations/[a-zA-Z0-9_-]+[a-zA-Z0-9._-]*\z") &&
        !path.Split('/').Any(part => part is "." or "..");

    private async Task PollVideo(Storyboard f, string sceneId, string opRaw, VideoIntent intent)
    {
        string? provId = null;
        var op = opRaw;
        var sep = opRaw.IndexOf('|');
        if (sep > 0) { provId = opRaw[..sep]; op = opRaw[(sep + 1)..]; }
        if (provId != intent.Provider || !ValidOperation(op)) throw new UserError("O recibo original da geração é inválido.");
        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            var (p, key, b) = Google(intent.Provider);
            if (b != intent.Endpoint || Digest(key) != intent.Credential)
                throw new UserError("A credencial ou o endereço original mudou. Restaure a conexão que iniciou a geração.");
            await Task.Delay(Continuity.Lab ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{b}/{op}");
            req.Headers.TryAddWithoutValidation("x-goog-api-key", key);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                throw new UserError($"A conexão original retornou HTTP {(int)resp.StatusCode} ao consultar a geração. O recibo foi preservado.");
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            if (node?["done"]?.GetValue<bool>() != true)
                continue;
            if (node["error"] is JsonObject)
            {
                End(f, sceneId, s => { s.Operation = null; s.VideoIntent = null; }, "O Google confirmou que a geração terminou com erro.");
                return;
            }
            var resp2 = node["response"];
            var sample = resp2?["generateVideoResponse"]?["generatedSamples"]?[0] ?? resp2?["generatedVideos"]?[0] ?? resp2?["videos"]?[0];
            var uri = (string?)sample?["video"]?["uri"] ?? (string?)sample?["uri"];
            var b64 = (string?)sample?["video"]?["bytesBase64Encoded"] ?? (string?)sample?["bytesBase64Encoded"];
            if (uri == null && b64 == null)
            {
                if (resp2?["generateVideoResponse"]?["raiMediaFilteredReasons"] is JsonArray { Count: > 0 })
                {
                    End(f, sceneId, s => { s.Operation = null; s.VideoIntent = null; }, "O Google confirmou que o vídeo foi filtrado.");
                    return;
                }
                throw new UserError("A operação terminou sem arquivo reconhecível. Confira o recibo original.");
            }
            byte[] bytes;
            if (b64 != null) bytes = Convert.FromBase64String(b64);
            else
            {
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var mediaUri) || !GoogleAddress(mediaUri))
                    throw new UserError("O vídeo tem um endereço de download inesperado. Confira a operação original.");
                using var dl = new HttpRequestMessage(HttpMethod.Get, mediaUri);
                dl.Headers.TryAddWithoutValidation("x-goog-api-key", key);
                using var dresp = await Http.SendAsync(dl);
                if (!dresp.IsSuccessStatusCode) throw new UserError($"Não consegui baixar o vídeo gerado ({(int)dresp.StatusCode}).");
                bytes = await dresp.Content.ReadAsByteArrayAsync();
            }
            var rel = $"media/{sceneId}-veo-{Digest(opRaw)[..16]}.mp4";
            var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllBytesAsync(full, bytes);
            End(f, sceneId, s =>
            {
                RecordTake(s, "video", rel, $"Veo · {p.Name}");
                s.Operation = null; s.VideoIntent = null;
            });
            return;
        }
        throw new UserError("A geração ultrapassou 20 minutos. Consulte novamente pela conexão original.");
    }

    public Task GenerateVoiceAsync(Storyboard f, string sceneId)
    {
        GoogleAccounts();
        lock (f) if (string.IsNullOrWhiteSpace(Find(f, sceneId).Narration)) throw new UserError("Essa cena não tem narração escrita.");
        if (!TryBegin(f, sceneId, "voz")) throw new UserError("Essa cena já está ocupada.");
        return Task.Run(async () =>
        {
            try
            {
                string text; lock (f) text = Find(f, sceneId).Narration;
                var m = settings.Current.Media;
                var body = new JsonObject
                {
                    ["contents"] = new JsonArray { new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = "Narre em português do Brasil, com emoção natural: " + text } } } },
                    ["generationConfig"] = new JsonObject
                    {
                        ["responseModalities"] = new JsonArray("AUDIO"),
                        ["speechConfig"] = new JsonObject { ["voiceConfig"] = new JsonObject { ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = m.Voice } } },
                    },
                };
                var (res, _, _, _) = await PostGoogleWithRotation($"models/{m.TtsModel}:generateContent", body, f, CancellationToken.None);
                var inline = (res["candidates"]?[0]?["content"]?["parts"] as JsonArray)?.Select(x => x?["inlineData"]).FirstOrDefault(x => x != null)
                    ?? throw new UserError("O modelo de voz não devolveu áudio.");
                var pcm = Convert.FromBase64String((string)inline["data"]!);
                var mime = (string?)inline["mimeType"] ?? "";
                var rate = 24000;
                var rm = System.Text.RegularExpressions.Regex.Match(mime, @"rate=(\d+)");
                if (rm.Success) rate = int.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture);
                var rel = $"media/{sceneId}-voz-{DateTime.Now:HHmmssfff}.wav";
                var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllBytesAsync(full, mime.Contains("wav") ? pcm : Wav(pcm, rate));
                End(f, sceneId, s => s.Audio = rel);
            }
            catch (Exception e) { End(f, sceneId, null, Friendly(e)); }
        });
    }

    public int RunBatchParallel(Storyboard f, string what)
    {
        GoogleAccounts();
        List<string> ids;
        lock (f) ids = f.AllScenes().Where(s => s.Busy == null && what switch
        {
            "image" => s.Image == null && s.Video == null,
            "video" => s.Video == null && s.VideoIntent == null && s.Operation == null,
            "voice" => s.Audio == null && s.Narration.Length > 0,
            _ => false,
        }).Select(s => s.Id).ToList();
        if (ids.Count == 0) throw new UserError("Não há cenas pendentes para isso.");

        var concurrency = Math.Clamp(GoogleAccountCount(), 1, 4);
        _ = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(concurrency, concurrency);
            var jobs = ids.Select(async (id, idx) =>
            {
                if (idx > 0) await Task.Delay(idx * 600);
                await sem.WaitAsync();
                try
                {
                    if (what == "image") await GenerateImageAsync(f, id);
                    else if (what == "voice") await GenerateVoiceAsync(f, id);
                    else await GenerateVideoAsync(f, id);
                }
                catch { }
                finally { sem.Release(); }
            });
            await Task.WhenAll(jobs);
        });
        return ids.Count;
    }

    private static byte[] Wav(byte[] pcm, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        w.Flush();
        return ms.ToArray();
    }

    public async Task ImportMedia(Storyboard f, string sceneId, IFormFile file)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var kind = ext switch
        {
            ".mp4" or ".mov" or ".webm" or ".mkv" => "video",
            ".png" or ".jpg" or ".jpeg" or ".webp" => "imagem",
            ".mp3" or ".wav" or ".m4a" or ".ogg" => "audio",
            _ => throw new UserError("Formato não suportado. Use vídeo (mp4, mov, webm), imagem (png, jpg) ou áudio (mp3, wav)."),
        };
        lock (f) Find(f, sceneId);
        var rel = $"media/{sceneId}-imp-{DateTime.Now:HHmmssfff}{ext}";
        var full = Store.Inside(store.FlowDir(f.ProjectId, f.Id), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using (var fs = File.Create(full)) await file.CopyToAsync(fs);
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            if (kind == "video") RecordTake(s, "video", rel, "Importado · " + Path.GetFileName(file.FileName));
            else if (kind == "imagem") RecordTake(s, "imagem", rel, "Importado · " + Path.GetFileName(file.FileName));
            else s.Audio = rel;
            s.Error = null;
        });
        Publish(f);
    }

    public void ClearMedia(Storyboard f, string sceneId, string kind)
    {
        store.MutateFlow(f, x =>
        {
            var s = Find(x, sceneId);
            if (kind == "video") { s.Video = null; s.VideoOrigin = null; s.Status = s.Image != null ? "quadro" : "rascunho"; }
            else if (kind == "imagem") { s.Image = null; if (s.Video == null) s.Status = "rascunho"; }
            else if (kind == "audio") s.Audio = null;
            s.Error = null;
        });
        Publish(f);
    }

    private static string Friendly(Exception e) => e is UserError ? e.Message : "Falhou: " + e.Message;

    // ---------- Montagem Cinematográfica (FFmpeg: xfade + trilha com ducking + legendas) ----------

    public void Assemble(Storyboard f, bool subtitles)
    {
        if (Tool("ffmpeg") == null) throw new UserError("FFmpeg não encontrado. Instale o FFmpeg e coloque no PATH (ou defina CENTRAL_FFMPEG).");
        var started = false;
        store.MutateFlow(f, x => { if (x.Rendering == null) { x.Rendering = "preparando"; started = true; } });
        if (!started) throw new UserError("A montagem já está em andamento.");
        Publish(f);
        _ = Task.Run(async () =>
        {
            try { await Render(f, subtitles); }
            catch (Exception e) { log.LogWarning(e, "Montagem falhou"); store.MutateFlow(f, x => x.Error = "Montagem falhou: " + e.Message); }
            finally { store.MutateFlow(f, x => x.Rendering = null); Publish(f); }
        });
    }

    private void Progress(Storyboard f, string text) { store.MutateFlow(f, x => x.Rendering = text); Publish(f); }

    private async Task Render(Storyboard f, bool subtitles)
    {
        var dir = store.FlowDir(f.ProjectId, f.Id);
        var work = Path.Combine(dir, "render");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        var font = new[] { @"C:\Windows\Fonts\segoeuib.ttf", @"C:\Windows\Fonts\arialbd.ttf", @"C:\Windows\Fonts\arial.ttf" }.FirstOrDefault(File.Exists);
        if (font != null) File.Copy(font, Path.Combine(work, "fonte.ttf"), true);
        var (w, h) = f.Aspect == "9:16" ? (720, 1280) : (1280, 720);
        List<Episode> episodes;
        double fade; string? musicFile; double musicVol;
        lock (f)
        {
            episodes = System.Text.Json.JsonSerializer.Deserialize<List<Episode>>(System.Text.Json.JsonSerializer.Serialize(f.EpisodeList))!;
            fade = Math.Clamp(f.TransitionSeconds, 0, 1.5);
            musicFile = f.Music != null && File.Exists(Path.Combine(dir, f.Music)) ? Path.Combine(dir, f.Music) : null;
            musicVol = Math.Clamp(f.MusicVolume, 0.05, 1.0);
        }
        var total = episodes.Sum(e => e.Scenes.Count);
        var n = 0;
        var episodeFiles = new List<(string File, string Label, double Seconds, List<(Scene, double)> Timeline)>();

        for (var ei = 0; ei < episodes.Count; ei++)
        {
            var ep = episodes[ei];
            var clips = new List<(string File, double Dur)>();
            var timeline = new List<(Scene, double)>();
            foreach (var s in ep.Scenes)
            {
                n++;
                Progress(f, $"preparando cena {n} de {total}");
                var clip = $"c{n:000}.mp4";
                var dur = await RenderScene(dir, work, s, clip, w, h);
                clips.Add((clip, dur));
                timeline.Add((s, dur));
            }

            Progress(f, episodes.Count > 1 ? $"juntando episódio {ei + 1}" : "juntando cenas");
            var outName = $"ep{ei + 1}.mp4";
            var useFade = fade > 0.05 && clips.Count > 1 && clips.All(c => c.Dur > fade * 2.2);
            double epDur;
            if (useFade)
            {
                await ConcatWithFade(work, clips, fade, outName);
                epDur = clips.Sum(c => c.Dur) - fade * (clips.Count - 1);
            }
            else
            {
                var list = $"ep{ei + 1}.txt";
                await File.WriteAllLinesAsync(Path.Combine(work, list), clips.Select(c => $"file '{c.File}'"));
                await Ffmpeg(work, ["-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", outName]);
                epDur = clips.Sum(c => c.Dur);
            }

            if (musicFile != null)
            {
                Progress(f, "mixando trilha sonora com ducking");
                var withMusic = $"ep{ei + 1}-mus.mp4";
                var fadeOutStart = Math.Max(0, epDur - 1.5).ToString("0.###", CultureInfo.InvariantCulture);
                var volStr = musicVol.ToString("0.###", CultureInfo.InvariantCulture);
                var durStr = epDur.ToString("0.###", CultureInfo.InvariantCulture);
                var fc = $"[0:a]asplit=2[vo][sc];[1:a]aresample=48000,aformat=channel_layouts=stereo,volume={volStr},afade=t=in:st=0:d=0.6,afade=t=out:st={fadeOutStart}:d=1.4[bg];[bg][sc]sidechaincompress=threshold=0.03:ratio=6:attack=25:release=350[duck];[vo][duck]amix=inputs=2:duration=first:normalize=0,atrim=0:{durStr}[aout]";
                await Ffmpeg(work, ["-i", outName, "-stream_loop", "-1", "-i", musicFile, "-filter_complex", fc,
                    "-map", "0:v", "-map", "[aout]", "-t", durStr, "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", withMusic]);
                outName = withMusic;
            }

            episodeFiles.Add((outName, episodes.Count > 1 ? $"Episódio {ei + 1} · {ep.Title}" : f.Title, epDur, timeline));
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var exports = new List<ExportItem>();
        string Safe(string s) => new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        for (var i = 0; i < episodeFiles.Count; i++)
        {
            var e = episodeFiles[i];
            var name = episodeFiles.Count > 1 ? $"{Safe(f.Title)}-ep{i + 1}-{stamp}.mp4" : $"{Safe(f.Title)}-{stamp}.mp4";
            var srtOverlap = fade > 0.05 && e.Timeline.Count > 1 && e.Timeline.All(t => t.Item2 > fade * 2.2) ? fade : 0;
            var srtContent = Srt(e.Timeline, srtOverlap);
            if (subtitles && font != null && e.Timeline.Any(t => t.Item1.Narration.Length > 0))
            {
                Progress(f, "aplicando legendas");
                await File.WriteAllTextAsync(Path.Combine(work, $"ep{i + 1}.srt"), srtContent, new UTF8Encoding(false));
                var legName = $"ep{i + 1}-leg.mp4";
                await Ffmpeg(work, ["-i", e.File, "-vf", $"subtitles=ep{i + 1}.srt:fontsdir=.:force_style='FontName=Segoe UI,FontSize=15,Outline=1,Shadow=0,MarginV=28'",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "copy", "-movflags", "+faststart", legName]);
                e = (legName, e.Label, e.Seconds, e.Timeline);
            }
            File.Copy(Path.Combine(work, e.File), Path.Combine(dir, "exports", name), true);
            await File.WriteAllTextAsync(Path.Combine(dir, "exports", Path.ChangeExtension(name, ".srt")), srtContent, new UTF8Encoding(false));
            exports.Add(new ExportItem { Path = "exports/" + name, Label = e.Label, Seconds = Math.Round(e.Seconds, 1), Time = DateTime.UtcNow });
            episodeFiles[i] = e;
        }
        if (episodeFiles.Count > 1)
        {
            Progress(f, "juntando a série completa");
            await File.WriteAllLinesAsync(Path.Combine(work, "serie.txt"), episodeFiles.Select(e => $"file '{e.File}'"));
            var name = $"{Safe(f.Title)}-completo-{stamp}.mp4";
            await Ffmpeg(work, ["-f", "concat", "-safe", "0", "-i", "serie.txt", "-c", "copy", "-movflags", "+faststart", Path.Combine("..", "exports", name)]);
            exports.Insert(0, new ExportItem { Path = "exports/" + name, Label = "Série completa", Seconds = Math.Round(episodeFiles.Sum(e => e.Seconds), 1), Time = DateTime.UtcNow });
        }
        store.MutateFlow(f, x => { x.Exports.InsertRange(0, exports); x.Error = null; });
        try { Directory.Delete(work, true); } catch { }
    }

    private static async Task ConcatWithFade(string work, List<(string File, double Dur)> clips, double fade, string outName)
    {
        var args = new List<string>();
        foreach (var c in clips) args.AddRange(["-i", c.File]);
        var filters = new List<string>();
        var dStr = fade.ToString("0.###", CultureInfo.InvariantCulture);
        var prevV = "[0:v]";
        var prevA = "[0:a]";
        double acc = clips[0].Dur;
        for (var i = 1; i < clips.Count; i++)
        {
            var offset = Math.Max(0, acc - fade).ToString("0.###", CultureInfo.InvariantCulture);
            var outV = i == clips.Count - 1 ? "[vout]" : $"[v{i}]";
            var outA = i == clips.Count - 1 ? "[aout]" : $"[a{i}]";
            filters.Add($"{prevV}[{i}:v]xfade=transition=fade:duration={dStr}:offset={offset}{outV}");
            filters.Add($"{prevA}[{i}:a]acrossfade=d={dStr}{outA}");
            prevV = outV; prevA = outA;
            acc = acc - fade + clips[i].Dur;
        }
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outName]);
        await Ffmpeg(work, args);
    }

    private async Task<double> RenderScene(string dir, string work, Scene s, string clip, int w, int h)
    {
        var video = s.Video != null && File.Exists(Path.Combine(dir, s.Video)) ? Path.Combine(dir, s.Video) : null;
        var image = s.Image != null && File.Exists(Path.Combine(dir, s.Image)) ? Path.Combine(dir, s.Image) : null;
        var audio = s.Audio != null && File.Exists(Path.Combine(dir, s.Audio)) ? Path.Combine(dir, s.Audio) : null;
        double dur = s.Seconds;
        var hasVideoAudio = false;
        if (video != null)
        {
            dur = await Probe(video) is double d and > 0.2 ? d : s.Seconds;
            hasVideoAudio = await HasAudio(video);
        }
        else if (audio != null && await Probe(audio) is double ad && ad + 0.6 > dur) dur = ad + 0.6;
        var D = dur.ToString("0.###", CultureInfo.InvariantCulture);
        var frames = (int)Math.Ceiling(dur * 30);
        var args = new List<string>();
        string vf;
        if (video != null)
        {
            args.AddRange(["-i", video]);
            vf = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30,format=yuv420p[v]";
        }
        else if (image != null)
        {
            args.AddRange(["-i", image]);
            vf = $"[0:v]scale={w * 2}:{h * 2}:force_original_aspect_ratio=increase,crop={w * 2}:{h * 2},zoompan=z='min(zoom+0.0009,1.18)':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={w}x{h}:fps=30,setsar=1,format=yuv420p[v]";
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-i", $"color=c=0x0f1218:s={w}x{h}:d={D}:r=30"]);
            var card = $"card-{clip}.txt";
            await File.WriteAllTextAsync(Path.Combine(work, card), Wrap(s.Title, 34), new UTF8Encoding(false));
            vf = File.Exists(Path.Combine(work, "fonte.ttf"))
                ? $"[0:v]drawtext=fontfile=fonte.ttf:textfile={card}:fontcolor=white:fontsize={(w > h ? 48 : 40)}:line_spacing=12:x=(w-text_w)/2:y=(h-text_h)/2,setsar=1,format=yuv420p[v]"
                : "[0:v]setsar=1,format=yuv420p[v]";
        }
        var next = 1;
        var mixes = new List<string>();
        var filters = new List<string> { vf };
        if (hasVideoAudio)
        {
            filters.Add($"[0:a]aresample=48000,aformat=channel_layouts=stereo,volume={(audio != null ? "0.35" : "1")}[a0]");
            mixes.Add("[a0]");
        }
        if (audio != null)
        {
            args.AddRange(["-i", audio]);
            filters.Add($"[{next}:a]aresample=48000,aformat=channel_layouts=stereo,adelay=250|250[a1]");
            mixes.Add("[a1]");
            next++;
        }
        args.AddRange(["-f", "lavfi", "-t", D, "-i", "anullsrc=r=48000:cl=stereo"]);
        mixes.Add($"[{next}:a]");
        filters.Add($"{string.Join("", mixes)}amix=inputs={mixes.Count}:duration=longest:normalize=0,atrim=0:{D}[a]");
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[v]", "-map", "[a]", "-t", D,
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-r", "30", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", clip]);
        await Ffmpeg(work, args);
        return dur;
    }

    private static string Wrap(string text, int width)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.Length + word.Length + 1 > width && cur.Length > 0) { lines.Add(cur); cur = word; }
            else cur = cur.Length == 0 ? word : cur + " " + word;
        }
        if (cur.Length > 0) lines.Add(cur);
        return string.Join("\n", lines);
    }

    private static string Srt(List<(Scene Scene, double Seconds)> timeline, double overlap = 0)
    {
        var sb = new StringBuilder();
        double t = 0; var i = 1;
        static string Ts(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"hh\:mm\:ss\,fff", CultureInfo.InvariantCulture);
        foreach (var (s, d) in timeline)
        {
            if (s.Narration.Length > 0)
                sb.AppendLine((i++).ToString(CultureInfo.InvariantCulture)).AppendLine($"{Ts(t + 0.25)} --> {Ts(t + d - overlap - 0.15)}").AppendLine(Wrap(s.Narration, 42)).AppendLine();
            t += d - overlap;
        }
        return sb.ToString();
    }

    public static string? Tool(string name)
    {
        var env = Environment.GetEnvironmentVariable("CENTRAL_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), name + ".exe"); if (File.Exists(p)) return p; } catch { }
        }
        var fallback = $@"C:\ffmpeg\bin\{name}.exe";
        return File.Exists(fallback) ? fallback : null;
    }

    private static async Task<(int Code, string Out, string Err)> Run(string exe, string cwd, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await o, await e);
    }

    private static async Task Ffmpeg(string cwd, IEnumerable<string> args)
    {
        var all = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        all.AddRange(args);
        var (code, _, err) = await Run(Tool("ffmpeg")!, cwd, all);
        if (code != 0) throw new UserError("FFmpeg: " + Titles.Trunc(err.Trim(), 600));
    }

    private static async Task<double?> Probe(string file)
    {
        var probe = Tool("ffprobe");
        if (probe == null) return null;
        var (code, o, _) = await Run(probe, Path.GetDirectoryName(file)!, ["-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", file]);
        return code == 0 && double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static async Task<bool> HasAudio(string file)
    {
        var probe = Tool("ffprobe");
        if (probe == null) return false;
        var (code, o, _) = await Run(probe, Path.GetDirectoryName(file)!, ["-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", file]);
        return code == 0 && o.Trim().Length > 0;
    }
}
