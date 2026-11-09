using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public sealed partial class ProjectCard(Project project) : ObservableObject
{
    public Project Project { get; } = project;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private Brush _stateBrush = StatePresentation.Gray;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private IReadOnlyList<string> _icons = [];
    [ObservableProperty] private bool _isFavorite = project.IsFavorite;

    public string? OpenUrl => Project.Services.Select(s => s.OpenUrl).FirstOrDefault(u => u.Length > 0);
    public bool HasOpenUrl => OpenUrl is not null;
}

public sealed partial class ProjectsViewModel : ObservableObject
{
    private readonly IAppDataStore _store;
    private readonly IProcessSupervisor _supervisor;
    private readonly RunnerRegistry _runners;
    private readonly ProjectActions _actions;
    private readonly INavigator _navigator;
    private readonly INotifier _notifier;
    private bool _rebuildingTags;

    public ProjectsViewModel(IAppDataStore store, IProcessSupervisor supervisor, RunnerRegistry runners, ProjectActions actions, INavigator navigator, INotifier notifier)
    {
        _notifier = notifier;
        _store = store;
        _supervisor = supervisor;
        _runners = runners;
        _actions = actions;
        _navigator = navigator;
        _supervisor.StateChanged += _ => Ui.Post(RefreshStates);
    }

    private const string AllTags = "Alle Tags";

    public ObservableCollection<ProjectCard> Cards { get; } = [];
    public ObservableCollection<string> Tags { get; } = [];
    public bool IsEmpty => _store.Data.Projects.Count == 0;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string? _selectedTag = AllTags;

    partial void OnQueryChanged(string value) => Refill();

    partial void OnSelectedTagChanged(string? value)
    {
        if (!_rebuildingTags)
            Refill();
    }

