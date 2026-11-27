using System.Diagnostics;
using System.Text;
using Orbita.Core;

namespace Orbita.Engine;

public sealed class GitSnapshotService(StudioFiles files) : IGitSnapshotService
{
    private const string DefaultIgnore = ".env\n.env.*\n!.env.example\nnode_modules/\nbin/\nobj/\n";
    private readonly string? git = LocalTools.Locate("git");
    public bool Available => git != null;

    public async Task<Mission> CreateWorktreeAsync(StudioProject project, string id, string workspace, string name, CancellationToken ct)
    {
        if (!Directory.Exists(project.Workspace)) throw new StudioException("A pasta original do projeto está indisponível.");
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.Workspace));
        var existing = await Command(folder, ct, "rev-parse", "--show-toplevel");
        var isRepositoryRoot = existing.Code == 0 && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing.Output.Trim())), folder,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var repository = folder;
        string baseline;
        if (!isRepositoryRoot && !project.Managed)
        {
            repository = files.LinkedProjectRepository(project.Id);
            baseline = await LinkedFolderBaselineAsync(folder, repository, id, ct);
        }
        else
        {
            if (!isRepositoryRoot)
            {
                await Require(folder, ct, "init", "--initial-branch=main");
                var ignore = Path.Combine(folder, ".gitignore");
                if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, DefaultIgnore, ct);
                await Require(folder, ct, "add", "--", ".gitignore");
                await Require(folder, ct, "commit", "--no-gpg-sign", "--allow-empty", "-m", "Criar projeto no Orbita Studio");
            }
            var head = await Command(folder, ct, "rev-parse", "--verify", "--quiet", "HEAD^{commit}");
            baseline = head.Code == 0 ? head.Output.TrimEnd() : await InitialBaselineAsync(folder, id, ct);
        }
        var branch = $"orbita/{id}";
        Directory.CreateDirectory(Path.GetDirectoryName(workspace)!);
        await Require(repository, ct, "worktree", "add", "-b", branch, workspace, baseline);
        return new Mission(id, project.Id, name, workspace, branch, baseline, DateTimeOffset.UtcNow, Revision: baseline);
    }

    private async Task<string> LinkedFolderBaselineAsync(string folder, string repository, string id, CancellationToken ct)
    {
        var dataPath = Path.GetRelativePath(folder, files.DataRoot);
        var projectPath = Path.GetRelativePath(files.Root, folder);
        if (dataPath == "." || projectPath == "." || IsChildPath(projectPath))
            throw new StudioException("Escolha uma pasta de trabalho fora do diretório interno do Orbita.");

        Directory.CreateDirectory(repository);
        if (!File.Exists(Path.Combine(repository, "HEAD")))
            await Require(repository, ct, "init", "--bare", "--template=", "--initial-branch=main");
        var info = Directory.CreateDirectory(Path.Combine(repository, "info"));
        var exclude = Path.Combine(info.FullName, "exclude");
        if (!File.Exists(exclude)) await File.WriteAllTextAsync(exclude, DefaultIgnore, ct);

        var index = Path.Combine(repository, $"orbita-{id}.index");
        try
        {
            var gitDir = $"--git-dir={repository}";
            var workTree = $"--work-tree={folder}";
            string[] add = [gitDir, workTree, "add", "-A", "--", "."];
            if (IsChildPath(dataPath)) add = [.. add, $":(top,exclude,literal){dataPath.Replace('\\', '/')}"];
            _ = Unwrap(await CommandWithIndex(folder, ct, index, add), "add");
            var tree = Unwrap(await CommandWithIndex(folder, ct, index, gitDir, workTree, "write-tree"), "write-tree");
            return await Require(repository, ct, "commit-tree", tree, "--no-gpg-sign", "-m", "Base da pasta vinculada no Orbita Studio");
        }
        finally
        {
            File.Delete(index);
            File.Delete(index + ".lock");
        }
    }

    private static bool IsChildPath(string relative)
        => !Path.IsPathRooted(relative) && relative is not ("." or "..") &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private async Task<string> InitialBaselineAsync(string folder, string id, CancellationToken ct)
    {
        var head = await Command(folder, ct, "symbolic-ref", "--quiet", "HEAD");
        if (head.Code != 0 || (await Command(folder, ct, "show-ref", "--verify", "--quiet", head.Output.Trim())).Code != 1)
            throw new StudioException("O Git não conseguiu ler a revisão atual do projeto.");

        var index = await Require(folder, ct, "rev-parse", "--path-format=absolute", "--git-path", "index");
        var snapshot = Path.Combine(Path.GetDirectoryName(index)!, $"orbita-{id}.index");
        try
        {
            if (File.Exists(index)) File.Copy(index, snapshot);
            var tree = Unwrap(await CommandWithIndex(folder, ct, snapshot, "write-tree"), "write-tree");
            return await Require(folder, ct, "commit-tree", tree, "--no-gpg-sign", "-m", "Base inicial da missão no Orbita Studio");
        }
        finally
        {
            File.Delete(snapshot);
            File.Delete(snapshot + ".lock");
        }
    }

    public async Task<string> CaptureAsync(Mission mission, string message, CancellationToken ct)
    {
        await Require(mission.Workspace, ct, "add", "-A", "--", ".");
        var staged = await Command(mission.Workspace, ct, "diff", "--cached", "--quiet", "--");
        if (staged.Code == 1) await Require(mission.Workspace, ct, "commit", "--no-gpg-sign", "-m", message);
        else if (staged.Code != 0) throw new StudioException("O Git não conseguiu preparar o snapshot.");
        return await Require(mission.Workspace, ct, "rev-parse", "HEAD");
    }

    public async Task<GitChanges> DiffAsync(Mission mission, CancellationToken ct)
    {
        var diff = await Require(mission.Workspace, ct, "-c", "core.quotePath=false", "diff", "--no-ext-diff", "--no-textconv", mission.Baseline, "--");
        var files = await Require(mission.Workspace, ct, "-c", "core.quotePath=false", "status", "--short", "--untracked-files=all");
        var revisions = await Require(mission.Workspace, ct, "rev-parse", "HEAD");
        return new GitChanges(files, diff.Length > 220_000 ? diff[..220_000] + "\n[Prévia limitada a 220 mil caracteres.]" : diff, revisions);
    }

    public async Task<string> RestoreAsync(Mission mission, CancellationToken ct)
    {
        await CaptureAsync(mission, "Salvar estado antes de reverter", ct);
        await Require(mission.Workspace, ct, "restore", "--source", mission.Baseline, "--staged", "--worktree", "--", ":/");
        return await CaptureAsync(mission, "Reverter missão ao ponto inicial", ct);
    }

    private async Task<string> Require(string workspace, CancellationToken ct, params string[] args)
        => Unwrap(await Command(workspace, ct, args), args[0]);

    private static string Unwrap((int Code, string Output, string Error) result, string command)
    {
        if (result.Code == 0) return result.Output.TrimEnd();
        var error = result.Error.Trim();
        throw new StudioException($"Git ({command}) falhou. {error[..Math.Min(error.Length, 600)]}");
    }

    private Task<(int Code, string Output, string Error)> Command(string workspace, CancellationToken ct, params string[] args)
        => CommandWithIndex(workspace, ct, null, args);

    private async Task<(int Code, string Output, string Error)> CommandWithIndex(string workspace, CancellationToken ct, string? index, params string[] args)
    {
        if (git == null) throw new StudioException("Instale o Git para criar worktrees e snapshots.");
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR" }) start.Environment.Remove(key);
        if (index != null) start.Environment["GIT_INDEX_FILE"] = index;
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "-c", "user.name=Orbita Studio", "-c", "user.email=orbita@localhost", "-c", "core.hooksPath=/dev/null" }
            .Concat(args)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new StudioException("Não foi possível iniciar o Git.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }
}
