using System.IO.Compression;
using Central.Domain;

namespace Central.Operations;

public sealed record Delivery(string Id, string Work, string Project, string Media, string? Cover, string? Caption,
    string Title, string Description, string Channel, string Visibility, string State, string? Remote, long? Views, string? Account);

public sealed class Distribution(Store store, Workbench workbench)
{
    private const string Selection = """
        SELECT id,work,project,media,cover,caption,title,description,channel,visibility,state,remote_id,views,remote_account FROM deliveries
        """;
    public IReadOnlyList<Delivery> List(string work) => store.Read(db => db.List(Selection + " WHERE work=$w ORDER BY created DESC", Map, work));
    public Delivery Get(string work, string id) => List(work).FirstOrDefault(item => item.Id == id)
        ?? throw new DomainError("Entrega não encontrada neste trabalho.");

    public void Create(string work, string media, string? cover, string? caption, string title, string description, string channel,
        string visibility, bool approved)
    {
        var item = workbench.Get(work);
        if (item.Area != "distribuicao") throw new DomainError("Abra um trabalho de distribuição.");
        if (visibility is not ("private" or "unlisted" or "public" or "local")) throw new DomainError("Visibilidade inválida.");
        title = Store.Text(title, 100, "o título");
        description = Store.Text(description, 4900, "a descrição");
        channel = Store.Text(channel, 160, "o destino");
        cover = string.IsNullOrWhiteSpace(cover) ? null : cover;
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption;
        store.Write(db =>
        {
            if (Store.AssetIn(db, item.Project, media).Type != ".mp4") throw new DomainError("Selecione um master MP4.");
            if (!string.IsNullOrEmpty(cover) && Store.AssetIn(db, item.Project, cover).Type is not (".png" or ".jpg" or ".webp"))
                throw new DomainError("Selecione uma capa.");
            if (!string.IsNullOrEmpty(caption) && Store.AssetIn(db, item.Project, caption).Type != ".srt") throw new DomainError("Selecione uma legenda SRT.");
            db.Exec("""
                INSERT INTO deliveries(id,project,work,media,cover,caption,title,description,channel,visibility,state,created,updated)
                VALUES($id,$p,$w,$m,$c,$s,$t,$d,$ch,$v,$st,$n,$n)
                """, Store.Id(), item.Project, work, media, cover, caption, title, description, channel, visibility,
                approved ? "aprovada_local" : "rascunho", store.Now);
        });
    }

    public void Approve(string work, string id)
    {
        var delivery = Get(work, id);
        store.Write(db => db.Exec("UPDATE deliveries SET state='aprovada_local',updated=$n WHERE id=$i AND state='rascunho'", store.Now, delivery.Id));
    }

    public string Export(string work, string id)
    {
        var delivery = Get(work, id);
        if (delivery.State == "rascunho") throw new DomainError("Aprove o pacote para exportar.");
        var result = Path.Combine(store.Staging, Store.Id() + ".zip");
        using var zip = ZipFile.Open(result, ZipArchiveMode.Create);
        var hashes = new Dictionary<string, string>();
        Add("master", delivery.Media);
        if (delivery.Cover != null) Add("capa", delivery.Cover);
        if (delivery.Caption != null) Add("legenda", delivery.Caption);
        using var manifest = new StreamWriter(zip.CreateEntry("entrega.json").Open());
        manifest.Write(Store.Json(new { schema = 1, delivery.Title, delivery.Description, delivery.Channel, delivery.Visibility, hashes }));
        return result;

        void Add(string role, string asset)
        {
            var file = store.GetAsset(delivery.Project, asset);
            var name = role + file.Type;
            zip.CreateEntryFromFile(store.Verify(file), name);
            hashes.Add(name, file.Hash);
        }
    }

    internal static Delivery Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(r.GetString(0),r.GetString(1),r.GetString(2),
        r.GetString(3),r.NullableText(4),r.NullableText(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),r.NullableText(11),r.NullableLong(12),r.NullableText(13));
}
