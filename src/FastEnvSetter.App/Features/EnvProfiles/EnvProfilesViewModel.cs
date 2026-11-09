using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public sealed record SecretSourceOption(ISecretProvider? Provider)
{
    public string Name => Provider?.DisplayName ?? "Lokal";
}

public sealed class SecretSources(IEnumerable<ISecretProvider> providers)
{
    public IReadOnlyList<ISecretProvider> Providers { get; } = providers.ToList();
    public IReadOnlyList<SecretSourceOption> Options { get; } =
        [new SecretSourceOption(null), .. providers.Select(p => new SecretSourceOption(p))];
}

public sealed class VariableViewModel(EnvVariable variable, IAppDataStore store, SecretSources sources) : ObservableObject
{
    public EnvVariable Variable => variable;
    public IReadOnlyList<SecretSourceOption> SourceOptions => sources.Options;
    public bool IsVault => variable.SecretRef is not null;
    public bool IsLocal => !IsVault;
    public bool ShowPlain => IsLocal && !variable.IsSecret;
    public bool ShowPassword => IsLocal && variable.IsSecret;
    public string ReferenceHint => SelectedSource.Provider?.ReferenceHint ?? "";

    public SecretSourceOption SelectedSource
    {
        get => sources.Options.FirstOrDefault(o => o.Provider?.Id == variable.SecretRef?.ProviderId) ?? sources.Options[0];
        set
        {
            if (value is null)
                return;

            if (value.Provider is null)
            {
                variable.SecretRef = null;
            }
            else
            {
                variable.SecretRef ??= new SecretRef();
                variable.SecretRef.ProviderId = value.Provider.Id;
                variable.IsSecret = true;
                variable.Value = "";
                variable.DecryptionFailed = false;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVault));
            OnPropertyChanged(nameof(IsLocal));
            OnPropertyChanged(nameof(ShowPlain));
            OnPropertyChanged(nameof(ShowPassword));
            OnPropertyChanged(nameof(ReferenceHint));
            OnPropertyChanged(nameof(Reference));
            OnPropertyChanged(nameof(DecryptionFailed));
            store.RequestSave();
        }
    }

    public string Reference
    {
        get => variable.SecretRef?.Reference ?? "";
        set
        {
            if (variable.SecretRef is null)
                return;
            variable.SecretRef.Reference = value.Trim();
            OnPropertyChanged();
            store.RequestSave();
        }
    }

    public string Name
    {
        get => variable.Name;
        set { variable.Name = value; OnPropertyChanged(); store.RequestSave(); }
    }

    public string Value
    {
        get => variable.Value;
        set
        {
            if (variable.Value == value)
                return;
            variable.Value = value;
            variable.DecryptionFailed = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DecryptionFailed));
            store.RequestSave();
        }
    }

    public bool IsSecret
    {
        get => variable.IsSecret;
        set
        {
            variable.IsSecret = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowPlain));
            OnPropertyChanged(nameof(ShowPassword));
            store.RequestSave();
        }
    }

    public bool DecryptionFailed => variable.DecryptionFailed;
}

public sealed class ProfileItem(EnvProfile profile, IAppDataStore store) : ObservableObject
{
    public EnvProfile Profile => profile;

    public string Name
    {
        get => profile.Name;
        set { profile.Name = value; OnPropertyChanged(); store.RequestSave(); }
    }
}

public sealed record ParentOption(EnvProfile? Profile)
{
    public string Name => Profile?.Name ?? "Kein Basisprofil";
}

public sealed partial class ImportRow(DotEnvEntry entry, bool exists) : ObservableObject
{
    public DotEnvEntry Entry { get; } = entry;
    public string Status { get; } = exists ? "überschreibt" : "neu";

    [ObservableProperty] private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private bool _isSecret = DotEnv.LooksSecret(entry.Name);

    public string Preview => IsSecret ? "••••••" : Entry.Value.ReplaceLineEndings(" ");
}

public sealed partial class EnvProfilesViewModel(IAppDataStore store, INotifier notifier, SecretSources sources) : ObservableObject
{
    private EnvProfile? _importTarget;
    private string _importName = "";
    private bool _loadingParent;

