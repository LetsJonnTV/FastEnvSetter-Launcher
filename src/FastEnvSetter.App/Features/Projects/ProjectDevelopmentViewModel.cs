using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using Microsoft.Win32;

namespace FastEnvSetter.App;

public sealed partial class ProjectDevelopmentViewModel(IAppDataStore store, SecretSources sources) : ObservableObject
{
    private readonly ProjectDevelopmentActions _actions = new(store);
    private Guid _projectId;
    private int _generation;
    private CancellationTokenSource? _operation;
    private ProjectTask? _expectedTask;
    private Guid _draftId;
    private HashSet<Guid> _draftDependencies = [];
    private GitService? _git;
    private GitCommitReview? _review;

    public ObservableCollection<ProjectTask> Tasks { get; } = [];
    public ObservableCollection<ProjectTask> Suggestions { get; } = [];
    public ObservableCollection<IdCheck> TaskDependencies { get; } = [];
    public ObservableCollection<string> EnvironmentFiles { get; } = [];
    public ObservableCollection<string> EnvironmentSuggestions { get; } = [];
    public ObservableCollection<GitStatusEntry> GitFiles { get; } = [];
    public ObservableCollection<GitRevision> GitHistory { get; } = [];
    public IReadOnlyList<ProjectTaskKind> TaskKinds { get; } = Enum.GetValues<ProjectTaskKind>();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private ProjectTask? _selectedTask;
    [ObservableProperty] private ProjectTask? _selectedSuggestion;
    [ObservableProperty] private string _taskName = "";
    [ObservableProperty] private ProjectTaskKind _taskKind = ProjectTaskKind.Test;
    [ObservableProperty] private string _taskFileName = "";
    [ObservableProperty] private string _taskArguments = "";
    [ObservableProperty] private string _taskWorkingDirectory = ".";
    [ObservableProperty] private int _taskTimeout = 600;
    [ObservableProperty] private string _taskOutput = "";
    [ObservableProperty] private string? _selectedEnvironmentFile;
    [ObservableProperty] private string? _selectedEnvironmentSuggestion;
    [ObservableProperty] private string _environmentPath = "";
    [ObservableProperty] private string _environmentPreview = "";
    [ObservableProperty] private GitStatusEntry? _selectedGitFile;
    [ObservableProperty] private bool _showStagedDiff;
    [ObservableProperty] private string _gitDiff = "";
    [ObservableProperty] private string _commitMessage = "";
    [ObservableProperty] private string _reviewFingerprint = "";
    [ObservableProperty] private bool _reviewAccepted;

