using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Central.Domain;

public sealed class DomainError(string message) : Exception(message);
public sealed record Project(string Id, string Name, string Kind, string Classification);
public sealed record Work(string Id, string Title, string State, int Version, string Policy, string? Reason);
public sealed record Asset(string Id, string Task, string Name, string Type, string Hash, long Size, string Origin, string Rights);
public sealed record Checkpoint(long Seq, string Task, int Version, string Next, string Details, string Created);
public sealed record Entry(long Seq, string Kind, string Detail, string Created);

public sealed class Store
{
    private readonly string connectionString;
    private readonly object writer = new();
    public const long FileLimit = 256L * 1024 * 1024;
    public string Root { get; }
    public string Staging => Path.Combine(Root, "staging");
    public TimeProvider Clock { get; }
    public string Now => Clock.GetUtcNow().ToString("O");
    public long Millis => Clock.GetUtcNow().ToUnixTimeMilliseconds();

    public Store(string root, TimeProvider? clock = null)
    {
        Root = Path.GetFullPath(root);
        Clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(Root);
        RejectLinks(Root);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Path.Combine(Root, "objects"));
        RejectLinks(Staging);
        RejectLinks(Path.Combine(Root, "objects"));
        var database = Path.Combine(Root, "central.sqlite");
        RejectLinks(database);
        connectionString = new SqliteConnectionStringBuilder { DataSource = database, ForeignKeys = true, Pooling = false }.ToString();
        using var connection = Open();
        connection.Exec("PRAGMA journal_mode=WAL");
        connection.Exec("CREATE TABLE IF NOT EXISTS migrations(version INTEGER PRIMARY KEY, applied TEXT NOT NULL)");
        var assembly = typeof(Store).Assembly;
        var migrations = assembly.GetManifestResourceNames().Where(n => n.StartsWith("Central.Migrations.", StringComparison.Ordinal)).Order().ToArray();
        if (connection.Scalar<long>("SELECT COALESCE(MAX(version),0) FROM migrations") > migrations.Length)
            throw new DomainError("Este banco requer uma versão mais recente da Central.");
        foreach (var name in migrations)
        {
            var version = int.Parse(name.Split('.')[2], CultureInfo.InvariantCulture);
            using var transaction = connection.BeginTransaction();
            if (connection.Scalar<long>("SELECT COUNT(*) FROM migrations WHERE version=$v", version) == 0)
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
                connection.Exec(reader.ReadToEnd());
                connection.Exec("INSERT INTO migrations VALUES($v,$date)", version, Now);
            }
            transaction.Commit();
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        connection.Exec("PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;");
        return connection;
    }

    public T Read<T>(Func<SqliteConnection, T> operation)
    {
        using var database = Open();
        return operation(database);
    }

    public T Write<T>(Func<SqliteConnection, T> operation)
    {
        lock (writer)
        {
            using var database = Open();
            using var transaction = database.BeginTransaction();
            var result = operation(database);
            transaction.Commit();
            return result;
        }
    }

    public void Write(Action<SqliteConnection> operation) => Write(db => { operation(db); return true; });
    public static string Id() => Guid.NewGuid().ToString("N");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }
    public static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static string Text(string? value, int max, string label)
    {
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > max)
            throw new DomainError($"Informe {label} com até {max} caracteres.");
        return value;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new DomainError("O caminho contém um link ou ponto de reanálise.");
    }

    public string ObjectPath(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new DomainError("Hash inválido.");
        var path = Path.Combine(Root, "objects", hash[..2], hash);
        RejectLinks(path);
        return path;
    }

    public IReadOnlyList<Project> Projects() => Read(db => db.List("SELECT id,name,kind,classification FROM projects ORDER BY created DESC", MapProject));
    public Project GetProject(string id) => Read(db => db.One("SELECT id,name,kind,classification FROM projects WHERE id=$id", MapProject, id))
        ?? throw new DomainError("Projeto não encontrado.");
    private static Project MapProject(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3));

    public string CreateProject(string? name, string? kind, string? classification, string? key)
    {
        name = Text(name, 100, "o nome");
        if (kind is not ("software" or "estudio" or "pesquisa") || classification is not ("publico" or "interno" or "restrito"))
            throw new DomainError("Tipo ou classificação inválidos.");
        return Write(db => Once(db, "", key, Json(new { name, kind, classification }), () =>
        {
            var id = Id();
            db.Exec("INSERT INTO projects VALUES($id,$name,$kind,$class,$date)", id, name, kind, classification, Now);
            Event(db, id, "projeto.criado", kind);
            return id;
        }));
    }

    public IReadOnlyList<Work> Tasks(string project) => Read(db => db.List("""
        SELECT id,title,state,version,policy,reason FROM tasks WHERE project=$p ORDER BY created,id
        """, MapWork, project));
    private static Work MapWork(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
        row.GetInt32(3), row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5));
    internal static Work Task(SqliteConnection db, string project, string id) =>
        db.One("SELECT id,title,state,version,policy,reason FROM tasks WHERE project=$p AND id=$id", MapWork, project, id)
        ?? throw new DomainError("Tarefa não encontrada no projeto.");

    public string CreateTask(string project, string? title, IEnumerable<string> predecessors, string policy = "local")
    {
        GetProject(project);
        var name = Text(title, 150, "o título");
        if (policy is not ("local" or "fixa" or "assistida")) throw new DomainError("Política inválida.");
        var dependencies = predecessors.Distinct().ToArray();
        return Write(db =>
        {
            var id = Id();
            foreach (var dependency in dependencies) Task(db, project, dependency);
            var ready = dependencies.All(dependency => Task(db, project, dependency).State == "aceita");
            db.Exec("INSERT INTO tasks(id,project,title,state,policy,created) VALUES($id,$p,$name,$state,$policy,$date)",
                id, project, name, ready ? "pronta" : "dependencias", policy, Now);
            foreach (var dependency in dependencies)
                db.Exec("INSERT INTO dependencies VALUES($p,$task,$predecessor)", project, id, dependency);
            SaveCheckpoint(db, project, id, ready ? "Executar a tarefa." : "Aguardar dependências.", "{}");
            Event(db, project, "tarefa.criada", id);
            return id;
        });
    }

    public void Advance(string project, string id, int version, string action, string? note)
    {
        var details = Text(note, 6000, "a decisão e a próxima ação");
        Write(db =>
        {
            var task = Task(db, project, id);
            if (task.Version != version) throw new DomainError("A tarefa mudou. Reabra a página.");
            if (db.Scalar<long>("SELECT COUNT(*) FROM effects WHERE task=$t AND state IN ('reserved','sending','ambiguous','accepted','identity_mismatch')", id) != 0)
                throw new DomainError("Reconcilie o efeito em aberto antes de alterar a tarefa.");
            var next = (task.State, action) switch
            {
                ("pronta", "pausar") or ("execucao", "pausar") or ("revisao", "pausar") => "pausada",
                ("pausada", "retomar") => "pronta",
                ("pronta", "revisar") or ("execucao", "revisar") => "revisao",
                ("revisao", "aceitar") => "aceita",
                ("revisao", "reabrir") => "pronta",
                ("pronta", "cancelar") or ("pausada", "cancelar") => "cancelada",
                _ => throw new DomainError("Transição indisponível.")
            };
            db.Exec("UPDATE tasks SET state=$s,version=version+1,reason=NULL WHERE id=$id", next, id);
            SaveCheckpoint(db, project, id, details, "{}");
            Event(db, project, "tarefa." + next, id);
            if (next == "aceita") Unblock(db, project);
        });
    }

    public void SaveCheckpoint(SqliteConnection db, string project, string task, string next, string details) =>
        db.Exec("INSERT INTO checkpoints(project,task,version,next_action,details,created) VALUES($p,$t,(SELECT version FROM tasks WHERE id=$t),$n,$d,$date)",
            project, task, next, details, Now);

    public void Event(SqliteConnection db, string project, string kind, string detail) =>
        db.Exec("INSERT INTO events(project,kind,detail,created) VALUES($p,$k,$d,$date)", project, kind, detail, Now);

    public static void Unblock(SqliteConnection db, string project) => db.Exec("""
        UPDATE tasks SET state='pronta',version=version+1 WHERE project=$p AND state='dependencias'
        AND NOT EXISTS (SELECT 1 FROM dependencies d JOIN tasks t ON t.id=d.predecessor
                        WHERE d.task=tasks.id AND t.state<>'aceita')
        """, project);

    public IReadOnlyList<Checkpoint> Checkpoints(string project) => Read(db => db.List("""
        SELECT seq,task,version,next_action,details,created FROM checkpoints WHERE project=$p ORDER BY seq DESC
        """, r => new Checkpoint(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5)), project));
    public IReadOnlyList<Entry> Events(string project, long after = 0) => Read(db => db.List("""
        SELECT seq,kind,detail,created FROM events WHERE project=$p AND seq>$after ORDER BY seq LIMIT 200
        """, r => new Entry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)), project, after));

    public async Task<Asset> Import(string project, string? task, string fileName, Stream input, string? origin, string? rights,
        string? key, CancellationToken token = default)
    {
        GetProject(project);
        var filename = Text(Path.GetFileName(fileName.Replace('\\', '/')), 150, "o nome do arquivo");
        if (filename.Any(char.IsControl) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || filename is "." or "..")
            throw new DomainError("Nome de arquivo inválido.");
        var type = Path.GetExtension(filename).ToLowerInvariant();
        if (type is not (".txt" or ".md" or ".json" or ".csv" or ".srt" or ".pdf" or ".png" or ".jpg" or ".webp" or ".mp4" or ".mov" or ".webm" or ".wav" or ".mp3" or ".bundle" or ".patch" or ".zip"))
            throw new DomainError("Extensão indisponível para importação.");
        origin = Text(origin, 160, "a origem");
        rights = Text(rights, 250, "a permissão de uso");
        var stage = Path.Combine(Staging, Id());
        try
        {
            long size = 0;
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
                {
                    size += count;
                    if (size > FileLimit) throw new DomainError("O arquivo excede 256 MiB.");
                    sha.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            }
            if (size == 0) throw new DomainError("O arquivo está vazio.");
            ValidateFormat(stage, type);
            if (HashFile(stage) != hash) throw new DomainError("O arquivo mudou no staging.");
            var path = ObjectPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try { File.Move(stage, path); }
            catch (IOException) when (File.Exists(path))
            {
                if (new FileInfo(path).Length != size || HashFile(path) != hash)
                    throw new DomainError("O objeto existente falhou na verificação.");
            }
            var id = Write(db => Once(db, project, key, Json(new { task, filename, hash, origin, rights }), () =>
            {
                var target = task;
                if (string.IsNullOrEmpty(target))
                {
                    target = Id();
                    db.Exec("INSERT INTO tasks(id,project,title,state,created) VALUES($id,$p,$title,'aceita',$date)",
                        target, project, "Validar " + filename, Now);
                }
                else if (Task(db, project, target).State is not ("pronta" or "execucao" or "revisao"))
                    throw new DomainError("Retome a tarefa e confirme as dependências antes de importar.");
                var artifact = Id();
                db.Exec("INSERT OR IGNORE INTO blobs VALUES($hash,$size)", hash, size);
                db.Exec("INSERT INTO artifacts VALUES($id,$p,$t,$n,$type,$hash,$origin,$rights,$date)",
                    artifact, project, target, filename, type, hash, origin, rights, Now);
                SaveCheckpoint(db, project, target, "Conferir a versão importada e prosseguir conforme o aceite.", Json(new { artifact, hash }));
                Event(db, project, "arquivo.validado", hash);
                return artifact;
            }));
            return GetAsset(project, id);
        }
        finally { File.Delete(stage); }
    }

    private static void ValidateFormat(string path, string type)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        var length = stream.Read(header);
        var valid = type switch
        {
            ".pdf" => header[..length].StartsWith("%PDF-"u8),
            ".png" => length >= 8 && header[0] == 137 && header[1..4].SequenceEqual("PNG"u8),
            ".jpg" => length >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255,
            ".mp4" or ".mov" => length >= 12 && header[4..8].SequenceEqual("ftyp"u8),
            ".wav" => length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),
            ".zip" => header[..length].StartsWith("PK"u8),
            ".webm" => length >= 4 && header[0] == 0x1a && header[1] == 0x45 && header[2] == 0xdf && header[3] == 0xa3,
            ".webp" => length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
            _ => true
        };
        if (!valid) throw new DomainError("O conteúdo não corresponde à extensão.");
        if (type is not (".txt" or ".md" or ".json" or ".csv" or ".srt" or ".patch")) return;
        stream.Position = 0;
        try
        {
            if (type == ".json") { using var json = JsonDocument.Parse(stream); return; }
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
            var chars = new char[8192];
            int count;
            while ((count = reader.Read(chars)) != 0)
                if (chars.AsSpan(0, count).Contains('\0')) throw new DomainError("O texto contém dados binários.");
        }
        catch (Exception e) when (e is JsonException or System.Text.DecoderFallbackException)
        { throw new DomainError("O arquivo de texto ou JSON é inválido."); }
    }

    public IReadOnlyList<Asset> Assets(string project) => Read(db => db.List(AssetQuery + " WHERE a.project=$p ORDER BY a.created DESC", MapAsset, project));
    private const string AssetQuery = """
        SELECT a.id,a.task,a.name,a.type,a.hash,b.size,a.origin,a.rights FROM artifacts a JOIN blobs b ON b.hash=a.hash
        """;
    public Asset GetAsset(string project, string id) => Read(db => AssetIn(db, project, id));
    internal static Asset AssetIn(SqliteConnection db, string project, string id) =>
        db.One(AssetQuery + " WHERE a.project=$p AND a.id=$id", MapAsset, project, id)
        ?? throw new DomainError("Arquivo não encontrado no projeto.");
    private static Asset MapAsset(SqliteDataReader row) => new(row.GetString(0), row.GetString(1), row.GetString(2),
        row.GetString(3), row.GetString(4), row.GetInt64(5), row.GetString(6), row.GetString(7));

    public string Verify(Asset asset)
    {
        var path = ObjectPath(asset.Hash);
        if (!File.Exists(path) || new FileInfo(path).Length != asset.Size || HashFile(path) != asset.Hash)
            throw new DomainError("Arquivo ausente ou com hash divergente.");
        return path;
    }

    private static string Once(SqliteConnection db, string project, string? key, string payload, Func<string> action)
    {
        if (string.IsNullOrWhiteSpace(key)) return action();
        key = Text(key, 100, "a chave da solicitação");
        var fingerprint = Hash(System.Text.Encoding.UTF8.GetBytes(payload));
        var previous = db.One("SELECT fingerprint,result FROM requests WHERE project=$p AND key=$k",
            r => Tuple.Create(r.GetString(0), r.GetString(1)), project, key);
        if (previous != null)
            return previous.Item1 == fingerprint ? previous.Item2 : throw new DomainError("A chave já foi usada com outra entrada.");
        var result = action();
        db.Exec("INSERT INTO requests VALUES($p,$key,$hash,$result)", project, key, fingerprint, result);
        return result;
    }
}

