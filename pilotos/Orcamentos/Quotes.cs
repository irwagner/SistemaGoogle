using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

internal sealed record Quote(long Id, string Client, string Item, int Quantity, long Unit)
{
    public long Total => checked(Quantity * Unit);
}

internal sealed partial class Quotes
{
    private readonly string connection;

    public Quotes(string path)
    {
        connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS quotes(
                id INTEGER PRIMARY KEY, request TEXT UNIQUE NOT NULL, client TEXT NOT NULL, item TEXT NOT NULL,
                quantity INTEGER NOT NULL CHECK(quantity>0), unit INTEGER NOT NULL CHECK(unit>0), created TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public Quote Save(string request, string client, string item, string quantity, string unit)
    {
        client = Validate(client, "cliente");
        item = Validate(item, "item");
        if (!Guid.TryParseExact(request, "N", out _)) throw new ArgumentException("Reabra o formulário.");
        if (!int.TryParse(quantity, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 100000)
            throw new ArgumentException("Informe uma quantidade inteira entre 1 e 100.000.");
        if (!Price().IsMatch(unit) || !decimal.TryParse(unit, NumberStyles.AllowDecimalPoint, CultureInfo.GetCultureInfo("pt-BR"), out var amount) || amount <= 0)
            throw new ArgumentException("Informe o valor unitário positivo em reais, como 47,90.");
        var cents = checked((long)(100 * amount));
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO quotes(request, client, item, quantity, unit, created)
            VALUES($key,$client,$item,$count,$unit,$date) ON CONFLICT(request) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$key", request);
        cmd.Parameters.AddWithValue("$client", client);
        cmd.Parameters.AddWithValue("$item", item);
        cmd.Parameters.AddWithValue("$count", count);
        cmd.Parameters.AddWithValue("$unit", cents);
        cmd.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT id,client,item,quantity,unit FROM quotes WHERE request=$key";
        Quote quote;
        using (var rows = cmd.ExecuteReader()) { rows.Read(); quote = Read(rows); }
        if (quote.Client != client || quote.Item != item || quote.Quantity != count || quote.Unit != cents)
            throw new ArgumentException("Reabra o formulário para criar outro orçamento.");
        transaction.Commit();
        return quote;
    }

    public Quote? Get(long id) => Select(id).SingleOrDefault();
    public IReadOnlyList<Quote> All() => Select(null);
    private List<Quote> Select(long? id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id,client,item,quantity,unit FROM quotes WHERE $id IS NULL OR id=$id ORDER BY id DESC";
        cmd.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        using var rows = cmd.ExecuteReader();
        var list = new List<Quote>();
        while (rows.Read()) list.Add(Read(rows));
        return list;
    }
    private static Quote Read(SqliteDataReader row) => new(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetInt32(3), row.GetInt64(4));
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connection);
        db.Open();
        return db;
    }
    private static string Validate(string text, string label)
    {
        text = text.Trim();
        if (text.Length is < 1 or > 80 || text.Any(c => char.IsControl(c) || c > 255))
            throw new ArgumentException($"Informe {label} com até 80 caracteres compatíveis com a impressão.");
        return text;
    }
    [GeneratedRegex(@"^\d{1,7}(,\d{1,2})?$", RegexOptions.CultureInvariant)]
    private static partial Regex Price();
}
