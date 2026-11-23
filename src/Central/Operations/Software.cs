using Central.Domain;

namespace Central.Operations;

public sealed class Software(Store store)
{
    public string Package(string project, string task, string baseCommit, string objective, string acceptance)
    {
        var selected = store.Tasks(project).SingleOrDefault(item => item.Id == task) ?? throw new DomainError("Tarefa não encontrada.");
        if (baseCommit.Length is not (40 or 64) || baseCommit.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainError("Informe o SHA completo da base revisada.");
        var inputTasks = store.Read(db => db.List("""
            WITH RECURSIVE inputs(id) AS (
                SELECT $t UNION
                SELECT d.predecessor FROM dependencies d JOIN inputs i ON d.task=i.id WHERE d.project=$p
            )
            SELECT id FROM inputs
            """, row => row.GetString(0), task, project)).ToHashSet();
        var inputs = store.Assets(project).Where(asset => inputTasks.Contains(asset.Task))
            .Select(asset => new { asset.Id, asset.Name, asset.Hash, asset.Rights });
        return Store.Json(new
        {
            schema = 1, project, task, inputVersion = selected.Version, baseCommit,
            mode = "assistida", executor = "cliente oficial Antigravity operado manualmente",
            objective = Store.Text(objective, 2000, "o objetivo"), acceptance = Store.Text(acceptance, 2000, "o critério de aceite"),
            allowedTree = "worktree exclusiva desta tarefa", classification = store.GetProject(project).Classification, inputs,
            returnPath = "Arquivos da tarefa na Central",
            ownerSteps = new[] { "Confirmar perfil, modelo, classificação e excedente desligado no cliente.",
                "Trabalhar somente na worktree aprovada. Exportar diff, SHA, build/testes e recibo sanitizado.",
                "Importar os arquivos e declarar a ferramenta e a participação humana. Somente depois revisar e integrar." }
        });
    }
}
