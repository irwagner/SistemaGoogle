using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Central.Domain;

namespace Central.Operations;

public sealed record Bible(string Episode, string Title, int Version, string Script, string Text);
public sealed record Shot(string Id, int Ordinal, string Scene, string Direction, int Frames);
public sealed record Take(string Id, string Shot, string Asset, int BibleVersion, string Parameters, string Disposition, string? Review);
public sealed record Track(string Asset, string Role, string Voice, double Gain, int StartFrame);
public sealed record Edit(string Id, string State, string Manifest, string? Master, string? Review);
public sealed record Clip(string Shot, string Take, string Asset, string Hash, int InFrame, int Frames);
public sealed record Mix(string Asset, string Hash, string Role, string Voice, double Gain, int StartFrame);
public sealed record Timeline(int Schema, int FpsNumerator, int FpsDenominator, int Width, int Height, int BibleVersion, Clip[] Clips, Mix[] Audio);

public sealed class Studio(Store store)
{
    private readonly ConcurrentDictionary<string, byte> rendering = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int Fps = 24;
    private const int Width = 1280;
    private const int Height = 720;

    public Bible? Current(string project) => store.Read(db => db.One("""
        SELECT e.id,e.title,b.version,b.script,b.bible FROM episodes e JOIN bibles b ON b.episode=e.id WHERE e.project=$p
        ORDER BY b.version DESC LIMIT 1
        """, r => new Bible(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4)), project));
    public IReadOnlyList<Shot> Shots(string project) => store.Read(db => db.List("""
        SELECT s.id,s.ordinal,c.description,s.direction,s.frames FROM shots s JOIN scenes c ON c.id=s.scene
        JOIN episodes e ON e.id=s.episode WHERE e.project=$p ORDER BY s.ordinal
        """, r => new Shot(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4)), project));
    public IReadOnlyList<Take> Takes(string project) => store.Read(db => db.List("""
        SELECT id,shot,artifact,bible_version,parameters,disposition,review FROM takes WHERE project=$p ORDER BY created
        """, r => new Take(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.NullableText(6)), project));
    public IReadOnlyList<Track> Tracks(string project) => store.Read(db => db.List("""
        SELECT artifact,role,voice,gain,start_frame FROM tracks WHERE project=$p ORDER BY rowid
        """, r => new Track(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetInt32(4)), project));
    public IReadOnlyList<Edit> Edits(string project) => store.Read(db => db.List("""
        SELECT id,state,manifest,master,review FROM edits WHERE project=$p ORDER BY created DESC
        """, MapEdit, project));

    public void SaveBible(string project, string title, string script, string bible)
    {
        if (store.GetProject(project).Kind != "estudio") throw new DomainError("O projeto precisa ser de estúdio.");
        title = Store.Text(title, 120, "o título");
        script = Store.Text(script, 16000, "o roteiro");
        bible = Store.Text(bible, 16000, "a bíblia, continuidade e direitos");
        store.Write(db =>
        {
            var episode = db.Scalar<string>("SELECT id FROM episodes WHERE project=$p", project);
            if (episode == null)
            {
                episode = Store.Id();
                db.Exec("INSERT INTO episodes VALUES($id,$p,$t,$now)", episode, project, title, store.Now);
            }
            var version = db.Scalar<long>("SELECT COALESCE(MAX(version),0)+1 FROM bibles WHERE episode=$e", episode);
            db.Exec("INSERT INTO bibles VALUES($e,$v,$s,$b,$now)", episode, version, script, bible, store.Now);
            store.Event(db, project, "estudio.biblia", $"{episode}/{version}");
        });
    }

    public void AddShot(string project, int scene, string sceneDescription, string direction, int frames)
    {
        if (scene < 1 || frames is < 24 or > 2400) throw new DomainError("Use 24 a 2400 quadros e numeração positiva de cena.");
        direction = Store.Text(direction, 4000, "a direção do plano");
        sceneDescription = Store.Text(sceneDescription, 1000, "a descrição da cena");
        store.Write(db =>
        {
            var episode = Episode(db, project);
            var sceneId = db.Scalar<string>("SELECT id FROM scenes WHERE episode=$e AND ordinal=$s", episode, scene);
            if (sceneId == null)
            {
                sceneId = Store.Id();
                db.Exec("INSERT INTO scenes VALUES($id,$e,$s,$d)", sceneId, episode, scene, sceneDescription);
            }
            var id = Store.Id();
            var ordinal = db.Scalar<long>("SELECT COALESCE(MAX(ordinal),0)+1 FROM shots WHERE episode=$e", episode);
            db.Exec("INSERT INTO shots VALUES($id,$e,$s,$o,$d,$f)", id, episode, sceneId, ordinal, direction, frames);
            store.Event(db, project, "estudio.plano", id);
        });
    }

    public void AddTake(string project, string shot, string asset, string parameters)
    {
        parameters = Store.Text(parameters, 4000, "a origem, o executor e os parâmetros observados");
        store.Write(db =>
        {
            var episode = Episode(db, project);
            if (db.Scalar<long>("SELECT COUNT(*) FROM shots WHERE episode=$e AND id=$s", episode, shot) != 1)
                throw new DomainError("Plano não encontrado.");
            if (Store.AssetIn(db, project, asset).Type is not (".mp4" or ".mov" or ".webm"))
                throw new DomainError("Escolha um arquivo de vídeo do projeto.");
            var version = db.Scalar<long>("SELECT MAX(version) FROM bibles WHERE episode=$e", episode);
            var id = Store.Id();
            db.Exec("INSERT INTO takes VALUES($id,$p,$e,$s,$a,$v,$q,'pendente',NULL,$n)", id, project, episode, shot, asset, version, parameters, store.Now);
            store.Event(db, project, "estudio.take_importado", id);
        });
    }

    public void ReviewTake(string project, string take, bool select, string notes)
    {
        notes = Store.Text(notes, 2000, "a avaliação de continuidade, som e direitos");
        store.Write(db =>
        {
            var shot = db.Scalar<string>("SELECT shot FROM takes WHERE id=$i AND project=$p", take, project)
                ?? throw new DomainError("Take não encontrado.");
            if (select && db.Scalar<long>("SELECT COUNT(*) FROM takes WHERE shot=$s AND disposition='selecionado' AND id<>$id", shot, take) != 0)
                throw new DomainError("Registre a reprovação ou desmarque o take selecionado deste plano primeiro.");
            db.Exec("UPDATE takes SET disposition=$s,review=$r WHERE id=$id", select ? "selecionado" : "reprovado", notes, take);
            store.Event(db, project, select ? "estudio.take_selecionado" : "estudio.take_reprovado", take);
        });
    }

    public void AddTrack(string project, string asset, string role, string voice, double gain, int start)
    {
        if (role is not ("trilha" or "voz" or "efeitos") || !double.IsFinite(gain) || gain is < 0 or > 2 || start is < 0 or > 12000)
            throw new DomainError("Faixa, ganho ou posição inválida.");
        voice = Store.Text(voice, 300, "a voz ou autoria e idioma");
        store.Write(db =>
        {
            var episode = Episode(db, project);
            if (Store.AssetIn(db, project, asset).Type is not (".wav" or ".mp3")) throw new DomainError("Escolha um arquivo de áudio do projeto.");
            var id = Store.Id();
            db.Exec("INSERT INTO tracks VALUES($id,$p,$e,$a,$r,$v,$g,$s)", id, project, episode, asset, role, voice, gain, start);
            store.Event(db, project, "estudio.faixa", id);
        });
    }

    public string CreateEdit(string project) => store.Write(db =>
    {
        var episode = Episode(db, project);
        var count = db.Scalar<long>("SELECT COUNT(*) FROM shots WHERE episode=$e", episode);
        var version = checked((int)db.Scalar<long>("SELECT MAX(version) FROM bibles WHERE episode=$e", episode));
        var clips = db.List("""
            SELECT s.id,t.id,a.id,a.hash,s.frames FROM shots s JOIN takes t ON t.shot=s.id AND t.disposition='selecionado'
            JOIN artifacts a ON a.id=t.artifact WHERE s.episode=$e AND t.bible_version=$v ORDER BY s.ordinal
            """, r => new Clip(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), 0, r.GetInt32(4)), episode, version);
        if (count is < 3 or > 30 || clips.Count != count) throw new DomainError("Selecione um take da bíblia vigente para cada um dos 3 a 30 planos.");
        var audio = db.List("""
            SELECT a.id,a.hash,t.role,t.voice,t.gain,t.start_frame FROM tracks t JOIN artifacts a ON a.id=t.artifact WHERE t.episode=$e ORDER BY t.rowid
            """, r => new Mix(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetInt32(5)), episode);
        if (audio.Count is < 1 or > 12 || audio.Any(track => track.StartFrame >= clips.Sum(clip => clip.Frames)))
            throw new DomainError("Adicione uma a doze faixas dentro da duração do episódio.");
        var manifest = Store.Json(new Timeline(1, Fps, 1, Width, Height, version, clips.ToArray(), audio.ToArray()));
        var existing = db.Scalar<string>("SELECT id FROM edits WHERE project=$p AND manifest=$m LIMIT 1", project, manifest);
        if (existing != null) return existing;
        var id = Store.Id();
        db.Exec("INSERT INTO edits(id,project,episode,manifest,state,created) VALUES($i,$p,$e,$m,'pronta',$n)", id, project, episode, manifest, store.Now);
        store.Event(db, project, "estudio.edicao", id);
        return id;
    });

    public async Task Render(string project, string id, CancellationToken token)
    {
        var edit = GetEdit(project, id);
        if (edit.State is "revisao" or "aprovada" or "reprovada") return;
        if (!rendering.TryAdd(id, 0)) throw new DomainError("A edição já está em render.");
        var stage = Path.Combine(store.Staging, "render-" + Store.Id());
        try
        {
            var timeline = ReadTimeline(edit);
            var mediaBytes = timeline.Clips.Sum(clip => store.GetAsset(project, clip.Asset).Size)
                + timeline.Audio.Sum(audio => store.GetAsset(project, audio.Asset).Size);
            if (new DriveInfo(Path.GetPathRoot(stage)!).AvailableFreeSpace < 512L * 1024 * 1024 + mediaBytes)
                throw new DomainError("Espaço insuficiente para staging e render.");
            Directory.CreateDirectory(stage);
            SetState(project, id, "renderizando");
            for (var index = 0; index < timeline.Clips.Length; index++)
            {
                var clip = timeline.Clips[index];
                var input = Verified(project, clip.Asset, clip.Hash);
                var file = $"{index:00}.mp4";
                await Encode(stage, ["-protocol_whitelist", "file", "-i", input, "-map", "0:v:0", "-an",
                    "-vf", $"fps={Fps},scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,trim=start_frame={clip.InFrame}:end_frame={clip.InFrame + clip.Frames},setpts=PTS-STARTPTS",
                    "-frames:v", N(clip.Frames), "-pix_fmt", "yuv420p", "-c:v", "libx264", "-threads", "2", "-preset", "fast", file], token);
                await VerifyClip(stage, file, clip.Frames, token);
            }
            await File.WriteAllLinesAsync(Path.Combine(stage, "concat.txt"),
                Enumerable.Range(0, timeline.Clips.Length).Select(index => $"file '{index:00}.mp4'"), token);
            var args = new List<string> { "-f", "concat", "-safe", "1", "-i", "concat.txt" };
            var filters = new List<string>();
            for (var i = 0; i < timeline.Audio.Length; i++)
            {
                var track = timeline.Audio[i];
                args.AddRange(["-protocol_whitelist", "file", "-i", Verified(project, track.Asset, track.Hash)]);
                var delay = (long)Math.Round(track.StartFrame * 1000.0 / Fps);
                filters.Add($"[{i + 1}:a:0]volume={track.Gain.ToString(CultureInfo.InvariantCulture)},adelay={delay}:all=1[a{i}]");
            }
            filters.Add(string.Concat(Enumerable.Range(0, timeline.Audio.Length).Select(i => $"[a{i}]")) +
                $"amix=inputs={timeline.Audio.Length}:normalize=1,loudnorm=I=-17:LRA=8:TP=-2,apad[mix]");
            var frames = timeline.Clips.Sum(clip => clip.Frames);
            args.AddRange(["-filter_complex", string.Join(';', filters), "-map", "0:v:0", "-map", "[mix]", "-c:v", "copy",
                "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-t", (frames / (double)Fps).ToString("0.000000", CultureInfo.InvariantCulture),
                "-map_metadata", "-1", "-movflags", "+faststart", "master.mp4"]);
            await Encode(stage, args, token);
            var probe = await VerifyMaster(stage, "master.mp4", frames, token);
            await using var output = File.OpenRead(Path.Combine(stage, "master.mp4"));
            var master = await store.Import(project, null, "master-" + id[..8] + ".mp4", output, "FFmpeg local · " + id,
                "Direitos vinculados aos takes e faixas da edição.", "master-" + id + "-" + Store.HashFile(output.Name)[..24], token);
            store.Write(db =>
            {
                db.Exec("UPDATE edits SET state='revisao',master=$a,probe=$q WHERE project=$p AND id=$e", master.Id, probe, project, id);
                store.Event(db, project, "estudio.master_validado", master.Hash);
            });
        }
        catch (Exception exception) when (exception is DomainError or IOException or OperationCanceledException)
        {
            SetState(project, id, "interrompida");
            if (exception is IOException) throw new DomainError("O render foi interrompido por erro de armazenamento.");
            throw;
        }
        finally
        {
            rendering.TryRemove(id, out _);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
        }
    }

    public void ReviewEdit(string project, string id, bool accept, string review)
    {
        review = Store.Text(review, 2000, "a avaliação visual e sonora");
        store.Write(db =>
        {
            var count = db.Exec("UPDATE edits SET state=$s,review=$r WHERE project=$p AND id=$id AND state='revisao' AND master IS NOT NULL",
                accept ? "aprovada" : "reprovada", review, project, id);
            if (count != 1) throw new DomainError("A edição precisa de um master em revisão.");
            store.Event(db, project, accept ? "estudio.master_aprovado" : "estudio.master_reprovado", id);
        });
    }

    public string Probe(string project, string id) => store.Read(db => db.Scalar<string>(
        "SELECT probe FROM edits WHERE project=$p AND id=$i", project, id)) ?? throw new DomainError("Ainda não existe laudo.");

    public string Export(string project, string id, string caption, string cover, string metadata, bool authorized)
    {
        metadata = Store.Text(metadata, 4000, "o título, público, créditos e autorização de uso");
        if (!authorized) throw new DomainError("Confirme os direitos de uso para esta entrega local.");
        var edit = GetEdit(project, id);
        if (edit.State != "aprovada" || edit.Master == null) throw new DomainError("Aprove a edição antes de exportar o pacote.");
        var timeline = ReadTimeline(edit);
        var captions = store.GetAsset(project, caption);
        var image = store.GetAsset(project, cover);
        if (captions.Type != ".srt" || image.Type is not (".png" or ".jpg" or ".webp"))
            throw new DomainError("Selecione legenda SRT e capa PNG, JPEG ou WebP.");
        var master = store.GetAsset(project, edit.Master);
        var assets = timeline.Clips.Select(clip => clip.Asset).Concat(timeline.Audio.Select(track => track.Asset))
            .Distinct().Select(a => store.GetAsset(project, a)).ToArray();
        var unresolved = new[] { "pendente", "incerto", "proibid", "sem autorização", "não autorizado" };
        if (assets.Append(captions).Append(image).Any(a => unresolved.Any(term => a.Rights.Contains(term, StringComparison.OrdinalIgnoreCase))))
            throw new DomainError("Resolva os direitos das mídias antes da entrega.");
        var package = Path.Combine(store.Staging, Store.Id() + ".zip");
        try
        {
            using var zip = ZipFile.Open(package, ZipArchiveMode.Create);
            var files = new Dictionary<string, string>();
            Add("master.mp4", master);
            Add("legenda.srt", captions);
            Add("capa" + image.Type, image);
            foreach (var asset in assets) Add("midia/" + asset.Id + asset.Type, asset);
            Write("timeline.json", edit.Manifest);
            Write("ffprobe.json", Probe(project, id));
            var bible = store.Read(db => db.One("""
                SELECT b.script,b.bible FROM bibles b JOIN episodes e ON e.id=b.episode WHERE e.project=$p AND b.version=$v
                """, r => Tuple.Create(r.GetString(0), r.GetString(1)), project, timeline.BibleVersion))!;
            Write("roteiro.txt", bible.Item1);
            Write("biblia.txt", bible.Item2);
            Write("entrega.txt", metadata + Environment.NewLine + edit.Review);
            Write("manifesto.json", Store.Json(new { schema = 1, edit = id, hashes = files,
                media = assets.Select(a => new { a.Id, a.Hash, a.Origin, a.Rights }) }));
            return package;

            void Add(string name, Asset asset)
            {
                zip.CreateEntryFromFile(store.Verify(asset), name, CompressionLevel.Fastest);
                files.Add(name, asset.Hash);
            }
            void Write(string name, string contents)
            {
                using var stream = new StreamWriter(zip.CreateEntry(name).Open());
                stream.Write(contents);
                if (name != "manifesto.json") files.Add(name, Store.Hash(System.Text.Encoding.UTF8.GetBytes(contents)));
            }
        }
        catch { File.Delete(package); throw; }
    }

    public string FlowPackage(string project, string shot)
    {
        var bible = Current(project) ?? throw new DomainError("Registre a bíblia.");
        var item = Shots(project).SingleOrDefault(value => value.Id == shot) ?? throw new DomainError("Plano não encontrado.");
        return Store.Json(new { schema = 1, mode = "assistida", status = "ensaio externo pendente",
            project, shot, bible.Version, bible.Text, item.Direction, durationSeconds = item.Frames / (double)Fps,
            outputs = 1, required = new[] { "Perfil autorizado conferido no Flow oficial", "Revisar modelo, preço e teto de créditos na tela",
                "Confirmar direitos e exportação das referências", "Operador confirma geração e coleta manualmente",
                "Importar arquivo e recibo sanitizado com identidade mascarada, modo, custo e identificador, se disponível" } });
    }

    private Edit GetEdit(string project, string id) => store.Read(db => db.One(
        "SELECT id,state,manifest,master,review FROM edits WHERE project=$p AND id=$i", MapEdit, project, id))
        ?? throw new DomainError("Edição não encontrada.");
    private static Edit MapEdit(Microsoft.Data.Sqlite.SqliteDataReader row) =>
        new(row.GetString(0), row.GetString(1), row.GetString(2), row.NullableText(3), row.NullableText(4));
    private static Timeline ReadTimeline(Edit edit) => JsonSerializer.Deserialize<Timeline>(edit.Manifest, JsonOptions)
        ?? throw new DomainError("Manifesto inválido.");
    private static string Episode(Microsoft.Data.Sqlite.SqliteConnection db, string project) =>
        db.Scalar<string>("SELECT id FROM episodes WHERE project=$p", project) ?? throw new DomainError("Crie a bíblia primeiro.");
    private string Verified(string project, string id, string hash)
    {
        var asset = store.GetAsset(project, id);
        if (asset.Hash != hash) throw new DomainError("A revisão do arquivo mudou.");
        return store.Verify(asset);
    }
    private void SetState(string project, string id, string state) => store.Write(db =>
    {
        db.Exec("UPDATE edits SET state=$s WHERE project=$p AND id=$id", state, project, id);
        store.Event(db, project, "estudio." + state, id);
    });

    private static string N(int number) => number.ToString(CultureInfo.InvariantCulture);
    private static async Task Encode(string stage, IEnumerable<string> arguments, CancellationToken token)
    {
        var result = await Tools.Run("ffmpeg", new[] { "-nostdin", "-v", "error", "-y", "-xerror", "-filter_threads", "2", "-filter_complex_threads", "2" }
            .Concat(arguments), stage, token);
        if (result.ExitCode != 0) throw new DomainError("O FFmpeg recusou a mídia, o codec selecionado ou a montagem.");
    }
    private static async Task<string> ProbeStreams(string stage, string file, CancellationToken token)
    {
        var result = await Tools.Run("ffprobe", ["-v", "error", "-show_entries",
            "stream=codec_type,codec_name,width,height,r_frame_rate,nb_frames,duration:format=duration", "-of", "json", file], stage, token);
        if (result.ExitCode != 0) throw new DomainError("O ffprobe recusou a saída.");
        return result.Output;
    }
    private static async Task VerifyClip(string stage, string file, int expected, CancellationToken token)
    {
        using var probe = JsonDocument.Parse(await ProbeStreams(stage, file, token));
        if (!probe.RootElement.GetProperty("streams").EnumerateArray().Any(s =>
            s.TryGetProperty("nb_frames", out var frames) && frames.GetString() == N(expected)))
            throw new DomainError("O take não tem a quantidade de quadros exigida.");
    }
    private static async Task<string> VerifyMaster(string stage, string file, int expected, CancellationToken token)
    {
        var raw = await ProbeStreams(stage, file, token);
        using var probe = JsonDocument.Parse(raw);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (streams.Length != 2 || !streams.Any(s => s.GetProperty("codec_name").GetString() == "aac") ||
            !streams.Any(s => s.GetProperty("codec_name").GetString() == "h264" && s.GetProperty("width").GetInt32() == Width
                && s.GetProperty("height").GetInt32() == Height && s.GetProperty("r_frame_rate").GetString() == "24/1"
                && s.GetProperty("nb_frames").GetString() == N(expected)) || Math.Abs(duration - expected / (double)Fps) > 1.0 / Fps)
            throw new DomainError("Master não corresponde aos codecs, quadros ou duração da timeline.");
        await Encode(stage, ["-i", file, "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "NUL"], token);
        return raw;
    }
}
