using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using FastEnvSetter.Core;

namespace FastEnvSetter.Infrastructure;

public sealed record ProjectTaskRunSnapshot(Project Project, IReadOnlyList<EnvProfile> Profiles, string Fingerprint);

public sealed class ProjectDevelopmentActions(IAppDataStore store)
{
    private readonly Action _save = store.RequestSave;

    public Project GetProject(Guid projectId) => store.Data.Projects.FirstOrDefault(project => project.Id == projectId)
        ?? throw new InvalidOperationException("Das Projekt wurde entfernt. Bitte zur Projektliste zurückkehren.");

    public ProjectTaskRunSnapshot CaptureRun(Guid projectId)
    {
        var project = GetProject(projectId);
        var projectCopy = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(project))!;
        var profiles = JsonSerializer.Deserialize<List<EnvProfile>>(JsonSerializer.Serialize(store.Data.Profiles))!;
        foreach (var profile in profiles)
            foreach (var variable in profile.Variables)
                variable.DecryptionFailed = store.Data.Profiles.First(original => original.Id == profile.Id)
                    .Variables.First(original => original.Id == variable.Id).DecryptionFailed;
        return new ProjectTaskRunSnapshot(projectCopy, profiles, RunFingerprint(project, store.Data.Profiles));
    }

    public bool IsCurrent(ProjectTaskRunSnapshot snapshot) =>
        snapshot.Fingerprint == RunFingerprint(GetProject(snapshot.Project.Id), store.Data.Profiles);

    public Task<WorkflowRunResult> RunTaskAsync(ProjectTaskRunSnapshot snapshot, Guid selectedTaskId,
        IEnumerable<ISecretProvider> secretProviders, CancellationToken token = default, IWorkflowProcessExecutor? executor = null)
    {
        if (!IsCurrent(snapshot))
            throw new InvalidOperationException("Das Projekt oder seine Profile wurden inzwischen geändert. Bitte erneut prüfen.");
        return new ProjectTaskRunner(executor, secretProviders).RunAsync(snapshot.Project, snapshot.Profiles, selectedTaskId, token);
    }

    private static string RunFingerprint(Project project, IReadOnlyList<EnvProfile> profiles) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { project, profiles }))));

    public static ProjectTask CopyTask(ProjectTask task) => new()
    {
        Id = task.Id,
        Name = task.Name,
        Kind = task.Kind,
        FileName = task.FileName,
        Arguments = [.. task.Arguments],
        WorkingDirectory = task.WorkingDirectory,
        DependsOn = [.. task.DependsOn],
        TimeoutSeconds = task.TimeoutSeconds,
    };

    public void SaveTask(Guid projectId, ProjectTask draft, ProjectTask? expected)
    {
        var project = GetProject(projectId);
        var current = project.Tasks.FirstOrDefault(task => task.Id == draft.Id);
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected))
            throw new InvalidOperationException("Die Aufgabe wurde inzwischen geändert. Bitte neu auswählen.");
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("Bitte einen Namen für die Aufgabe eingeben.");
        var directory = EnvFileSources.ResolvePath(project.SourceRoot, draft.WorkingDirectory);
        if (!Directory.Exists(directory))
            throw new ArgumentException("Der Arbeitsordner muss im Quellordner liegen und existieren.");
        var replacement = CopyTask(draft);
        var tasks = project.Tasks.Where(task => task.Id != draft.Id).Append(replacement).ToArray();
        var plan = WorkflowGraph.Plan(tasks);
        if (plan.Errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, plan.Errors));
        if (current is null)
            project.Tasks.Add(replacement);
        else
            project.Tasks[project.Tasks.IndexOf(current)] = replacement;
        _save();
    }

    public void AddSuggestion(Guid projectId, ProjectTask selected, IReadOnlyList<ProjectTask> suggestions)
    {
        var project = GetProject(projectId);
        var plan = WorkflowGraph.Plan(suggestions, selected.Id);
        if (plan.Errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, plan.Errors));
        var additions = new List<ProjectTask>();
        var mapping = new Dictionary<Guid, Guid>();
        foreach (var task in plan.Tasks)
        {
            var copy = CopyTask(task);
            copy.DependsOn = task.DependsOn.Select(id => mapping[id]).ToList();
            var existing = project.Tasks.Concat(additions).FirstOrDefault(candidate =>
                candidate.Name == copy.Name && candidate.Kind == copy.Kind && candidate.FileName == copy.FileName
                && candidate.WorkingDirectory == copy.WorkingDirectory && candidate.TimeoutSeconds == copy.TimeoutSeconds
                && candidate.Arguments.SequenceEqual(copy.Arguments) && candidate.DependsOn.SequenceEqual(copy.DependsOn));
            mapping[task.Id] = existing?.Id ?? Guid.NewGuid();
            if (existing is not null)
                continue;
            copy.Id = mapping[task.Id];
            var directory = EnvFileSources.ResolvePath(project.SourceRoot, copy.WorkingDirectory);
            if (!Directory.Exists(directory))
                throw new ArgumentException("Ein vorgeschlagener Arbeitsordner existiert nicht mehr. Bitte erneut erkennen.");
            additions.Add(copy);
        }
        var validation = WorkflowGraph.Plan(project.Tasks.Concat(additions).ToArray());
        if (validation.Errors.Count > 0)
            throw new ArgumentException(string.Join(Environment.NewLine, validation.Errors));
        project.Tasks.AddRange(additions);
        _save();
    }

    public void RemoveTask(Guid projectId, Guid taskId)
    {
        var project = GetProject(projectId);
        if (project.Tasks.Any(task => task.DependsOn.Contains(taskId)))
            throw new InvalidOperationException("Andere Aufgaben benötigen diese Aufgabe. Zuerst deren Abhängigkeiten bearbeiten.");
        project.Tasks.RemoveAll(task => task.Id == taskId);
        _save();
    }

    public void AddEnvironmentFile(Guid projectId, string relativePath)
    {
        var project = GetProject(projectId);
        if (EnvFileSources.IsExample(relativePath))
            throw new ArgumentException("Example-Dateien dienen nur der Schlüsselvorschau und sind keine Laufzeitquelle.");
        var preview = EnvFileSources.Preview(project.SourceRoot, relativePath);
        var runtime = EnvFileSources.Load(new Project { SourceRoot = project.SourceRoot, EnvironmentFiles = [relativePath] });
        if (preview.SkippedLines.Count > 0 || runtime.Errors.Count > 0)
            throw new ArgumentException("Die Datei enthält ungültige Zeilen. Bitte vor dem Hinzufügen korrigieren.");
        relativePath = Path.GetRelativePath(project.SourceRoot, EnvFileSources.ResolvePath(project.SourceRoot, relativePath));
        if (!project.EnvironmentFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            project.EnvironmentFiles.Add(relativePath);
        _save();
    }

    public void RemoveEnvironmentFile(Guid projectId, string relativePath)
    {
        GetProject(projectId).EnvironmentFiles.Remove(relativePath);
        _save();
    }

    public static IReadOnlyList<string> DiscoverEnvironmentFiles(string sourceRoot)
    {
        var root = EnvFileSources.ResolvePath(sourceRoot, ".");
        var found = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        string[] ignored = [".git", "node_modules", ".venv", "venv", "bin", "obj", "target", "vendor", "publish"];
        while (queue.TryDequeue(out var current) && visited++ < 500)
        {
            foreach (var file in Directory.EnumerateFiles(current.Path).Where(IsEnvironmentCandidate).Order(StringComparer.OrdinalIgnoreCase))
                found.Add(Path.GetRelativePath(root, file));
            if (current.Depth >= 3)
                continue;
            foreach (var directory in Directory.EnumerateDirectories(current.Path).Order(StringComparer.OrdinalIgnoreCase))
                if (!ignored.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)
                    && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    queue.Enqueue((directory, current.Depth + 1));
        }
        return found;
    }

    private static bool IsEnvironmentCandidate(string file) =>
        Path.GetFileName(file).StartsWith(".env", StringComparison.OrdinalIgnoreCase)
        && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0;

    public static IReadOnlyList<string> SelectedGitPaths(GitStatusEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
            throw new ArgumentException("Bitte zuerst eine geänderte Datei auswählen.");
        return entry.OriginalPath is { Length: > 0 } original ? [entry.Path, original] : [entry.Path];
    }
}
