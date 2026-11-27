using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Orbita.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Orbita.Studio;

public sealed class MissionAttachments(StudioFiles files)
{
    public const int MaxCount = 6;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxTotalBytes = 10 * 1024 * 1024;
    private const int MaxText = 45_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] TextExtensions =
        [".txt", ".md", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml", ".log", ".sql", ".cs", ".py", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".css", ".html", ".rs", ".go", ".toml", ".ini", ".java"];

    public async Task<MissionAttachment[]> SaveAsync(string missionId, IFormFileCollection uploads, CancellationToken ct)
    {
        if (uploads.Count > MaxCount || uploads.Sum(file => file.Length) > MaxTotalBytes)
            throw new StudioException("Anexe até 6 arquivos, com até 10 MB no total.");
        var saved = new List<MissionAttachment>();
        try
        {
            foreach (var upload in uploads)
            {
                if (upload.Length is <= 0 or > MaxFileBytes)
                    throw new StudioException("Cada anexo deve conter dados e ter até 5 MB.");
                var name = Path.GetFileName(upload.FileName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.Any(char.IsControl))
                    throw new StudioException("Use um nome de arquivo de até 180 caracteres.");
                var extension = Path.GetExtension(name).ToLowerInvariant();
                var mimeType = ContentType(extension);
                var attachment = new MissionAttachment(StudioFiles.NewId(), name, mimeType, upload.Length);
                saved.Add(attachment);
                var directory = DirectoryFor(missionId, attachment.Id);
                Directory.CreateDirectory(directory);
                var path = OriginalPath(directory, attachment);
                await using (var output = File.Create(path)) await upload.CopyToAsync(output, ct);
                if (mimeType.StartsWith("image/", StringComparison.Ordinal)) ValidateImage(path, mimeType, name);
                else
                {
                    var text = ReadDocument(path, extension, name, ct);
                    var truncated = text.Length > MaxText;
                    if (truncated)
                    {
                        var end = char.IsHighSurrogate(text[MaxText - 1]) ? MaxText - 1 : MaxText;
                        text = text[..end] + "\n[O restante do documento foi omitido pelo limite de texto do anexo.]";
                    }
                    await File.WriteAllTextAsync(Path.Combine(directory, "texto.txt"), text, Utf8, ct);
                    attachment = attachment with { Truncated = truncated };
                    saved[^1] = attachment;
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "anexo.json"), JsonSerializer.Serialize(attachment, StudioFiles.Json), Utf8, ct);
            }
            return [.. saved];
        }
        catch
        {
            Discard(missionId, saved);
            throw;
        }
    }

    public (MissionAttachment Attachment, string Path) Get(string missionId, string attachmentId)
    {
        var directory = DirectoryFor(missionId, attachmentId);
        var metadata = Path.Combine(directory, "anexo.json");
        if (!File.Exists(metadata) || JsonSerializer.Deserialize<MissionAttachment>(File.ReadAllText(metadata), StudioFiles.Json) is not { } attachment)
            throw new StudioException("Anexo não encontrado nesta missão.");
        var path = OriginalPath(directory, attachment);
        if (!File.Exists(path)) throw new StudioException("O arquivo original do anexo não está disponível.");
        return (attachment, path);
    }

    public async Task<AgentAttachment[]> ReadAsync(string missionId, IReadOnlyList<MissionAttachment> attachments, CancellationToken ct)
    {
        var result = new AgentAttachment[attachments.Count];
        for (var index = 0; index < attachments.Count; index++)
        {
            var (attachment, path) = Get(missionId, attachments[index].Id);
            var textPath = Path.Combine(Path.GetDirectoryName(path)!, "texto.txt");
            var text = attachment.MimeType.StartsWith("image/", StringComparison.Ordinal) ? null : await File.ReadAllTextAsync(textPath, ct);
            result[index] = new AgentAttachment(attachment.Name, attachment.MimeType, path, text);
        }
        return result;
    }

    public void Discard(string missionId, IEnumerable<MissionAttachment> attachments)
    {
        foreach (var attachment in attachments)
        {
            var directory = DirectoryFor(missionId, attachment.Id);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private string DirectoryFor(string missionId, string attachmentId)
    {
        if (!Guid.TryParseExact(attachmentId, "N", out _)) throw new StudioException("Identificador de anexo inválido.");
        return Path.Combine(files.MissionPath(missionId), "anexos", attachmentId);
    }

    private static string OriginalPath(string directory, MissionAttachment attachment)
        => Path.Combine(directory, "original" + Path.GetExtension(attachment.Name).ToLowerInvariant());

    private static string ContentType(string extension) => extension switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ when TextExtensions.Contains(extension) => "text/plain",
        _ => throw new StudioException("Formato incompatível. Use PNG, JPG, WebP, GIF, PDF, DOCX, XLSX ou um arquivo de texto, planilha CSV ou código.")
    };

    private static void ValidateImage(string path, string mimeType, string name)
    {
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length)
        {
            bool valid = mimeType switch
            {
                "image/png" => header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "image/jpeg" => header[0] == 255 && header[1] == 216 && header[2] == 255,
                "image/webp" => header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8),
                "image/gif" => header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8),
                _ => false
            };
            if (valid) return;
        }
        throw new StudioException($"A imagem {name} está inválida ou usa outra extensão.");
    }

    private static string ReadDocument(string path, string extension, string name, CancellationToken ct)
    {
        try
        {
            var text = extension switch
            {
                ".pdf" => PdfText(path, ct),
                ".docx" => OfficeText(path, spreadsheet: false, ct),
                ".xlsx" => OfficeText(path, spreadsheet: true, ct),
                _ => PlainText(path)
            };
            if (!string.IsNullOrWhiteSpace(text) && !text.Contains('\0')) return text;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        { throw new StudioException($"Não foi possível ler {name}. Confira se o arquivo abre e está sem senha."); }
        throw new StudioException(extension == ".pdf"
            ? $"O PDF {name} não contém texto selecionável. Anexe uma imagem das páginas ou um PDF com texto pesquisável."
            : $"Não encontrei texto legível em {name}.");
    }

    private static string PlainText(string path)
    {
        using var reader = new StreamReader(path, Utf8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxText + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    private static string PdfText(string path, CancellationToken ct)
    {
        using var pdf = PdfDocument.Open(path);
        var text = new StringBuilder();
        for (var number = 1; number <= pdf.NumberOfPages && text.Length <= MaxText; number++)
        {
            ct.ThrowIfCancellationRequested();
            var page = ContentOrderTextExtractor.GetText(pdf.GetPage(number));
            if (!string.IsNullOrWhiteSpace(page)) text.Append("Página ").Append(number).AppendLine().AppendLine(page).AppendLine();
        }
        return text.ToString();
    }

    private static string OfficeText(string path, bool spreadsheet, CancellationToken ct)
    {
        using var file = ZipFile.OpenRead(path);
        if (spreadsheet) return SpreadsheetText(file, ct);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var document = ReadXml(file.GetEntry("word/document.xml") ?? throw new InvalidDataException());
        var text = new StringBuilder();
        foreach (var paragraph in document.Descendants(word + "p"))
        {
            ct.ThrowIfCancellationRequested();
            text.AppendLine(string.Concat(paragraph.Descendants(word + "t").Select(node => node.Value)));
            if (text.Length > MaxText) break;
        }
        return text.ToString();
    }

    private static string SpreadsheetText(ZipArchive file, CancellationToken ct)
    {
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
        var workbook = ReadXml(file.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException());
        var links = ReadXml(file.GetEntry("xl/_rels/workbook.xml.rels") ?? throw new InvalidDataException());
        var shared = file.GetEntry("xl/sharedStrings.xml") is { } entry
            ? ReadXml(entry).Descendants(sheet + "si").Select(item => string.Concat(item.Descendants(sheet + "t").Select(node => node.Value))).ToArray()
            : [];
        var text = new StringBuilder();
        foreach (var tab in workbook.Descendants(sheet + "sheet"))
        {
            var id = (string?)tab.Attribute(relationships + "id");
            var target = (string?)links.Descendants(package + "Relationship").FirstOrDefault(link => (string?)link.Attribute("Id") == id)?.Attribute("Target");
            var location = target?.StartsWith('/') == true ? target.TrimStart('/') : "xl/" + target;
            if (file.GetEntry(location) is not { } worksheet) continue;
            var heading = $"Planilha: {(string?)tab.Attribute("name")}";
            var hasRows = false;
            foreach (var row in ReadXml(worksheet).Descendants(sheet + "row"))
            {
                ct.ThrowIfCancellationRequested();
                var cells = row.Elements(sheet + "c").Select(cell =>
                {
                    var value = (string?)cell.Element(sheet + "v") ?? string.Concat(cell.Descendants(sheet + "t").Select(node => node.Value));
                    if ((string?)cell.Attribute("t") == "s" && int.TryParse(value, out var index) && index >= 0 && index < shared.Length)
                        value = shared[index];
                    return string.IsNullOrEmpty(value) ? "" : $"{(string?)cell.Attribute("r")}={value}";
                });
                var line = string.Join('\t', cells.Where(cell => cell.Length > 0));
                if (line.Length == 0) continue;
                if (!hasRows) { text.AppendLine(heading); hasRows = true; }
                text.AppendLine(line);
                if (text.Length > MaxText) return text.ToString();
            }
        }
        return text.ToString();
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > 4_000_000) throw new InvalidDataException();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 4_000_000 });
        return XDocument.Load(reader);
    }
}
