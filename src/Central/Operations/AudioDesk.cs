using System.Globalization;
using Central.Domain;

namespace Central.Operations;

public sealed record AudioReview(Asset Asset, string Role, double Seconds, bool Approved, string Note);

public sealed class AudioDesk(Store store, Workbench workbench, VideoStories video, Studio studio)
{
    public IReadOnlyList<AudioReview> Reviews(WorkItem item) => store.Read(db =>
        db.List("SELECT asset,role,seconds,approved,note FROM audio_reviews WHERE work=$w",
            r => (r.GetString(0), r.GetString(1), r.GetDouble(2), r.GetBoolean(3), r.GetString(4)), item.Id)
            .Select(row => new AudioReview(Store.AssetIn(db, item.Project, row.Item1), row.Item2, row.Item3, row.Item4, row.Item5)).ToArray());

    public async Task Review(string work, string asset, string role, bool approved, string note, CancellationToken token)
    {
        var item = workbench.Get(work);
        if (item.Area != "audio" || role is not ("trilha" or "voz" or "efeitos")) throw new DomainError("Selecione uma função de áudio.");
        var input = workbench.Files(item).FirstOrDefault(file => file.Asset.Id == asset)?.Asset;
        if (input?.Type is not (".wav" or ".mp3")) throw new DomainError("Importe uma faixa WAV ou MP3 neste trabalho.");
        note = Store.Text(note, 1500, "a revisão, direitos e autoria");
        var probe = await Tools.Run("ffprobe", ["-v", "error", "-protocol_whitelist", "file", "-select_streams", "a:0",
            "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", store.Verify(input)], store.Staging, token);
        if (probe.ExitCode != 0 || !double.TryParse(probe.Output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            || !double.IsFinite(duration) || duration <= 0) throw new DomainError("O ffprobe não conseguiu medir esta faixa.");
        store.Write(db => db.Exec("""
            INSERT INTO audio_reviews VALUES($w,$p,$a,$r,$s,$ok,$n,$u)
            ON CONFLICT(work,asset) DO UPDATE SET role=excluded.role,seconds=excluded.seconds,approved=excluded.approved,
                note=excluded.note,updated=excluded.updated
            """, work, item.Project, asset, role, duration, approved, note, store.Now));
    }

    public void AddToScene(string id, string asset, string episode, int scene)
    {
        var item = workbench.Get(id);
        var track = Reviews(item).FirstOrDefault(track => track.Asset.Id == asset && track.Approved)
            ?? throw new DomainError("Aprove a faixa antes de vinculá-la.");
        var scope = video.Scope(episode) ?? throw new DomainError("Escolha um episódio com storyboard aprovado.");
        if (scope.Project != item.Project) throw new DomainError("Escolha um episódio deste projeto.");
        var shots = studio.Shots(scope.Scope);
        if (scene < 1 || scene > shots.Count) throw new DomainError("Cena fora da montagem.");
        var linked = video.Link(episode, asset, false);
        studio.AddTrack(scope.Scope, linked, track.Role, track.Note[..Math.Min(track.Note.Length, 250)], 1,
            shots.Take(scene - 1).Sum(shot => shot.Frames));
        workbench.Comment(id, $"Faixa aprovada vinculada à cena {scene} de {workbench.Get(episode).Title}.");
    }
}