internal static partial class Sql
{
    [System.Text.RegularExpressions.GeneratedRegex(@"\$[A-Za-z_][A-Za-z_0-9]*")]
    private static partial System.Text.RegularExpressions.Regex Parameters();

    private static SqliteCommand Command(this SqliteConnection db, string sql, object?[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        var names = Parameters().Matches(sql).Select(match => match.Value).Distinct().ToArray();
        if (names.Length != values.Length)
        {
            command.Dispose();
            throw new InvalidOperationException("Quantidade de parâmetros SQL divergente.");
        }
        for (var i = 0; i < names.Length; i++) command.Parameters.AddWithValue(names[i], values[i] ?? DBNull.Value);
        return command;
    }
    internal static int Exec(this SqliteConnection db, string sql, params object?[] values)
    {
        using var command = db.Command(sql, values);
        return command.ExecuteNonQuery();
    }
    internal static T? Scalar<T>(this SqliteConnection db, string sql, params object?[] values)
    {
        using var command = db.Command(sql, values);
        var result = command.ExecuteScalar();
        return result is null or DBNull ? default : (T)result;
    }
    internal static List<T> List<T>(this SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] values)
    {
        using var command = db.Command(sql, values);
        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read()) results.Add(map(reader));
        return results;
    }
    internal static T? One<T>(this SqliteConnection db, string sql, Func<SqliteDataReader, T> map, params object?[] values) where T : class =>
        db.List(sql, map, values).SingleOrDefault();
    internal static string? NullableText(this SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);
    internal static long? NullableLong(this SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetInt64(i);
}