    public ObservableCollection<ProfileItem> Profiles { get; } = [];
    public ObservableCollection<VariableViewModel> Variables { get; } = [];
    public ObservableCollection<ParentOption> ParentOptions { get; } = [];
    public ObservableCollection<ImportRow> ImportRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(ShowEditor))]
    private ProfileItem? _selected;

    [ObservableProperty] private ParentOption? _selectedParent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditor))]
    private bool _isImporting;

    [ObservableProperty] private string _importTitle = "";
    [ObservableProperty] private string _importNote = "";

    public bool HasSelection => Selected is not null;
    public bool ShowEditor => HasSelection && !IsImporting;

    partial void OnSelectedChanged(ProfileItem? value)
    {
        Variables.Clear();
        foreach (var variable in value?.Profile.Variables ?? [])
            Variables.Add(new VariableViewModel(variable, store, sources));
        LoadParents();
    }

    partial void OnSelectedParentChanged(ParentOption? value)
    {
        if (_loadingParent || value is null || Selected is null)
            return;
        Selected.Profile.ParentId = value.Profile?.Id;
        store.RequestSave();
    }

    public void Reload()
    {
        var selectedId = Selected?.Profile.Id;
        Profiles.Clear();
        foreach (var profile in store.Data.Profiles)
            Profiles.Add(new ProfileItem(profile, store));
        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == selectedId) ?? Profiles.FirstOrDefault();
    }

    private void LoadParents()
    {
        _loadingParent = true;
        ParentOptions.Clear();
        ParentOptions.Add(new ParentOption(null));
        if (Selected is not null)
        {
            foreach (var profile in store.Data.Profiles.Where(p => !InheritsFrom(p, Selected.Profile)))
                ParentOptions.Add(new ParentOption(profile));
        }

        SelectedParent = ParentOptions.FirstOrDefault(o => o.Profile?.Id == Selected?.Profile.ParentId) ?? ParentOptions[0];
        _loadingParent = false;
    }

    /// <summary>Wahr, wenn die Wahl als Basisprofil einen Vererbungskreis erzeugen würde.</summary>
    private bool InheritsFrom(EnvProfile candidate, EnvProfile target)
    {
        var visited = new HashSet<Guid>();
        for (EnvProfile? current = candidate; current is not null && visited.Add(current.Id);
             current = store.Data.Profiles.FirstOrDefault(p => p.Id == current.ParentId))
        {
            if (current.Id == target.Id)
                return true;
        }

        return false;
    }

    private void Select(EnvProfile profile)
    {
        var item = new ProfileItem(profile, store);
        Profiles.Add(item);
        Selected = item;
    }

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new EnvProfile { Name = $"Profil {store.Data.Profiles.Count + 1}" };
        store.Data.Profiles.Add(profile);
        store.RequestSave();
        Select(profile);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        if (Selected is null)
            return;

        var copy = new EnvProfile
        {
            Name = Selected.Name + " (Kopie)",
            ParentId = Selected.Profile.ParentId,
            Variables = Selected.Profile.Variables.Select(v => new EnvVariable
            {
                Name = v.Name,
                Value = v.Value,
                IsSecret = v.IsSecret,
                ProtectedValue = v.ProtectedValue,
                DecryptionFailed = v.DecryptionFailed,
                SecretRef = v.SecretRef is null ? null : new SecretRef { ProviderId = v.SecretRef.ProviderId, Reference = v.SecretRef.Reference },
            }).ToList(),
        };

        store.Data.Profiles.Add(copy);
        store.RequestSave();
        Select(copy);
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (Selected is null)
            return;
        if (!await Dialogs.ConfirmAsync("Profil löschen", $"„{Selected.Name}“ wirklich löschen? Projekte verwenden es danach nicht mehr.", "Löschen"))
            return;

        var profile = Selected.Profile;
        foreach (var project in store.Data.Projects)
            project.EnvProfileIds.Remove(profile.Id);
        foreach (var child in store.Data.Profiles.Where(p => p.ParentId == profile.Id))
            child.ParentId = profile.ParentId;

        store.Data.Profiles.Remove(profile);
        store.RequestSave();
        Profiles.Remove(Selected);
        Selected = Profiles.FirstOrDefault();
    }

    [RelayCommand]
    private void AddVariable()
    {
        if (Selected is null)
            return;

        var variable = new EnvVariable();
        Selected.Profile.Variables.Add(variable);
        Variables.Add(new VariableViewModel(variable, store, sources));
        store.RequestSave();
    }

    [RelayCommand]
    private void RemoveVariable(VariableViewModel variable)
    {
        Selected?.Profile.Variables.Remove(variable.Variable);
        Variables.Remove(variable);
        store.RequestSave();
    }

    [RelayCommand]
    private void ImportAsNew() => ChooseImportFile(null);

    [RelayCommand]
    private void ImportIntoSelected() => ChooseImportFile(Selected?.Profile);

    private void ChooseImportFile(EnvProfile? target)
    {
        var dialog = new OpenFileDialog { Filter = ".env-Dateien|*.env;.env*;*.env.*|Alle Dateien|*.*" };
        if (dialog.ShowDialog() == true)
            BeginImport(dialog.FileName, target);
    }

    public void BeginImport(string path, EnvProfile? target)
    {
        DotEnvDocument document;
        try
        {
            document = DotEnv.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            notifier.Show("Datei nicht lesbar", e.Message, InfoBarSeverity.Error);
            return;
        }

        if (document.Entries.Count == 0)
        {
            notifier.Show("Nichts zu importieren", "In der Datei wurden keine Variablen gefunden.", InfoBarSeverity.Warning);
            return;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        _importName = folder.Length > 0 ? folder : Path.GetFileName(path);
        _importTarget = target;
        ImportTitle = target is null ? $"Neues Profil „{_importName}“ aus {Path.GetFileName(path)}" : $"In „{target.Name}“ importieren";
        ImportNote = document.SkippedLines.Count > 0
            ? $"Nicht lesbar und übersprungen: Zeile {string.Join(", ", document.SkippedLines)}"
            : "";

        ImportRows.Clear();
        foreach (var entry in document.Entries)
            ImportRows.Add(new ImportRow(entry, target?.Variables.Any(v => SameName(v, entry.Name)) == true));
        IsImporting = true;
    }

    [RelayCommand]
    private void CancelImport()
    {
        IsImporting = false;
        ImportRows.Clear();
    }

    [RelayCommand]
    private void ConfirmImport()
    {
        var target = _importTarget;
        if (target is null)
        {
            target = new EnvProfile { Name = _importName };
            store.Data.Profiles.Add(target);
        }

        foreach (var row in ImportRows.Where(r => r.IsSelected))
        {
            var variable = target.Variables.FirstOrDefault(v => SameName(v, row.Entry.Name));
            if (variable is null)
            {
                variable = new EnvVariable { Name = row.Entry.Name };
                target.Variables.Add(variable);
            }

            variable.Value = row.Entry.Value;
            variable.IsSecret = row.IsSecret;
            variable.DecryptionFailed = false;
            variable.SecretRef = null;
        }

        store.RequestSave();
        CancelImport();
        Selected = null;
        Reload();
        Selected = Profiles.First(p => p.Profile.Id == target.Id);
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Selected is null)
            return;

        var variables = Selected.Profile.Variables.Where(v => !v.DecryptionFailed && v.SecretRef is null && v.Name.Trim().Length > 0).ToList();
        if (variables.Any(v => v.IsSecret)
            && !await Dialogs.ConfirmAsync("Secrets exportieren?", "Eine .env-Datei enthält alle Werte im Klartext, auch die geheimen.", "Trotzdem exportieren"))
        {
            return;
        }

        var dialog = new SaveFileDialog { FileName = ".env", Filter = "Alle Dateien|*.*" };
        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, DotEnv.Serialize(variables.Select(v => new DotEnvEntry(v.Name.Trim(), v.Value))));
        notifier.Show("Exportiert", dialog.FileName, InfoBarSeverity.Success);
    }

    private static bool SameName(EnvVariable variable, string name) =>
        variable.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase);
}
