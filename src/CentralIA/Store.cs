using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CentralIA;

/// <summary>Armazenamento local em JSON: um diretório por projeto, com workspace, conversas e storyboards.</summary>
public sealed partial class Store
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Root { get; }
    private readonly ConcurrentDictionary<string, ChatThread> threads = new();
    private readonly ConcurrentDictionary<string, Storyboard> flows = new();
    private readonly object projectGate = new();

    public Store(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(ProjectsDir);
    }

    public static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("CENTRAL_DATA");
        return string.IsNullOrWhiteSpace(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CentralIA")
            : env;
    }

    public string ProjectsDir => Path.Combine(Root, "projetos");
    public static string NewId() => DateTime.UtcNow.ToString("yyMMddHHmmss") + Guid.NewGuid().ToString("N")[..6];

    [GeneratedRegex("^[a-z0-9]{6,40}$")]
    private static partial Regex IdPattern();
    public static string CheckId(string id) => IdPattern().IsMatch(id ?? "") ? id! : throw new UserError("Identificador inválido.");

    public string ProjectDir(string pid) => Path.Combine(ProjectsDir, CheckId(pid));
    public string Workspace(string pid)
    {
        var p = GetProject(pid);
        if (!string.IsNullOrWhiteSpace(p?.CustomPath))
        {
            var custom = Path.GetFullPath(p.CustomPath);
            if (Directory.Exists(custom)) return custom;
            throw new UserError("A pasta original do projeto está indisponível. Restaure o acesso antes de continuar.");
        }
        var dir = Path.Combine(ProjectDir(pid), "workspace");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static T? ReadFile<T>(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default;

    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    // ---------- Projetos ----------

    private string ProjectFile(string pid) => Path.Combine(ProjectDir(pid), "projeto.json");

    public List<Project> Projects() =>
        Directory.EnumerateDirectories(ProjectsDir)
            .Select(d => Path.Combine(d, "projeto.json"))
            .Where(File.Exists)
            .Select(p => { try { return ReadFile<Project>(p); } catch { return null; } })
            .Where(p => p != null)
            .Select(p => p!)
            .OrderByDescending(p => p.Updated)
            .ToList();

    public Project? GetProject(string pid) => ReadFile<Project>(ProjectFile(pid));

    public Project CreateProject(string name, string kind, string? customPath = null)
    {
        string? resolved = null;
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            resolved = Path.GetFullPath(customPath.Trim().Trim('"'));
            if (!Directory.Exists(resolved)) throw new UserError("A pasta informada não existe: " + resolved);
        }
        var p = new Project { Id = NewId(), Name = Clean(name, 80, "Novo projeto"), Kind = kind, CustomPath = resolved, Created = DateTime.UtcNow, Updated = DateTime.UtcNow };
        if (resolved == null) Workspace(p.Id);
        WriteFile(ProjectFile(p.Id), p);
        return p;
    }

    public void UpdateProject(string pid, string? name, string? customPath, bool hasCustomPath)
    {
        lock (projectGate)
        {
            var p = GetProject(pid) ?? throw new UserError("Projeto não encontrado.");
            if (!string.IsNullOrWhiteSpace(name)) p.Name = Clean(name, 80, p.Name);
            if (hasCustomPath)
            {
                if (string.IsNullOrWhiteSpace(customPath)) p.CustomPath = null;
                else
                {
                    var full = Path.GetFullPath(customPath.Trim().Trim('"'));
                    if (!Directory.Exists(full)) throw new UserError("A pasta informada não existe: " + full);
                    p.CustomPath = full;
                }
            }
            WriteFile(ProjectFile(pid), p);
        }
    }

    public void TouchProject(string pid)
    {
        lock (projectGate)
        {
            var p = GetProject(pid);
            if (p == null) return;
            p.Updated = DateTime.UtcNow;
            WriteFile(ProjectFile(pid), p);
        }
    }

    public void TrashProject(string pid)
    {
        var dir = ProjectDir(pid);
        if (!Directory.Exists(dir)) return;
        foreach (var key in threads.Where(t => t.Value.ProjectId == pid).Select(t => t.Key).ToList()) threads.TryRemove(key, out _);
        foreach (var key in flows.Where(t => t.Value.ProjectId == pid).Select(t => t.Key).ToList()) flows.TryRemove(key, out _);
        var trash = Path.Combine(Root, "lixeira");
        Directory.CreateDirectory(trash);
        Directory.Move(dir, Path.Combine(trash, pid + "-" + DateTime.Now.ToString("yyyyMMddHHmmss")));
    }

    public static string Clean(string? text, int max, string fallback)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return fallback;
        return t.Length > max ? t[..max] : t;
    }

    // ---------- Conversas ----------

    private string ThreadPath(string pid, string tid) => Path.Combine(ProjectDir(pid), "conversas", CheckId(tid) + ".json");

    public ChatThread CreateThread(string pid, string title, string mode)
    {
        if (GetProject(pid) == null) throw new UserError("Projeto não encontrado.");
        var t = new ChatThread { Id = NewId(), ProjectId = pid, Title = Clean(title, 100, "Nova conversa"), Mode = mode, Created = DateTime.UtcNow, Updated = DateTime.UtcNow };
        threads[t.Id] = t;
        WriteFile(ThreadPath(pid, t.Id), t);
        TouchProject(pid);
        return t;
    }

    public ChatThread? GetThread(string pid, string tid)
    {
        if (threads.TryGetValue(CheckId(tid), out var cached)) return cached.ProjectId == pid ? cached : null;
        var t = ReadFile<ChatThread>(ThreadPath(pid, tid));
        if (t == null) return null;
        t.ProjectId = pid;
        return threads.GetOrAdd(tid, t);
    }

    public List<ChatThread> Threads(string pid)
    {
        var dir = Path.Combine(ProjectDir(pid), "conversas");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateFiles(dir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(id => IdPattern().IsMatch(id))
            .Select(id => { try { return GetThread(pid, id); } catch { return null; } })
            .Where(t => t != null).Select(t => t!).ToList();
    }

    public void Mutate(ChatThread t, Action<ChatThread> change)
    {
        lock (t)
        {
            change(t);
            t.Updated = DateTime.UtcNow;
            WriteFile(ThreadPath(t.ProjectId, t.Id), t);
        }
    }

    public string Serialize(ChatThread t) { lock (t) return JsonSerializer.Serialize(t, Json); }

    public void TrashThread(string pid, string tid)
    {
        var path = ThreadPath(pid, tid);
        threads.TryRemove(tid, out _);
        if (!File.Exists(path)) return;
        var trash = Path.Combine(Root, "lixeira");
        Directory.CreateDirectory(trash);
        File.Move(path, Path.Combine(trash, $"{pid}-{tid}-{DateTime.Now:yyyyMMddHHmmss}.json"));
    }

    // ---------- Storyboards (Flow) ----------

    public string FlowDir(string pid, string fid) => Path.Combine(ProjectDir(pid), "flows", CheckId(fid));
    private string FlowPath(string pid, string fid) => Path.Combine(FlowDir(pid, fid), "storyboard.json");

    public void SaveNewFlow(Storyboard f)
    {
        flows[f.Id] = f;
        lock (f) WriteFile(FlowPath(f.ProjectId, f.Id), f);
        TouchProject(f.ProjectId);
    }

    public Storyboard? GetFlow(string pid, string fid)
    {
        if (flows.TryGetValue(CheckId(fid), out var cached)) return cached.ProjectId == pid ? cached : null;
        var f = ReadFile<Storyboard>(FlowPath(pid, fid));
        if (f == null) return null;
        f.ProjectId = pid;
        return flows.GetOrAdd(fid, f);
    }

    public List<Storyboard> Flows(string pid)
    {
        var dir = Path.Combine(ProjectDir(pid), "flows");
        if (!Directory.Exists(dir)) return new();
        return Directory.EnumerateDirectories(dir)
            .Select(d => Path.GetFileName(d))
            .Where(id => IdPattern().IsMatch(id))
            .Select(id => { try { return GetFlow(pid, id); } catch { return null; } })
            .Where(f => f != null).Select(f => f!).ToList();
    }

    public void MutateFlow(Storyboard f, Action<Storyboard> change)
    {
        lock (f)
        {
            change(f);
            f.Updated = DateTime.UtcNow;
            WriteFile(FlowPath(f.ProjectId, f.Id), f);
        }
    }

    public string SerializeFlow(Storyboard f) { lock (f) return JsonSerializer.Serialize(f, Json); }

    public void TrashFlow(string pid, string fid)
    {
        var dir = FlowDir(pid, fid);
        flows.TryRemove(fid, out _);
        if (!Directory.Exists(dir)) return;
        var trash = Path.Combine(Root, "lixeira");
        Directory.CreateDirectory(trash);
        Directory.Move(dir, Path.Combine(trash, $"{pid}-{fid}-{DateTime.Now:yyyyMMddHHmmss}"));
    }

    // ---------- Workspace ----------

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "__pycache__", ".venv", "venv", ".next", ".cache" };

    public static List<(string Path, long Size)> ListFiles(string root, int max = 600)
    {
        var result = new List<(string, long)>();
        if (!Directory.Exists(root)) return result;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && result.Count < max)
        {
            var dir = stack.Pop();
            IEnumerable<string> files, dirs;
            try { files = Directory.EnumerateFiles(dir).OrderBy(f => f).ToList(); dirs = Directory.EnumerateDirectories(dir).OrderByDescending(d => d).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                if (result.Count >= max) break;
                try
                {
                    var info = new FileInfo(f);
                    if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                        result.Add((Path.GetRelativePath(root, f).Replace('\\', '/'), info.Length));
                }
                catch (IOException) { }
            }
            foreach (var d in dirs)
            {
                if (SkipDirs.Contains(Path.GetFileName(d))) continue;
                try { if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) stack.Push(d); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return result.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resolve um caminho relativo garantindo que fique dentro da pasta raiz.</summary>
    public static string Inside(string root, string? relative)
    {
        var rel = (relative ?? "").Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(root, rel));
        var baseDir = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!(full + Path.DirectorySeparatorChar).StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            throw new UserError("Caminho fora da pasta permitida: " + rel);
        return full;
    }
}