    public void Reload()
    {
        _rebuildingTags = true;
        var selected = SelectedTag;
        Tags.Clear();
        Tags.Add(AllTags);
        foreach (var tag in _store.Data.Projects.SelectMany(p => p.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order())
            Tags.Add(tag);
        SelectedTag = selected is not null && Tags.Contains(selected) ? selected : AllTags;
        _rebuildingTags = false;

        OnPropertyChanged(nameof(IsEmpty));
        Refill();
    }

    private void Refill()
    {
        var projects = _store.Data.Projects
            .Where(p => Query.Length == 0 || p.Name.Contains(Query, StringComparison.OrdinalIgnoreCase))
            .Where(p => SelectedTag is null or AllTags || p.Tags.Contains(SelectedTag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.IsFavorite);

        Cards.Clear();
        foreach (var project in projects)
        {
            var card = new ProjectCard(project);
            Describe(card);
            Cards.Add(card);
        }

        OnPropertyChanged(nameof(Cards));
    }

    private void RefreshStates()
    {
        foreach (var card in Cards)
            UpdateState(card);
    }

    private void Describe(ProjectCard card)
    {
        var project = card.Project;
        var runners = project.Services.Select(s => _runners.Find(s.RunnerId)).OfType<IRunner>().Distinct().ToList();
        var count = project.Services.Count == 1 ? "1 Dienst" : $"{project.Services.Count} Dienste";

        card.Name = string.IsNullOrWhiteSpace(project.Name) ? "Ohne Namen" : project.Name;
        card.Summary = $"{count} · {string.Join(", ", runners.Select(r => r.DisplayName))}";
        card.Icons = runners.Select(r => r.Icon).ToList();
        var profiles = _store.Data.Profiles.Where(p => project.EnvProfileIds.Contains(p.Id)).Select(p => p.Name).ToList();
        card.ProfileName = profiles.Count > 0 ? string.Join(", ", profiles) : "Kein Env-Profil";
        UpdateState(card);
    }

    private void UpdateState(ProjectCard card)
    {
        var states = card.Project.Services.Select(s => _supervisor.GetState(s.Id)).ToList();
        var state = states.Contains(ServiceState.Failed) || states.Contains(ServiceState.Blocked) ? ServiceState.Failed
            : states.Contains(ServiceState.Starting) || states.Contains(ServiceState.Waiting) ? ServiceState.Starting
            : states.Contains(ServiceState.Stopping) ? ServiceState.Stopping
            : states.Contains(ServiceState.Running) ? ServiceState.Running
            : ServiceState.Stopped;

        var running = states.Count(s => s == ServiceState.Running);
        card.StateText = states.Count > 1 && running > 0
            ? $"{StatePresentation.TextFor(state)} ({running}/{states.Count})"
            : StatePresentation.TextFor(state);
        card.StateBrush = StatePresentation.BrushFor(state);
        card.IsActive = _actions.IsActive(card.Project);
    }

    [RelayCommand]
    private async Task ToggleAsync(ProjectCard card)
    {
        if (card.IsActive)
            await _actions.StopAsync(card.Project);
        else
            await _actions.StartAsync(card.Project);
        UpdateState(card);
    }

    [RelayCommand]
    private void ToggleFavorite(ProjectCard card)
    {
        card.Project.IsFavorite = !card.Project.IsFavorite;
        _store.RequestSave();
        Refill();
    }

    [RelayCommand]
    private async Task ExportAsync(ProjectCard card)
    {
        var root = ProjectTransfer.CommonRoot(card.Project);
        var json = ProjectTransfer.Export(card.Project, _store.Data.Profiles, root);
        var summary = string.Join(Environment.NewLine, ProjectTransfer.Summarize(ProjectTransfer.Parse(json)!));
        if (!await Dialogs.ConfirmAsync("Projekt exportieren", $"Die Datei enthält:{Environment.NewLine}{summary}", "Weiter"))
            return;

        var dialog = new SaveFileDialog { FileName = card.Name + ".fesproject", Filter = "FastEnvSetter-Projekt|*.fesproject" };
        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, json);
        _notifier.Show("Exportiert", dialog.FileName, InfoBarSeverity.Success);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var file = new OpenFileDialog { Filter = "FastEnvSetter-Projekt|*.fesproject|Alle Dateien|*.*" };
        if (file.ShowDialog() != true)
            return;

        var package = ProjectTransfer.Parse(File.ReadAllText(file.FileName));
        if (package is null)
        {
            _notifier.Show("Import nicht möglich", "Die Datei ist kein gültiges FastEnvSetter-Projekt.", InfoBarSeverity.Error);
            return;
        }

        var summary = string.Join(Environment.NewLine, ProjectTransfer.Summarize(package));
        if (!await Dialogs.ConfirmAsync("Projekt importieren", $"{summary}{Environment.NewLine}{Environment.NewLine}Importiere nur Dateien, denen du vertraust: Beim Start laufen die enthaltenen Befehle.", "Weiter"))
            return;

        var folder = new OpenFolderDialog { Title = "Wurzelordner des Projekts wählen" };
        if (folder.ShowDialog() != true)
            return;

        var project = ProjectTransfer.Import(package, folder.FolderName, _store.Data);
        _store.RequestSave();
        _navigator.Navigate(typeof(ProjectEditorPage), project);
    }

    [RelayCommand]
    private void Add()
    {
        var project = new Project { Name = "Neues Projekt" };
        _store.Data.Projects.Add(project);
        _store.RequestSave();
        _navigator.Navigate(typeof(ProjectEditorPage), project);
    }

    [RelayCommand]
    private void Edit(ProjectCard card) => _navigator.Navigate(typeof(ProjectEditorPage), card.Project);

    [RelayCommand]
    private void Open(ProjectCard card)
    {
        if (card.OpenUrl is { } url)
            _actions.OpenUrl(url);
    }

    [RelayCommand]
    private void ShowLogs(ProjectCard card) => _navigator.Navigate(typeof(RunningPage), card.Project);

    [RelayCommand]
    private void Duplicate(ProjectCard card)
    {
        var copy = JsonSerializer.Deserialize<Project>(JsonSerializer.Serialize(card.Project))!;
        copy.Id = Guid.NewGuid();
        copy.Name += " (Kopie)";
        foreach (var service in copy.Services)
            service.Id = Guid.NewGuid();

        _store.Data.Projects.Add(copy);
        _store.RequestSave();
        Reload();
    }

    [RelayCommand]
    private async Task DeleteAsync(ProjectCard card)
    {
        if (!await Dialogs.ConfirmAsync("Projekt löschen", $"„{card.Name}“ wirklich löschen?", "Löschen"))
            return;

        await _actions.StopAsync(card.Project);
        _store.Data.Projects.Remove(card.Project);
        _store.RequestSave();
        Reload();
    }
}
