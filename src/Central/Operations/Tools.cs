using System.Diagnostics;
using System.Runtime.InteropServices;
using Central.Domain;

namespace Central.Operations;

public sealed record ToolResult(int ExitCode, string Output, string Error);

public static class Tools
{
    private static readonly string[] Names = ["git", "ffmpeg", "ffprobe", "agy"];

    public static string? Find(string name)
    {
        if (!Names.Contains(name)) throw new DomainError("Ferramenta desconhecida.");
        var configured = Environment.GetEnvironmentVariable("CENTRAL_" + name.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathFullyQualified(configured) && File.Exists(configured) ? configured : null;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim('"')).Where(Path.IsPathFullyQualified)
            .Select(path => Path.Combine(path, name + ".exe")).FirstOrDefault(File.Exists);
    }

    public static async Task<ToolResult> Run(string tool, IEnumerable<string> args, string workingDirectory, CancellationToken token = default)
    {
        var executable = Find(tool) ?? throw new DomainError($"Instale ou configure {tool} no Windows.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        var allowed = new HashSet<string>(["SystemRoot", "WINDIR", "TEMP", "TMP", "PATH", "COMSPEC"], StringComparer.OrdinalIgnoreCase);
        foreach (var variable in start.Environment.Keys.Where(key => !allowed.Contains(key)).ToArray()) start.Environment.Remove(variable);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start) ?? throw new DomainError("Falha ao iniciar o processo local.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        var output = ReadBounded(process.StandardOutput);
        var error = ReadBounded(process.StandardError);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (token.IsCancellationRequested) throw;
            throw new DomainError("A ferramenta ultrapassou o prazo local.");
        }
        return new ToolResult(process.ExitCode, await output, await error);
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        var buffer = new char[8192];
        var retained = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
            retained.Append(buffer, 0, Math.Min(count, Math.Max(0, 512 * 1024 - retained.Length)));
        return retained.ToString();
    }

    public static async Task<string> Diagnostics(Store store)
    {
        var info = new List<string>
        {
            $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}",
            $"Central {typeof(Tools).Assembly.GetName().Version?.ToString(3)}",
            $".NET {Environment.Version}",
            $"SQLite {store.Read(db => db.Scalar<string>("SELECT sqlite_version()"))}",
            $"Schema {store.Read(db => db.Scalar<long>("SELECT MAX(version) FROM migrations"))}",
            $"Armazenamento disponível {new DriveInfo(Path.GetPathRoot(store.Root)!).AvailableFreeSpace / 1024 / 1024} MiB"
        };
        foreach (var tool in Names)
        {
            var path = Find(tool);
            if (path == null) { info.Add($"{tool}: indisponível"); continue; }
            if (tool == "agy") { info.Add("agy: executável localizado; identidade e conta pendentes"); continue; }
            var check = await Run(tool, [tool == "git" ? "--version" : "-version"], store.Staging);
            info.Add($"{tool}: {(check.ExitCode == 0 ? check.Output.Split('\n')[0].Trim() : "verificação pendente")}");
        }
        return string.Join(Environment.NewLine, info);
    }
}
