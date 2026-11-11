using System.Diagnostics;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed class EditorLauncher
{
    private const string CodeExecutableName = "Code.exe";

    public ProcessStartInfo BuildStart(Project project, EditorTarget target, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(project.SourceRoot) || !Directory.Exists(project.SourceRoot))
            throw new DirectoryNotFoundException("Quellordner fehlt. Im Projekt einen vorhandenen Quellordner zuordnen.");

        var root = Path.GetFullPath(project.SourceRoot);
        var path = ProjectTransfer.ResolvePath(root, target.Path);
        if (!Directory.Exists(path) && !(path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) && File.Exists(path)))
            throw new FileNotFoundException("Editorziel fehlt oder ist keine .code-workspace-Datei. Ziel im Projekt korrigieren.", path);

        var executable = executablePath ?? FindExecutable();
        if (!File.Exists(executable) || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("VS Code fehlt. VS Code installieren oder den Pfad zu Code.exe angeben.", executable);

        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = root };
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        start.ArgumentList.Add("--new-window");
        if (!string.IsNullOrWhiteSpace(target.Profile))
        {
            start.ArgumentList.Add("--profile");
            start.ArgumentList.Add(target.Profile);
        }
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);
        return start;
    }

    public void Open(Project project, EditorTarget target, string? executablePath = null)
    {
        using var process = Process.Start(BuildStart(project, target, executablePath));
        if (process is null)
            throw new IOException("VS Code konnte nicht gestartet werden.");
    }

    private static string FindExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", CodeExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", CodeExecutableName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft VS Code", CodeExecutableName),
        };
        return candidates.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(entry => !string.IsNullOrWhiteSpace(entry))
                .SelectMany(entry => new[] { Path.Combine(entry.Trim('"'), CodeExecutableName), Path.Combine(entry.Trim('"'), "..", CodeExecutableName) }))
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("VS Code wurde nicht gefunden. Microsoft VS Code installieren und erneut versuchen.");
    }
}