    public bool IsIdle => !IsBusy;
    public bool HasTaskDraft => _draftId != Guid.Empty;
    public bool CanAddEnvironment => !string.IsNullOrWhiteSpace(EnvironmentPath) && !EnvFileSources.IsExample(EnvironmentPath);
    public string SuggestionPreview => SelectedSuggestion is null ? "" : string.Join(Environment.NewLine,
        WorkflowGraph.Plan(Suggestions.ToArray(), SelectedSuggestion.Id).Tasks.Select(task =>
            $"{task.Name} [{task.Kind}] {task.FileName} {JsonSerializer.Serialize(task.Arguments)} ({task.WorkingDirectory}, {task.TimeoutSeconds}s)"));
    public bool CanCommit => IsIdle && _review is not null && ReviewAccepted && !string.IsNullOrWhiteSpace(CommitMessage);

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanCommit));
    }

    partial void OnReviewAcceptedChanged(bool value) => OnPropertyChanged(nameof(CanCommit));
    partial void OnSelectedSuggestionChanged(ProjectTask? value) => OnPropertyChanged(nameof(SuggestionPreview));
    partial void OnCommitMessageChanged(string value) => OnPropertyChanged(nameof(CanCommit));
    partial void OnSelectedGitFileChanged(GitStatusEntry? value) => InvalidateReview();
    partial void OnShowStagedDiffChanged(bool value) => InvalidateReview();
    partial void OnSelectedEnvironmentSuggestionChanged(string? value)
    {
        if (value is not null)
            EnvironmentPath = value;
    }
    partial void OnSelectedEnvironmentFileChanged(string? value)
    {
        if (value is not null)
            EnvironmentPath = value;
    }
    partial void OnEnvironmentPathChanged(string value)
    {
        EnvironmentPreview = "";
        OnPropertyChanged(nameof(CanAddEnvironment));
    }

    public void Load(Guid projectId)
    {
        Stop();
        _projectId = projectId;
        _git = null;
        InvalidateReview();
        Error = "";
        Status = "";
        TaskOutput = "";
        EnvironmentPath = "";
        EnvironmentPreview = "";
        Suggestions.Clear();
        EnvironmentSuggestions.Clear();
        GitFiles.Clear();
        GitHistory.Clear();
        CommitMessage = "";
        ReloadTasks();
        ReloadEnvironmentFiles();
        ClearTaskDraft();
    }

    public void Stop()
    {
        _generation++;
        _operation?.Cancel();
        IsBusy = false;
    }

    private Project Current => _actions.GetProject(_projectId);

    private void ReloadTasks()
    {
        SelectedTask = null;
        Tasks.Clear();
        foreach (var task in Current.Tasks)
            Tasks.Add(ProjectDevelopmentActions.CopyTask(task));
    }

    private void ReloadEnvironmentFiles()
    {
        EnvironmentFiles.Clear();
        foreach (var path in Current.EnvironmentFiles)
            EnvironmentFiles.Add(path);
    }

    partial void OnSelectedTaskChanged(ProjectTask? value)
    {
        if (value is null)
        {
            ClearTaskDraft();
            return;
        }
        _expectedTask = ProjectDevelopmentActions.CopyTask(value);
        SetTaskDraft(value);
    }

    private void ClearTaskDraft()
    {
        _expectedTask = null;
        _draftId = Guid.Empty;
        TaskDependencies.Clear();
        OnPropertyChanged(nameof(HasTaskDraft));
    }

    private void SetTaskDraft(ProjectTask task)
    {
        _draftId = task.Id;
        TaskName = task.Name;
        TaskKind = task.Kind;
        TaskFileName = task.FileName;
        TaskArguments = JsonSerializer.Serialize(task.Arguments);
        TaskWorkingDirectory = task.WorkingDirectory;
        TaskTimeout = task.TimeoutSeconds;
        var selected = task.DependsOn.ToHashSet();
        _draftDependencies = selected;
        TaskDependencies.Clear();
        foreach (var other in Tasks.Where(other => other.Id != task.Id))
        {
            var id = other.Id;
            TaskDependencies.Add(new IdCheck(other.Name, "", false, () => selected.Contains(id), enabled =>
            {
                if (enabled) selected.Add(id); else selected.Remove(id);
            }));
        }
        OnPropertyChanged(nameof(HasTaskDraft));
    }

    [RelayCommand]
    private void NewTask()
    {
        if (IsBusy) return;
        SelectedTask = null;
        _expectedTask = null;
        SetTaskDraft(new ProjectTask { Name = "Neue Aufgabe", Kind = ProjectTaskKind.Test });
    }

    [RelayCommand]
    private Task SaveTaskAsync() => PerformAsync(_ =>
    {
        if (!HasTaskDraft) throw new ArgumentException("Bitte eine Aufgabe auswählen oder anlegen.");
        var arguments = JsonSerializer.Deserialize<List<string>>(TaskArguments);
        if (arguments is null || arguments.Any(argument => argument is null))
            throw new ArgumentException("Argumente müssen ein JSON-Array aus Zeichenketten sein, z. B. [\"test\"].");
        var draft = new ProjectTask
        {
            Id = _draftId,
            Name = TaskName.Trim(),
            Kind = TaskKind,
            FileName = TaskFileName.Trim(),
            Arguments = arguments,
            WorkingDirectory = TaskWorkingDirectory.Trim(),
            TimeoutSeconds = TaskTimeout,
            DependsOn = _draftDependencies.ToList(),
        };
        _actions.SaveTask(_projectId, draft, _expectedTask);
        ReloadTasks();
        SelectedTask = Tasks.First(task => task.Id == draft.Id);
        Status = "Aufgabe gespeichert.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RemoveTaskAsync() => PerformAsync(_ =>
    {
        if (SelectedTask is null) throw new ArgumentException("Bitte eine Aufgabe auswählen.");
        _actions.RemoveTask(_projectId, SelectedTask.Id);
        ReloadTasks();
        Status = "Aufgabe entfernt.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task DiscoverTasksAsync() => PerformAsync(async token =>
    {
        var root = Current.SourceRoot;
        var result = await Task.Run(() => WorkflowDiscovery.Discover(root), token);
        token.ThrowIfCancellationRequested();
        Suggestions.Clear();
        foreach (var task in result.Tasks) Suggestions.Add(task);
        Status = $"{Suggestions.Count} Vorschläge. Hinzufügen übernimmt auch benötigte Abhängigkeiten.";
        Error = string.Join(Environment.NewLine, result.Warnings);
    });

    [RelayCommand]
    private Task AddSuggestionAsync() => PerformAsync(_ =>
    {
        if (SelectedSuggestion is null) throw new ArgumentException("Bitte einen Vorschlag auswählen.");
        _actions.AddSuggestion(_projectId, SelectedSuggestion, Suggestions.ToArray());
        ReloadTasks();
        Status = "Vorschlag mit Abhängigkeiten übernommen; es wurde nichts ausgeführt.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RunTaskAsync() => PerformAsync(async token =>
    {
        if (SelectedTask is null) throw new ArgumentException("Bitte eine gespeicherte Aufgabe auswählen.");
        var snapshot = _actions.CaptureRun(_projectId);
        var project = snapshot.Project;
        var selectedId = SelectedTask.Id;
        var generation = _generation;
        var plan = WorkflowGraph.Plan(project.Tasks, selectedId);
        if (plan.Errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, plan.Errors));
        var commands = string.Join(Environment.NewLine, plan.Tasks.Select(task =>
            $"{task.Name}: {task.FileName} {JsonSerializer.Serialize(task.Arguments)} ({task.WorkingDirectory}, {task.TimeoutSeconds}s)"));
        if (!await Dialogs.ConfirmAsync("Aufgabe ausführen", commands + Environment.NewLine +
            "Die gespeicherten Befehle einschließlich Abhängigkeiten laufen lokal mit deinen Rechten.", "Ausführen")) return;
        token.ThrowIfCancellationRequested();
        if (!_actions.IsCurrent(snapshot))
            throw new InvalidOperationException("Das Projekt wurde inzwischen geändert. Bitte erneut prüfen.");
        Status = "Aufgabe läuft; maskierte Logs folgen nach Abschluss. Abbruch beendet den Prozessbaum.";
        TaskOutput = "";
        var result = await _actions.RunTaskAsync(snapshot, selectedId, sources.Providers, token);
        if (generation != _generation) return;
        TaskOutput = FormatTaskResults(result, plan);
        Error = string.Join(Environment.NewLine, result.Errors);
        Status = result.Success ? "Aufgabe erfolgreich abgeschlossen." : "Aufgabe nicht erfolgreich. Ergebnisse prüfen.";
    });

    private static string FormatTaskResults(WorkflowRunResult result, WorkflowPlan plan) =>
        string.Join(Environment.NewLine, result.Tasks.SelectMany(task =>
            new[] { $"{plan.Tasks.FirstOrDefault(item => item.Id == task.TaskId)?.Name}: {task.Status} (Exit: {task.ExitCode?.ToString() ?? "-"}) {task.Message}" }
                .Concat(task.Logs.Select(log => $"{log.Timestamp:HH:mm:ss} {(log.IsError ? "stderr" : "stdout")}: {log.Text}"))));

    [RelayCommand]
    private void Cancel() => _operation?.Cancel();

    [RelayCommand]
    private Task DiscoverEnvironmentAsync() => PerformAsync(async token =>
    {
        var root = Current.SourceRoot;
        var paths = await Task.Run(() => ProjectDevelopmentActions.DiscoverEnvironmentFiles(root), token);
        token.ThrowIfCancellationRequested();
        EnvironmentSuggestions.Clear();
        foreach (var path in paths) EnvironmentSuggestions.Add(path);
        Status = $"{paths.Count} Dateien gefunden (max. 3 Ebenen / 500 Ordner). Keine Quelle wurde hinzugefügt.";
    });

    [RelayCommand]
    private Task ChooseEnvironmentAsync() => PerformAsync(_ =>
    {
        var dialog = new OpenFileDialog { Title = "Env-Datei im Quellordner wählen", Filter = "Alle Dateien|*.*" };
        if (Directory.Exists(Current.SourceRoot)) dialog.InitialDirectory = Current.SourceRoot;
        if (dialog.ShowDialog() == true)
        {
            var relative = Path.GetRelativePath(Current.SourceRoot, dialog.FileName);
            EnvFileSources.ResolvePath(Current.SourceRoot, relative);
            EnvironmentPath = relative;
        }
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task PreviewEnvironmentAsync() => PerformAsync(_ =>
    {
        var preview = EnvFileSources.Preview(Current.SourceRoot, EnvironmentPath);
        EnvironmentPreview = (EnvFileSources.IsExample(EnvironmentPath) ? "Example: nur erwartete Schlüssel" : "Schlüsselvorschau ohne Werte")
            + Environment.NewLine + string.Join(Environment.NewLine, preview.Variables.Select(variable => variable.Name))
            + (preview.SkippedLines.Count == 0 ? "" : Environment.NewLine + "Ungültige Zeilen: " + string.Join(", ", preview.SkippedLines));
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task AddEnvironmentAsync() => PerformAsync(_ =>
    {
        _actions.AddEnvironmentFile(_projectId, EnvironmentPath);
        ReloadEnvironmentFiles();
        Status = "Laufzeitquelle gespeichert. Sie gilt erst für den nächsten Start.";
        return Task.CompletedTask;
    });

    [RelayCommand]
    private Task RemoveEnvironmentAsync() => PerformAsync(_ =>
    {
        if (SelectedEnvironmentFile is null) throw new ArgumentException("Bitte eine gespeicherte Laufzeitquelle auswählen.");
        _actions.RemoveEnvironmentFile(_projectId, SelectedEnvironmentFile);
        ReloadEnvironmentFiles();
        Status = "Laufzeitquelle entfernt; die Datei bleibt unverändert.";
        return Task.CompletedTask;
    });

    private async Task<GitService> OpenGitAsync(CancellationToken token)
    {
        var root = Current.SourceRoot;
        if (_git is null || !string.Equals(_git.Repository.SourceRoot, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            _git = await GitService.OpenAsync(root, cancellationToken: token);
        return _git;
    }

    private void InvalidateReview()
    {
        _review = null;
        ReviewAccepted = false;
        ReviewFingerprint = "";
        GitDiff = "";
        OnPropertyChanged(nameof(CanCommit));
    }

    [RelayCommand]
    private Task RefreshGitAsync() => PerformAsync(async token =>
    {
        InvalidateReview();
        var git = await OpenGitAsync(token);
        await LoadGitStatusAsync(git, token);
        Status = $"{GitFiles.Count} geänderte Dateien.";
    });

    private async Task LoadGitStatusAsync(GitService git, CancellationToken token)
    {
        var files = await git.GetStatusAsync(token);
        token.ThrowIfCancellationRequested();
        GitFiles.Clear();
        foreach (var file in files) GitFiles.Add(file);
    }

    [RelayCommand]
    private Task GitHistoryAsync() => PerformAsync(async token =>
    {
        var history = await (await OpenGitAsync(token)).GetHistoryAsync(30, token);
        token.ThrowIfCancellationRequested();
        GitHistory.Clear();
        foreach (var revision in history) GitHistory.Add(revision);
        Status = "Letzte 30 Commits geladen.";
    });

    [RelayCommand]
    private Task ShowDiffAsync() => PerformAsync(async token =>
    {
        InvalidateReview();
        var git = await OpenGitAsync(token);
        var paths = ProjectDevelopmentActions.SelectedGitPaths(SelectedGitFile);
        var arguments = new List<string> { "--literal-pathspecs", "diff" };
        if (ShowStagedDiff) arguments.Add("--cached");
        arguments.AddRange(["--no-ext-diff", "--no-textconv", "--", .. paths]);
        var result = await new GitNativeCommandExecutor(maximumOutputCharacters: 65536).ExecuteAsync(
            new GitCommand("git", git.Repository.SourceRoot, arguments, true), token);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException("Diff konnte nicht gelesen werden. Git-Status erneut laden.");
        GitDiff = result.StandardOutput.Length == 0 ? "Kein Diff (nicht versionierte Dateien haben noch keinen Git-Diff)." : result.StandardOutput;
    });

    [RelayCommand]
    private Task StageAsync() => ChangeIndexAsync(true);

    [RelayCommand]
    private Task UnstageAsync() => ChangeIndexAsync(false);

    private Task ChangeIndexAsync(bool stage) => PerformAsync(async token =>
    {
        var paths = ProjectDevelopmentActions.SelectedGitPaths(SelectedGitFile);
        InvalidateReview();
        var git = await OpenGitAsync(token);
        if (stage) await git.StageAsync(paths, token); else await git.UnstageAsync(paths, token);
        await LoadGitStatusAsync(git, token);
        Status = stage ? "Ausgewählter Pfad im Index." : "Ausgewählter Pfad aus dem Index entfernt.";
    });

    [RelayCommand]
    private Task ReviewCommitAsync() => PerformAsync(async token =>
    {
        InvalidateReview();
        var review = await (await OpenGitAsync(token)).ReviewCommitAsync(token);
        token.ThrowIfCancellationRequested();
        if (review.StagedDiff.Length > 65536)
            throw new InvalidOperationException("Der Index-Diff ist für die vollständige Prüfung zu groß (64 KiB). Bitte den Index verkleinern oder extern prüfen und committen.");
        _review = review;
        GitDiff = review.StagedDiff;
        ReviewFingerprint = review.Fingerprint;
        Status = "Gesamten Index-Diff prüfen und anschließend bestätigen. Der Commit enthält alle geprüften Indexänderungen.";
    });

    [RelayCommand]
    private Task CommitAsync() => PerformAsync(async token =>
    {
        if (_review is null || !ReviewAccepted || string.IsNullOrWhiteSpace(CommitMessage))
            throw new ArgumentException("Zuerst den gesamten Index prüfen, bestätigen und eine Commit-Nachricht eingeben.");
        var review = _review;
        var git = await OpenGitAsync(token);
        if (!await Dialogs.ConfirmAsync("Geprüften Index committen", CommitMessage + Environment.NewLine +
            "Fingerprint: " + review.Fingerprint, "Committen")) return;
        token.ThrowIfCancellationRequested();
        InvalidateReview();
        var sha = await git.CommitAsync(review, CommitMessage.Trim(), token);
        CommitMessage = "";
        await LoadGitStatusAsync(git, token);
        Status = "Commit erstellt: " + sha;
    });

    private async Task PerformAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var generation = _generation;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        Error = "";
        try
        {
            await action(operation.Token);
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) Status = "Abgebrochen. Bei Git-Schreibaktionen Status erneut laden.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or JsonException or Win32Exception or FormatException)
        {
            if (generation == _generation)
                Error = exception is JsonException ? "Argumente als JSON-Array eingeben, z. B. [\"test\", \"--no-build\"]." : exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
            if (generation == _generation) IsBusy = false;
        }
    }
}
