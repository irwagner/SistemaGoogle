using System.Text.Json;
using Central.Domain;
using Microsoft.Data.Sqlite;

namespace Central.Operations;

public static class Backup
{
    private sealed record Snapshot(int Schema, string DatabaseHash, string[] Objects);

    public static void Create(Store store, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (Path.Exists(target) || IsInside(store.Root, target)) throw new DomainError("Use uma pasta nova fora dos dados da Central.");
        Store.RejectLinks(target);
        var stage = target + "." + Store.Id();
        Directory.CreateDirectory(stage);
        var file = Path.Combine(stage, "central.sqlite");
        using (var source = store.Open())
        using (var output = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            output.Open();
            source.BackupDatabase(output);
        }
        var (schema, objects) = Inspect(file);
        foreach (var hash in objects)
            CopyObject(store.ObjectPath(hash), stage, hash);
        File.WriteAllText(Path.Combine(stage, "snapshot.json"), Store.Json(new Snapshot(schema, Store.HashFile(file), objects)));
        Directory.Move(stage, target);
    }

    public static void Restore(string snapshot, string destination)
    {
        var source = Path.GetFullPath(snapshot);
        var target = Path.GetFullPath(destination);
        Store.RejectLinks(source);
        Store.RejectLinks(target);
        Directory.CreateDirectory(target);
        // The process owns the launcher lock for this root.
        if (Directory.EnumerateFileSystemEntries(target).Any(path => Path.GetFileName(path) != "central.lock")
            || IsInside(source, target) || IsInside(target, source))
            throw new DomainError("A restauração requer uma pasta de dados vazia e separada.");
        var metadata = Path.Combine(source, "snapshot.json");
        var sourceDb = Path.Combine(source, "central.sqlite");
        Store.RejectLinks(metadata);
        Store.RejectLinks(sourceDb);
        var manifest = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(metadata), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new DomainError("Manifesto ausente.");
        if (Store.HashFile(sourceDb) != manifest.DatabaseHash) throw new DomainError("O banco do snapshot tem hash divergente.");
        var (schema, objects) = Inspect(sourceDb);
        if (schema != manifest.Schema || !objects.SequenceEqual(manifest.Objects)) throw new DomainError("Índice do snapshot divergente.");
        var stage = Path.Combine(target, "restore-" + Store.Id());
        Directory.CreateDirectory(stage);
        Directory.CreateDirectory(Path.Combine(stage, "objects"));
        foreach (var hash in objects)
            CopyObject(Path.Combine(source, "objects", hash[..2], hash), stage, hash);
        var stageDb = Path.Combine(stage, "central.sqlite");
        File.Copy(sourceDb, stageDb);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stageDb, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString()))
        {
            db.Open();
            db.Exec("PRAGMA journal_mode=DELETE");
            using var tx = db.BeginTransaction();
            db.Exec("INSERT OR REPLACE INTO service_flags VALUES('restore_hold','Reconciliar efeitos posteriores ao snapshot')");
            db.Exec("UPDATE effects SET state='ambiguous' WHERE state IN ('sending','accepted')");
            db.Exec("UPDATE tasks SET state='espera',reason='reconciliar_restore' WHERE id IN (SELECT task FROM effects WHERE state='ambiguous')");
            tx.Commit();
        }
        Directory.Move(Path.Combine(stage, "objects"), Path.Combine(target, "objects"));
        File.Move(stageDb, Path.Combine(target, "central.sqlite"));
        Directory.Delete(stage);
    }

    private static (int Schema, string[] Objects) Inspect(string file)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        if (db.Scalar<string>("PRAGMA integrity_check") != "ok" || db.List("PRAGMA foreign_key_check", r => r.GetString(0)).Count > 0)
            throw new DomainError("O snapshot falhou na verificação do SQLite.");
        var schema = checked((int)db.Scalar<long>("SELECT MAX(version) FROM migrations"));
        var objects = db.List("SELECT DISTINCT hash FROM artifacts ORDER BY hash", r => r.GetString(0)).ToArray();
        if (objects.Any(hash => hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw new DomainError("Hash inválido no snapshot.");
        return (schema, objects);
    }

    private static void CopyObject(string source, string target, string hash)
    {
        Store.RejectLinks(source);
        if (!File.Exists(source)) throw new DomainError($"Objeto ausente no snapshot: {hash}.");
        var destination = Path.Combine(target, "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination);
        if (Store.HashFile(destination) != hash) throw new DomainError($"Objeto divergente: {hash}.");
    }

    private static bool IsInside(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
