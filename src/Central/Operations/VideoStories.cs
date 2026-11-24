using System.Text.Json;
using Central.Domain;

namespace Central.Operations;

public sealed record Story(string Script, string Characters, string[] Shots, bool Approved);
public sealed record EpisodeScope(string Work, string Project, string Scope);

public sealed class VideoStories(Store store, Workbench workbench)
{
    public Story? Get(string work) => store.Read(db => db.One("""
        SELECT script,characters,storyboard,approved FROM video_stories WHERE work=$w
        """, r => new Story(r.GetString(0), r.GetString(1), JsonSerializer.Deserialize<string[]>(r.GetString(2))!, r.GetBoolean(3)), work));
    public EpisodeScope? Scope(string work) => store.Read(db => db.One("""
        SELECT work,project,scope FROM work_scopes WHERE work=$w
        """, r => new EpisodeScope(r.GetString(0), r.GetString(1), r.GetString(2)), work));
    public EpisodeScope? Owner(string scope) => store.Read(db => db.One("""
        SELECT work,project,scope FROM work_scopes WHERE scope=$s
        """, r => new EpisodeScope(r.GetString(0), r.GetString(1), r.GetString(2)), scope));

    public void Save(string work, string script, string characters, string[] directions, bool approved)
    {
        var item = workbench.Get(work);
        if (item.Area != "video") throw new DomainError("Abra um trabalho de vídeo.");
        var scriptText = Store.Text(script, 16000, "o roteiro");
        var cast = Store.Text(characters, 12000, "personagens, continuidade e direitos");
        if (directions.Length != 3) throw new DomainError("Descreva três planos iniciais.");
        var shots = directions.Select(direction => Store.Text(direction, 2200, "a direção de cada plano")).ToArray();
        var parent = store.GetProject(item.Project);
        store.Write(db =>
        {
            if (db.Scalar<long>("SELECT COUNT(*) FROM work_scopes WHERE work=$w", work) != 0)
                throw new DomainError("O roteiro inicial está aprovado. Edite as versões na montagem.");
            db.Exec("""
                INSERT INTO video_stories(work,characters,storyboard,script,approved,updated) VALUES($w,$c,$b,$s,$a,$n)
                ON CONFLICT(work) DO UPDATE SET characters=excluded.characters,storyboard=excluded.storyboard,
                    script=excluded.script,approved=excluded.approved,updated=excluded.updated
                """, work, cast, Store.Json(shots), scriptText, approved, store.Now);
            if (!approved) return;
            var scope = Store.Id();
            db.Exec("INSERT INTO projects VALUES($i,$t,'estudio',$c,$n)", scope, item.Title, parent.Classification, store.Now);
            db.Exec("INSERT INTO work_scopes VALUES($w,$p,$s)", work, item.Project, scope);
            var episode = Store.Id();
            db.Exec("INSERT INTO episodes VALUES($e,$p,$t,$n)", episode, scope, item.Title, store.Now);
            db.Exec("INSERT INTO bibles VALUES($e,1,$s,$b,$n)", episode, scriptText, cast, store.Now);
            for (var i = 0; i < shots.Length; i++)
            {
                var scene = Store.Id();
                db.Exec("INSERT INTO scenes VALUES($id,$e,$o,$d)", scene, episode, i + 1, "Cena " + (i + 1));
                db.Exec("INSERT INTO shots VALUES($id,$e,$s,$o,$d,192)", Store.Id(), episode, scene, i + 1, shots[i]);
            }
            db.Exec("UPDATE work_items SET state='producao',note='Storyboard aprovado. Monte e revise o episódio.',revision=revision+1,updated=$n WHERE id=$w", store.Now, work);
            db.Exec("INSERT INTO work_messages(work,author,body,created) VALUES($w,'Você','Roteiro e três planos aprovados. Montagem iniciada.',$n)", work, store.Now);
            store.Event(db, item.Project, "video.storyboard_aprovado", work);
        });
    }

    public string Link(string work, string source, bool toParent)
    {
        var scope = Scope(work) ?? throw new DomainError("Aprove o storyboard para abrir a montagem.");
        var origin = toParent ? scope.Scope : scope.Project;
        var target = toParent ? scope.Project : scope.Scope;
        var asset = store.GetAsset(origin, source);
        store.Verify(asset);
        return store.Write(db =>
        {
            var existing = db.Scalar<string>("SELECT asset FROM scope_imports WHERE scope=$s AND source=$a", scope.Scope, source);
            if (existing != null) return existing;
            var task = Store.Id();
            db.Exec("INSERT INTO tasks(id,project,title,state,created) VALUES($t,$p,$n,'aceita',$d)", task, target, "Vínculo autorizado", store.Now);
            var id = Store.Id();
            db.Exec("INSERT INTO artifacts VALUES($id,$p,$t,$n,$type,$hash,$origin,$rights,$d)",
                id, target, task, asset.Name, asset.Type, asset.Hash, asset.Origin, asset.Rights, store.Now);
            db.Exec("INSERT INTO scope_imports VALUES($s,$a,$i)", scope.Scope, source, id);
            if (toParent)
            {
                db.Exec("INSERT INTO work_files VALUES($p,$w,$a,'exportacao')", scope.Project, scope.Work, id);
                db.Exec("UPDATE work_items SET state='aceito',note='Master aprovado e disponibilizado no projeto.',revision=revision+1,updated=$n WHERE id=$w",
                    store.Now, scope.Work);
            }
            store.Event(db, target, "video.arquivo_vinculado", asset.Hash);
            return id;
        });
    }
}
