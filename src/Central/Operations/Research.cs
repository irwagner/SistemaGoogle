using System.Text.RegularExpressions;
using Central.Domain;

namespace Central.Operations;

public sealed record Question(string Id, string Text, string Criteria);
public sealed record Source(string Id, string Question, string Name, string Locator, string Author, string Rights, string Excerpt, string Consulted);
public sealed record Claim(string Id, string Question, string Source, string Text, bool Inference, string? Review);

public sealed class Research(Store store)
{
    public IReadOnlyList<Question> Questions(string project) => store.Read(db => db.List(
        "SELECT id,question,criteria FROM research WHERE project=$p ORDER BY created", r => new Question(r.GetString(0), r.GetString(1), r.GetString(2)), project));
    public IReadOnlyList<Source> Sources(string project) => store.Read(db => db.List("""
        SELECT id,research,label,locator,author,rights,excerpt,consulted FROM sources WHERE project=$p ORDER BY consulted
        """, r => new Source(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7)), project));
    public IReadOnlyList<Claim> Claims(string project) => store.Read(db => db.List("""
        SELECT id,research,source,claim,inference,review FROM claims WHERE project=$p ORDER BY rowid
        """, r => new Claim(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.NullableText(5)), project));

    public void Ask(string project, string text, string criteria)
    {
        if (store.GetProject(project).Kind != "pesquisa") throw new DomainError("Escolha um projeto de pesquisa.");
        text = Store.Text(text, 2000, "a pergunta");
        criteria = Store.Text(criteria, 3000, "o escopo, prazo e critérios");
        store.Write(db =>
        {
            var id = Store.Id();
            db.Exec("INSERT INTO research VALUES($i,$p,$q,$c,$n)", id, project, text, criteria, store.Now);
            store.Event(db, project, "pesquisa.pergunta", id);
        });
    }

    public void AddSource(string project, string question, string label, string locator, string author, string rights, string excerpt)
    {
        label = Store.Text(label, 200, "o título");
        locator = Store.Text(locator, 1000, "a URL ou hash");
        author = Store.Text(author, 150, "o autor");
        rights = Store.Text(rights, 250, "a permissão de uso");
        excerpt = Store.Text(excerpt, 3000, "o trecho ou página de referência");
        if (Regex.IsMatch(locator, "^[a-f0-9]{64}$"))
        {
            var asset = store.Assets(project).FirstOrDefault(a => a.Hash == locator) ?? throw new DomainError("O hash precisa pertencer a este projeto.");
            store.Verify(asset);
        }
        else if (!Uri.TryCreate(locator, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length > 0 || uri.IsLoopback || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new DomainError("Use URL HTTPS pública canônica ou o hash de um arquivo do projeto.");
        store.Write(db =>
        {
            if (db.Scalar<long>("SELECT COUNT(*) FROM research WHERE id=$i AND project=$p", question, project) != 1)
                throw new DomainError("Pergunta não encontrada.");
            if (db.Scalar<long>("SELECT COUNT(*) FROM sources WHERE project=$p AND research=$q AND locator=$l AND excerpt=$e",
                project, question, locator, excerpt) != 0) return;
            var id = Store.Id();
            db.Exec("INSERT INTO sources VALUES($i,$p,$q,$l,$u,$a,$r,$e,$n)", id, project, question, label, locator, author, rights, excerpt, store.Now);
            store.Event(db, project, "pesquisa.fonte", id);
        });
    }

    public void AddClaim(string project, string source, string text, bool inference)
    {
        text = Store.Text(text, 3000, "a afirmação");
        store.Write(db =>
        {
            var question = db.Scalar<string>("SELECT research FROM sources WHERE id=$i AND project=$p", source, project) ?? throw new DomainError("Fonte não encontrada.");
            var id = Store.Id();
            db.Exec("INSERT INTO claims VALUES($id,$p,$q,$s,$t,$i,NULL)", id, project, question, source, text, inference);
            store.Event(db, project, "pesquisa.afirmacao", id);
        });
    }

    public void Review(string project, string claim, bool approved, string notes)
    {
        notes = Store.Text(notes, 1800, "a revisão da evidência");
        store.Write(db =>
        {
            if (db.Exec("UPDATE claims SET review=$r WHERE project=$p AND id=$id", (approved ? "Aprovada. " : "Pendente. ") + notes, project, claim) != 1)
                throw new DomainError("Afirmação não encontrada.");
            store.Event(db, project, "pesquisa.revisao", claim);
        });
    }

    public string Report(string project)
    {
        var sources = Sources(project).ToDictionary(s => s.Id);
        var claims = Claims(project);
        var report = new System.Text.StringBuilder($"# {store.GetProject(project).Name}\n");
        foreach (var question in Questions(project))
        {
            report.AppendLine($"\n## {question.Text}\n\n{question.Criteria}\n");
            foreach (var claim in claims.Where(claim => claim.Question == question.Id))
            {
                var source = sources[claim.Source];
                report.AppendLine($"- {(claim.Inference ? "Inferência" : "Afirmação")}. {claim.Text} {claim.Review ?? "Sem revisão."}");
                report.AppendLine($"  Fonte: {source.Name}, {source.Author}, {source.Locator}. Consulta {source.Consulted}. Uso: {source.Rights}. Referência: {source.Excerpt}");
            }
        }
        return report.ToString();
    }
}
