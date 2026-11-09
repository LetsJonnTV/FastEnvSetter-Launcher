using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FastEnvSetter.Core;

namespace FastEnvSetter.App;

public sealed partial class PaletteViewModel(IEnumerable<ICommandSource> sources) : ObservableObject
{
    private const int MaxResults = 30;

    private IReadOnlyList<PaletteCommand> _all = [];

    public ObservableCollection<PaletteCommand> Results { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteCommand? _selected;

    partial void OnQueryChanged(string value) => Filter();

    public void Open()
    {
        _all = sources.SelectMany(s => s.GetCommands()).ToList();
        Query = "";
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Move(int offset)
    {
        if (Results.Count == 0)
            return;
        var index = Selected is null ? 0 : Results.IndexOf(Selected) + offset;
        Selected = Results[Math.Clamp(index, 0, Results.Count - 1)];
    }

    public async Task ExecuteSelectedAsync()
    {
        var command = Selected;
        Close();
        if (command is not null)
            await command.Execute();
    }

    private void Filter()
    {
        var terms = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Results.Clear();
        foreach (var command in _all.Where(c => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(MaxResults))
            Results.Add(command);
        Selected = Results.FirstOrDefault();
    }
}

public sealed class NavigationCommandSource(IEnumerable<NavigationEntry> entries, INavigator navigator) : ICommandSource
{
    public IEnumerable<PaletteCommand> GetCommands() => entries.OrderBy(e => e.Order).Select(entry =>
        new PaletteCommand($"Gehe zu {entry.Title}", "Seite", entry.Icon, () =>
        {
            navigator.Navigate(entry.PageType);
            return Task.CompletedTask;
        }));
}

public sealed class ProjectCommandSource(IAppDataStore store, ProjectActions actions, INavigator navigator) : ICommandSource
{
    public IEnumerable<PaletteCommand> GetCommands()
    {
        foreach (var project in store.Data.Projects.OrderByDescending(p => p.IsFavorite))
        {
            yield return actions.IsActive(project)
                ? new PaletteCommand($"{project.Name} stoppen", "Projekt", "Stop24", () => actions.StopAsync(project))
                : new PaletteCommand($"{project.Name} starten", "Projekt", "Play24", () => actions.StartAsync(project));
            yield return new PaletteCommand($"{project.Name} bearbeiten", "Projekt", "Edit24", () => Go(typeof(ProjectEditorPage), project));
            yield return new PaletteCommand($"{project.Name} Logs", "Projekt", "TextAlignLeft24", () => Go(typeof(RunningPage), project));
        }
    }

    private Task Go(Type page, Project project)
    {
        navigator.Navigate(page, project);
        return Task.CompletedTask;
    }
}
