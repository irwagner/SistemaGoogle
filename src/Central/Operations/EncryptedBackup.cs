using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Central.Domain;

namespace Central.Operations;

public static class EncryptedBackup
{
    private static readonly byte[] Magic = "CENTRAL2"u8.ToArray();
    private const int ChunkSize = 1024 * 1024;
    private const int KdfIterations = 600_000;

    public static void Create(Store store, string destination, string password)
    {
        CheckPassword(password);
        var result = Path.GetFullPath(destination);
        Store.RejectLinks(result);
        if (Path.Exists(result)) throw new DomainError("Escolha um arquivo novo para o backup.");
        var temporary = result + "." + Store.Id();
        var scratch = Scratch();
        Directory.CreateDirectory(scratch);
        try
        {
            var snapshot = Path.Combine(scratch, "snapshot");
            var zip = Path.Combine(scratch, "origem.zip");
            Backup.Create(store, snapshot);
            ZipFile.CreateFromDirectory(snapshot, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
            using (var input = File.OpenRead(zip))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Encrypt(input, output, password);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, result);
        }
        finally
        {
            File.Delete(temporary);
            Directory.Delete(scratch, recursive: true);
        }
    }

    public static void Restore(string inputPath, string destination, string password)
    {
        CheckPassword(password);
        var source = Path.GetFullPath(inputPath);
        Store.RejectLinks(source);
        var scratch = Scratch();
        Directory.CreateDirectory(scratch);
        try
        {
            var zip = Path.Combine(scratch, "autenticado.zip");
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Decrypt(input, output, password);
            var snapshot = Path.Combine(scratch, "snapshot");
            Directory.CreateDirectory(snapshot);
            Extract(zip, snapshot);
            Backup.Restore(snapshot, destination);
        }
        catch (Exception error) when (error is CryptographicException or EndOfStreamException or InvalidDataException)
        { throw new DomainError("Senha, formato ou integridade do backup inválidos."); }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    public static void Encrypt(Stream input, Stream output, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            output.Write(Magic);
            output.Write(salt);
            using var aes = new AesGcm(key, 16);
            var plain = new byte[ChunkSize];
            var cipher = new byte[ChunkSize];
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            Span<byte> size = stackalloc byte[4];
            Span<byte> tag = stackalloc byte[16];
            for (ulong index = 0; ; index++)
            {
                var count = input.ReadAtLeast(plain, ChunkSize, false);
                BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
                BinaryPrimitives.WriteInt32BigEndian(size, count);
                aes.Encrypt(nonce, plain.AsSpan(0, count), cipher.AsSpan(0, count), tag, size);
                output.Write(size);
                output.Write(cipher.AsSpan(0, count));
                output.Write(tag);
                if (count == 0) break;
            }
            CryptographicOperations.ZeroMemory(plain);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static void Decrypt(Stream input, Stream output, string password)
    {
        Span<byte> header = stackalloc byte[Magic.Length];
        input.ReadExactly(header);
        if (!header.SequenceEqual(Magic)) throw new InvalidDataException("Header inválido.");
        var salt = new byte[16];
        input.ReadExactly(salt);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            using var aes = new AesGcm(key, 16);
            var plain = new byte[ChunkSize];
            var cipher = new byte[ChunkSize];
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            Span<byte> size = stackalloc byte[4];
            Span<byte> tag = stackalloc byte[16];
            for (ulong index = 0; ; index++)
            {
                input.ReadExactly(size);
                var count = BinaryPrimitives.ReadInt32BigEndian(size);
                if (count is < 0 or > ChunkSize) throw new InvalidDataException("Trecho inválido.");
                input.ReadExactly(cipher.AsSpan(0, count));
                input.ReadExactly(tag);
                BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], index);
                aes.Decrypt(nonce, cipher.AsSpan(0, count), tag, plain.AsSpan(0, count), size);
                if (count == 0) break;
                output.Write(plain.AsSpan(0, count));
            }
            CryptographicOperations.ZeroMemory(plain);
            if (input.ReadByte() != -1) throw new InvalidDataException("Dados após o marcador final.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void Extract(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var drive = new DriveInfo(Path.GetPathRoot(destination)!);
        var limit = drive.AvailableFreeSpace - 128 * 1024 * 1024;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var isStandard = name is "central.sqlite" or "snapshot.json";
            if (!isStandard)
            {
                var parts = name.Split('/');
                if (parts.Length != 3 || parts[0] != "objects" || parts[2].Length != 64 ||
                    !parts[2].All(char.IsAsciiHexDigitLower) || parts[1] != parts[2][..2])
                    throw new InvalidDataException("Caminho não reconhecido no snapshot.");
            }
            if (!seen.Add(name) || entry.Length > limit - total) throw new DomainError("O backup não cabe no armazenamento temporário ou tem entradas repetidas.");
            total += entry.Length;
            var path = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    private static void CheckPassword(string password)
    {
        if (password.Length is < 12 or > 512) throw new DomainError("Use uma senha de backup entre 12 e 512 caracteres.");
    }
    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "CentralCifrado-" + Store.Id());
        Store.RejectLinks(path);
        return path;
    }
}
