using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using FastEnvSetter.Infrastructure;
using Microsoft.Win32;

namespace FastEnvSetter.App;

public sealed partial class SettingFieldViewModel : ObservableObject
{
    private readonly Func<string> _baseFolder;
    private readonly Action<string> _write;
    private readonly Action _changed;
    private string _value;
    private bool _isVisible = true;

    public SettingFieldViewModel(SettingField field, string value, Func<string> baseFolder, Action<string> write, Action changed)
    {
        Field = field;
        _value = value;
        _baseFolder = baseFolder;
        _write = write;
        _changed = changed;
        Choices = new ObservableCollection<string>(field.Choices);
    }

    public SettingField Field { get; }
    public ObservableCollection<string> Choices { get; }

    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (!SetProperty(ref _value, value))
                return;
            _write(value);
            OnPropertyChanged(nameof(BoolValue));
            _changed();
        }
    }

    public bool BoolValue
    {
        get => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public void ReplaceChoices(IReadOnlyList<string> choices)
    {
        var current = _value;
        Choices.Clear();
        foreach (var choice in choices)
            Choices.Add(choice);

        // Das Leeren der Liste kann den Text der ComboBox zurücksetzen.
        _value = current;
        OnPropertyChanged(nameof(Value));
    }

    [RelayCommand]
    private void Browse()
    {
        var folder = _baseFolder();
        string? selected = null;
        if (Field.Kind == SettingKind.FolderPath)
        {
            var dialog = new OpenFolderDialog { InitialDirectory = folder };
            if (dialog.ShowDialog() == true)
                selected = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { InitialDirectory = folder };
            if (dialog.ShowDialog() == true)
                selected = dialog.FileName;
        }

        if (selected is null)
            return;

        var relative = folder.Length > 0 ? Path.GetRelativePath(folder, selected) : selected;
        Value = relative.StartsWith("..", StringComparison.Ordinal) ? selected : relative;
    }
}

/// <summary>Eine Checkbox, die eine ID in einer Menge an- oder abwählt.</summary>
public sealed class IdCheck(string name, string detail, bool isSecret, Func<bool> read, Action<bool> write) : ObservableObject
{
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public bool IsSecret { get; } = isSecret;

    public bool IsChecked
    {
        get => read();
        set
        {
            write(value);
            OnPropertyChanged();
        }
    }
}

public sealed partial class ServiceEditorViewModel : ObservableObject
{
    private readonly ServiceDefinition _service;
    private readonly RunnerRegistry _runners;
    private readonly IAppDataStore _store;
    private readonly Action _nameChanged;
    private readonly SecretSources _sources;
    private IRunner _runner;
    private bool _isGroup;

    public ServiceEditorViewModel(ServiceDefinition service, RunnerRegistry runners, IAppDataStore store, SecretSources sources, Action nameChanged)
    {
        _service = service;
        _runners = runners;
        _store = store;
        _nameChanged = nameChanged;
        _sources = sources;
        _runner = runners.Find(service.RunnerId) ?? runners.All[0];
        foreach (var variable in service.ExtraVariables)
            ExtraVariables.Add(new VariableViewModel(variable, store, sources));
        BuildFields();
        ApplyDetection(choicesOnly: true);
    }

    public ServiceDefinition Service => _service;
    public IReadOnlyList<IRunner> Runners => _runners.All;
    public ObservableCollection<SettingFieldViewModel> Fields { get; } = [];
    public ObservableCollection<VariableViewModel> ExtraVariables { get; } = [];
    public ObservableCollection<IdCheck> ProfileVariables { get; } = [];
    public ObservableCollection<IdCheck> Dependencies { get; } = [];
    public IReadOnlyList<string> ReadinessOptions { get; } = ["Keine Prüfung", "Port ist offen", "URL antwortet"];

    public IRunner SelectedRunner
    {
        get => _runner;
        set
        {
            if (value is null || !SetProperty(ref _runner, value))
                return;

            _service.RunnerId = value.Id;
            _service.Settings.Clear();
            BuildFields();
            ApplyDetection(choicesOnly: false);
            FieldsChanged();
        }
    }

