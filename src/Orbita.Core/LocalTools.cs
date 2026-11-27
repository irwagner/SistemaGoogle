namespace Orbita.Core;

public static class LocalTools
{
    public static string? Locate(string program)
    {
        var name = OperatingSystem.IsWindows() ? program + ".exe" : program;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = folder.Trim().Trim('"');
            if (Path.IsPathFullyQualified(clean) && File.Exists(Path.Combine(clean, name))) return Path.Combine(clean, name);
        }
        return null;
    }

    public static string? Agy() => Configured("ORBITA_AGY_PATH") ?? Locate("agy") ??
        Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe"));

    public static string? Configured(string key) => Existing(Environment.GetEnvironmentVariable(key));
    private static string? Existing(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && File.Exists(path)
        ? path : null;
}