/// <summary>Cofre de segredos com DPAPI do usuário Windows (chaves de API nunca vão para o JSON).</summary>
public sealed class Vault(string root)
{
    private readonly string dir = Path.Combine(root, "cofre");

    private string PathFor(string key) =>
        Path.Combine(dir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".dpapi");

    public void Set(string key, string? value)
    {
        var path = PathFor(key);
        if (string.IsNullOrWhiteSpace(value)) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Transform(Encoding.UTF8.GetBytes(value.Trim()), true));
    }

    public string? Get(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try { return Encoding.UTF8.GetString(Transform(File.ReadAllBytes(path), false)); }
        catch { return null; }
    }

    public bool Has(string key) => File.Exists(PathFor(key));

    private static byte[] Transform(byte[] data, bool encrypt)
    {
        var input = new DataBlob { Count = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        DataBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            var ok = encrypt
                ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new UserError("O cofre do Windows não está disponível para este usuário.");
            var bytes = new byte[output.Count];
            Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob data, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob result);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class SettingsService
{
    public static readonly string[] Roles = ["conversa", "codigo", "roteiro"];
    public static readonly string[] StrategyRoles = ["conversa", "codigo", "roteiro", "midia"];
    private readonly string path;
    private readonly Vault vault;
    private readonly object gate = new();
    public AppSettings Current { get; private set; }

    public SettingsService(Store store)
    {
        path = Path.Combine(store.Root, "config.json");
        vault = new Vault(store.Root);
        AppSettings? loaded = null;
        try { loaded = Store.ReadFile<AppSettings>(path); } catch { }
        Current = loaded ?? new AppSettings();
        foreach (var r in Roles) Current.Chains.TryAdd(r, new());
        foreach (var r in StrategyRoles) Current.Strategies.TryAdd(r, r == "midia" ? "roundrobin" : "failover");
    }

    public string StrategyFor(string role) =>
        Current.Strategies.TryGetValue(role, out var s) && s is "failover" or "roundrobin" or "leastused" ? s : "failover";

    public void Update(Action<AppSettings> change)
    {
        lock (gate)
        {
            change(Current);
            foreach (var r in Roles) Current.Chains.TryAdd(r, new());
            foreach (var r in StrategyRoles) Current.Strategies.TryAdd(r, r == "midia" ? "roundrobin" : "failover");
            var ids = Current.Providers.Select(p => p.Id).ToHashSet();
            foreach (var c in Current.Chains.Values) c.RemoveAll(r => !ids.Contains(r.ProviderId) || string.IsNullOrWhiteSpace(r.Model));
            Store.WriteFile(path, Current);
        }
    }

    public string? GetKey(string providerId) => vault.Get("provider:" + providerId);
    public void SetKey(string providerId, string? key) => vault.Set("provider:" + providerId, key);
    public bool HasKey(string providerId) => vault.Has("provider:" + providerId);

    public object Snapshot()
    {
        lock (gate)
        {
            foreach (var p in Current.Providers) p.HasKey = HasKey(p.Id);
            return JsonSerializer.SerializeToNode(Current, Store.Json)!;
        }
    }

    public List<(RouteEntry Route, ProviderConfig Provider)> ResolveChain(string role)
    {
        var s = Current;
        var enabled = s.Providers.Where(CanRoute).ToDictionary(p => p.Id);
        List<RouteEntry> chain =
            s.Chains.TryGetValue(role, out var c) && c.Count > 0 ? c
            : s.Chains.TryGetValue("conversa", out var d) && d.Count > 0 ? d
            : enabled.Values.Where(p => p.DefaultModel.Length > 0)
                .Select(p => new RouteEntry { ProviderId = p.Id, Model = p.DefaultModel }).ToList();
        return chain.Where(r => enabled.ContainsKey(r.ProviderId) && !string.IsNullOrWhiteSpace(r.Model))
            .Select(r => (r, enabled[r.ProviderId])).ToList();
    }

    public static bool CanRoute(ProviderConfig p) => p.Enabled && p.Kind is not ("google-personal" or "ensaio") &&
        !(p.Kind == "cli" && p.BaseUrl is ("cli://agy" or "cli://gemini"));
}