    public string Name
    {
        get => _service.Name;
        set { _service.Name = value; OnPropertyChanged(); Save(); _nameChanged(); }
    }

    public string WorkingDirectory
    {
        get => _service.WorkingDirectory;
        set
        {
            _service.WorkingDirectory = value;
            OnPropertyChanged();
            ApplyDetection(choicesOnly: false);
            FieldsChanged();
        }
    }

    public int StartDelaySeconds
    {
        get => _service.StartDelaySeconds;
        set { _service.StartDelaySeconds = Math.Max(0, value); OnPropertyChanged(); Save(); }
    }

    public bool UseTerminal
    {
        get => _service.OutputMode == OutputMode.Terminal || TerminalForced;
        set
        {
            _service.OutputMode = value ? OutputMode.Terminal : OutputMode.Integrated;
            OnPropertyChanged();
            FieldsChanged();
        }
    }

    public bool TerminalForced => _runner.RequiresTerminal(_service);
    public bool CanChooseOutput => !TerminalForced;

    public bool IsGroup
    {
        get => _isGroup;
        set => SetProperty(ref _isGroup, value);
    }

    public int ReadinessIndex
    {
        get => (int)_service.Readiness.Kind;
        set
        {
            _service.Readiness.Kind = (ReadinessKind)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPortReadiness));
            OnPropertyChanged(nameof(IsUrlReadiness));
            OnPropertyChanged(nameof(HasReadiness));
            Save();
        }
    }

    public bool IsPortReadiness => _service.Readiness.Kind == ReadinessKind.Port;
    public bool IsUrlReadiness => _service.Readiness.Kind == ReadinessKind.Http;
    public bool HasReadiness => _service.Readiness.Kind != ReadinessKind.None;

    public int ReadinessPort
    {
        get => _service.Readiness.Port;
        set { _service.Readiness.Port = value; OnPropertyChanged(); Save(); }
    }

    public string ReadinessUrl
    {
        get => _service.Readiness.Url;
        set { _service.Readiness.Url = value; OnPropertyChanged(); Save(); }
    }

    public int ReadinessTimeout
    {
        get => _service.Readiness.TimeoutSeconds;
        set { _service.Readiness.TimeoutSeconds = Math.Max(1, value); OnPropertyChanged(); Save(); }
    }

    public string PreStepsText
    {
        get => string.Join(Environment.NewLine, _service.PreSteps);
        set
        {
            _service.PreSteps = value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Save();
        }
    }

    public bool AutoRestart
    {
        get => _service.RestartPolicy == RestartPolicy.OnFailure;
        set { _service.RestartPolicy = value ? RestartPolicy.OnFailure : RestartPolicy.Never; OnPropertyChanged(); Save(); }
    }

    public string ExpectedPortsText
    {
        get => string.Join(", ", _service.ExpectedPorts);
        set
        {
            _service.ExpectedPorts = value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p, out var port) ? port : 0)
                .Where(p => p is > 0 and < 65536)
                .Distinct()
                .ToList();
            Save();
        }
    }

    public string OpenUrl
    {
        get => _service.OpenUrl;
        set { _service.OpenUrl = value.Trim(); OnPropertyChanged(); Save(); }
    }

    public string CommandPreview
    {
        get
        {
            try
            {
                return _runner.BuildStart(_service).ToCommandLine();
            }
            catch (Exception e) when (e is ArgumentException or IOException)
            {
                return "";
            }
        }
    }

    public void SetProfileVariables(IReadOnlyList<EnvLayerRow> rows)
    {
        ProfileVariables.Clear();
        foreach (var row in rows)
        {
            var id = row.Variable.Id;
            ProfileVariables.Add(new IdCheck(row.Variable.Name, row.Profile.Name, row.Variable.IsSecret,
                () => !_service.DisabledVariableIds.Contains(id),
                value =>
                {
                    if (value)
                        _service.DisabledVariableIds.Remove(id);
                    else
                        _service.DisabledVariableIds.Add(id);
                    Save();
                }));
        }
    }

    public void SetDependencies(IEnumerable<ServiceDefinition> others)
    {
        Dependencies.Clear();
        foreach (var other in others)
        {
            var id = other.Id;
            Dependencies.Add(new IdCheck(other.Name, "", false, () => _service.DependsOn.Contains(id), value =>
            {
                _service.DependsOn.Remove(id);
                if (value)
                    _service.DependsOn.Add(id);
                Save();
            }));
        }
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(WorkingDirectory))
            dialog.InitialDirectory = WorkingDirectory;
        if (dialog.ShowDialog() == true)
            WorkingDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void AddVariable()
    {
        var variable = new EnvVariable();
        _service.ExtraVariables.Add(variable);
        ExtraVariables.Add(new VariableViewModel(variable, _store, _sources));
        Save();
    }

    [RelayCommand]
    private void RemoveVariable(VariableViewModel variable)
    {
        _service.ExtraVariables.Remove(variable.Variable);
        ExtraVariables.Remove(variable);
        Save();
    }

    private void BuildFields()
    {
        _service.RunnerId = _runner.Id;
        Fields.Clear();
        foreach (var field in _runner.GetSettingsSchema())
        {
            var value = _service.Settings.TryGetValue(field.Key, out var stored) ? stored : field.Default;
            Fields.Add(new SettingFieldViewModel(field, value, () => _service.WorkingDirectory,
                v => _service.Settings[field.Key] = v, FieldsChanged));
        }

        UpdateVisibility();
    }

    private void ApplyDetection(bool choicesOnly)
    {
        if (!Directory.Exists(_service.WorkingDirectory))
            return;

        var detection = _runner.Detect(_service.WorkingDirectory);
        foreach (var field in Fields)
        {
            if (detection.Choices.TryGetValue(field.Field.Key, out var choices))
                field.ReplaceChoices(choices);

            // Vom Nutzer gesetzte Werte werden nie überschrieben.
            var untouched = field.Value.Length == 0 || field.Value == field.Field.Default;
            if (!choicesOnly && untouched && detection.Values.TryGetValue(field.Field.Key, out var value))
                field.Value = value;
        }
    }

    private void FieldsChanged()
    {
        UpdateVisibility();
        OnPropertyChanged(nameof(TerminalForced));
        OnPropertyChanged(nameof(CanChooseOutput));
        OnPropertyChanged(nameof(UseTerminal));
        OnPropertyChanged(nameof(CommandPreview));
        Save();
    }

    private void UpdateVisibility()
    {
        foreach (var field in Fields)
            field.IsVisible = field.Field.IsVisible(ValueOf);
    }

    private string ValueOf(string key) => Fields.FirstOrDefault(f => f.Field.Key == key)?.Value ?? "";

    private void Save() => _store.RequestSave();
}

public sealed partial class EditorTargetViewModel(EditorTarget target, Action save, Action<EditorTarget> open, Func<string> root) : ObservableObject
{
    public EditorTarget Target => target;
    public string Name { get => target.Name; set { target.Name = value; OnPropertyChanged(); save(); } }
    public string Path { get => target.Path; set { target.Path = value; OnPropertyChanged(); save(); } }
    public string Profile { get => target.Profile; set { target.Profile = value; OnPropertyChanged(); save(); } }

    [RelayCommand]
    private void Open() => open(target);

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Editorordner wählen" };
        if (Directory.Exists(root()))
            dialog.InitialDirectory = root();
        if (dialog.ShowDialog() == true && Directory.Exists(root()))
            Path = System.IO.Path.GetRelativePath(root(), dialog.FolderName);
    }

    [RelayCommand]
    private void BrowseWorkspace()
    {
        var dialog = new OpenFileDialog { Filter = "VS Code Workspace|*.code-workspace" };
        if (Directory.Exists(root()))
            dialog.InitialDirectory = root();
        if (dialog.ShowDialog() == true && Directory.Exists(root()))
            Path = System.IO.Path.GetRelativePath(root(), dialog.FileName);
    }
}

public sealed partial class ProjectEditorViewModel(
    IAppDataStore store, RunnerRegistry runners, ProjectActions actions, INavigator navigator, SecretSources sources) : ObservableObject
{
    private Project _project = new();

    public ProjectDevelopmentViewModel Development { get; } = new(store, sources);
    [ObservableProperty] private bool _isProjectAvailable;
    public ObservableCollection<IdCheck> Profiles { get; } = [];
    public ObservableCollection<IdCheck> Variables { get; } = [];
    public ObservableCollection<ServiceEditorViewModel> Services { get; } = [];
    public ObservableCollection<EditorTargetViewModel> Editors { get; } = [];
    [ObservableProperty] private string _editorError = "";

    public string SourceRoot
    {
        get => _project.SourceRoot;
        set
        {
            var root = value.Trim();
            if (_project.SourceRoot == root) return;
            _project.SourceRoot = root;
            OnPropertyChanged();
            store.RequestSave();
            Development.Load(_project.Id);
        }
    }

    [RelayCommand]
    private void BrowseSourceRoot()
    {
        var dialog = new OpenFolderDialog { Title = "Quellordner zuordnen" };
        if (Directory.Exists(SourceRoot))
            dialog.InitialDirectory = SourceRoot;
        if (dialog.ShowDialog() == true)
            SourceRoot = dialog.FolderName;
    }

    [RelayCommand]
    private void AddEditor()
    {
        var target = new EditorTarget { Name = "VS Code" };
        _project.EditorTargets.Add(target);
        Editors.Add(CreateEditorTarget(target));
        store.RequestSave();
    }

    [RelayCommand]
    private void RemoveEditor(EditorTargetViewModel editor)
    {
        _project.EditorTargets.Remove(editor.Target);
        Editors.Remove(editor);
        store.RequestSave();
    }

    [RelayCommand]
    private void OpenSource() => OpenEditorTarget(new EditorTarget());

    private EditorTargetViewModel CreateEditorTarget(EditorTarget target) => new(target, store.RequestSave, OpenEditorTarget, () => SourceRoot);

    private void OpenEditorTarget(EditorTarget target)
    {
        try
        {
            new EditorLauncher().Open(_project, target);
            EditorError = "";
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            EditorError = error.Message;
        }
    }

    public bool HasProfiles => Profiles.Count > 0;

    public string Name
    {
        get => _project.Name;
        set { _project.Name = value; OnPropertyChanged(); store.RequestSave(); }
    }

    public string TagsText
    {
        get => string.Join(", ", _project.Tags);
        set
        {
            _project.Tags = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            store.RequestSave();
        }
    }

    public void Load(Project project)
    {
        var current = store.Data.Projects.FirstOrDefault(candidate => candidate.Id == project.Id);
        IsProjectAvailable = current is not null;
        if (current is null)
        {
            Development.Stop();
            EditorError = "Das Projekt wurde entfernt. Bitte zur Projektliste zurückkehren.";
            return;
        }
        project = current;
        _project = project;
        Development.Load(project.Id);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TagsText));
        OnPropertyChanged(nameof(SourceRoot));
        EditorError = "";
        Editors.Clear();
        foreach (var target in project.EditorTargets)
            Editors.Add(CreateEditorTarget(target));

        Profiles.Clear();
        foreach (var profile in store.Data.Profiles)
        {
            var id = profile.Id;
            Profiles.Add(new IdCheck(profile.Name, "", false, () => _project.EnvProfileIds.Contains(id), value => ToggleProfile(id, value)));
        }

        OnPropertyChanged(nameof(HasProfiles));

        Services.Clear();
        foreach (var service in project.Services)
            Services.Add(CreateEditor(service));

        LoadVariables();
        UpdateGroupState();
    }

    public void RefreshCurrentProject()
    {
        var current = store.Data.Projects.FirstOrDefault(candidate => candidate.Id == _project.Id);
        if (!ReferenceEquals(current, _project))
        {
            Load(current ?? _project);
            if (current is not null)
                EditorError = "Das Projekt wurde extern geändert und neu geladen. Nicht gespeicherte Aufgabenentwürfe wurden verworfen.";
        }
    }

    private ServiceEditorViewModel CreateEditor(ServiceDefinition service) =>
        new(service, runners, store, sources, RefreshDependencies);

    private void ToggleProfile(Guid id, bool selected)
    {
        _project.EnvProfileIds.Remove(id);
        if (selected)
            _project.EnvProfileIds.Add(id);
        store.RequestSave();
        LoadVariables();
    }

    private void LoadVariables()
    {
        var rows = EnvResolver.Describe(_project, store.Data.Profiles);

        Variables.Clear();
        foreach (var row in rows)
        {
            var id = row.Variable.Id;
            var detail = row.Profile.Name + (row.IsInherited ? " (geerbt)" : "") + (row.IsOverridden ? " · wird überschrieben" : "");
            Variables.Add(new IdCheck(row.Variable.Name, detail, row.Variable.IsSecret,
                () => !_project.DisabledVariableIds.Contains(id),
                value =>
                {
                    if (value)
                        _project.DisabledVariableIds.Remove(id);
                    else
                        _project.DisabledVariableIds.Add(id);
                    store.RequestSave();
                }));
        }

        foreach (var service in Services)
            service.SetProfileVariables(rows);
    }

    private ServiceDefinition NewService(Project project) => new()
    {
        Name = project.Services.Count == 0 ? "Hauptdienst" : $"Dienst {project.Services.Count + 1}",
        RunnerId = runners.All[0].Id,
    };

    private void UpdateGroupState()
    {
        foreach (var service in Services)
            service.IsGroup = Services.Count > 1;
        RefreshDependencies();
    }

    private void RefreshDependencies()
    {
        foreach (var service in Services)
            service.SetDependencies(_project.Services.Where(s => s != service.Service));
    }

    [RelayCommand]
    private void AddService()
    {
        var service = NewService(_project);
        _project.Services.Add(service);
        Services.Add(CreateEditor(service));
        LoadVariables();
        UpdateGroupState();
        store.RequestSave();
    }

    [RelayCommand]
    private async Task RemoveServiceAsync(ServiceEditorViewModel service)
    {
        if (!await Dialogs.ConfirmAsync("Dienst entfernen", $"„{service.Name}“ aus dem Projekt entfernen?", "Entfernen"))
            return;

        _project.Services.Remove(service.Service);
        foreach (var other in _project.Services)
            other.DependsOn.Remove(service.Service.Id);
        Services.Remove(service);
        UpdateGroupState();
        store.RequestSave();
    }

    [RelayCommand]
    private void MoveUp(ServiceEditorViewModel service) => Move(service, -1);

    [RelayCommand]
    private void MoveDown(ServiceEditorViewModel service) => Move(service, 1);

    private void Move(ServiceEditorViewModel service, int offset)
    {
        var from = Services.IndexOf(service);
        var to = from + offset;
        if (to < 0 || to >= Services.Count)
            return;

        Services.Move(from, to);
        _project.Services.RemoveAt(from);
        _project.Services.Insert(to, service.Service);
        store.RequestSave();
    }

    [RelayCommand]
    private void Back() => navigator.Navigate(typeof(ProjectsPage));

    [RelayCommand]
    private async Task StartAsync()
    {
        var current = store.Data.Projects.FirstOrDefault(project => project.Id == _project.Id);
        if (current is null)
        {
            EditorError = "Das Projekt wurde entfernt. Bitte zur Projektliste zurückkehren.";
            return;
        }
        await actions.StartAsync(current);
        navigator.Navigate(typeof(RunningPage), current);
    }
}
