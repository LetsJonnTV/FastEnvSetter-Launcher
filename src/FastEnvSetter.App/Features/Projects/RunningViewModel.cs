using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FastEnvSetter.Core;
using Microsoft.Win32;
using Wpf.Ui.Controls;

namespace FastEnvSetter.App;

public sealed partial class RunningItem(Project project, ServiceDefinition service) : ObservableObject
{
    public Project Project { get; } = project;
    public ServiceDefinition Service { get; } = service;
    public string Title => $"{Project.Name} · {Service.Name}";
    public bool HasOpenUrl => Service.OpenUrl.Length > 0;
    public bool HasFolder => Service.WorkingDirectory.Length > 0;

    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private Brush _stateBrush = StatePresentation.Gray;
    [ObservableProperty] private bool _isActive;
}

public sealed record LogLine(LogEntry Entry, string Source)
{
    public bool HasSource => Source.Length > 0;
    public string ExportText => HasSource ? $"[{Source}] {Entry.Text}" : Entry.Text;
}

/// <summary>Zeigt eine Logzeile mit ihren ANSI-Farben an.</summary>
public static class LogText
{
    public static readonly DependencyProperty EntryProperty = DependencyProperty.RegisterAttached(
        "Entry", typeof(LogEntry), typeof(LogText), new PropertyMetadata(null, OnEntryChanged));

    private static readonly Dictionary<int, Brush> Brushes = [];

    public static LogEntry? GetEntry(DependencyObject element) => (LogEntry?)element.GetValue(EntryProperty);
    public static void SetEntry(DependencyObject element, LogEntry? value) => element.SetValue(EntryProperty, value);

    private static void OnEntryChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not System.Windows.Controls.TextBlock block || e.NewValue is not LogEntry entry)
            return;

        if (entry.Spans is null)
        {
            block.Text = entry.Text;
            return;
        }

        block.Inlines.Clear();
        foreach (var span in entry.Spans)
        {
            var run = new Run(span.Text);
            if (span.Color is { } color)
                run.Foreground = BrushFor(color);
            block.Inlines.Add(run);
        }
    }

    private static Brush BrushFor(int color)
    {
        if (Brushes.TryGetValue(color, out var brush))
            return brush;

        brush = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        Brushes[color] = brush;
        return brush;
    }
}

public sealed partial class RunningViewModel : ObservableObject
{
    private const int MaxVisibleLines = 5000;

    private readonly IAppDataStore _store;
    private readonly IProcessSupervisor _supervisor;
    private readonly ProjectActions _actions;
    private readonly INotifier _notifier;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, long> _lastSequences = [];

    public RunningViewModel(IAppDataStore store, IProcessSupervisor supervisor, ProjectActions actions, INotifier notifier)
    {
        _store = store;
        _supervisor = supervisor;
        _actions = actions;
        _notifier = notifier;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => PullLines();
        _supervisor.StateChanged += _ => Ui.Post(Refresh);
    }

    /// <summary>Wird ausgelöst, nachdem neue Zeilen angehängt wurden.</summary>
    public event Action? LinesAppended;

    public ObservableCollection<RunningItem> Items { get; } = [];
    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty] private RunningItem? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _followOutput = true;
    [ObservableProperty] private bool _onlyErrors;
    [ObservableProperty] private bool _showAll;

    partial void OnSelectedChanged(RunningItem? value) => ReloadLines();
    partial void OnSearchChanged(string value) => ReloadLines();
    partial void OnOnlyErrorsChanged(bool value) => ReloadLines();
    partial void OnShowAllChanged(bool value) => ReloadLines();

    public void Activate(object? parameter)
    {
        Refresh();
        if (parameter is Project project)
            Selected = Items.FirstOrDefault(i => i.Project == project) ?? Selected;
        Selected ??= Items.FirstOrDefault();
        ReloadLines();
        _timer.Start();
    }

    public void Deactivate() => _timer.Stop();

    private void Refresh()
    {
        var wanted = _store.Data.Projects
            .SelectMany(p => p.Services.Select(s => (Project: p, Service: s)))
            .Where(x => _supervisor.GetState(x.Service.Id) != ServiceState.Stopped || _supervisor.GetLogSince(x.Service.Id, 0).Count > 0)
            .ToList();

        foreach (var stale in Items.Where(i => !wanted.Any(w => w.Service == i.Service)).ToList())
            Items.Remove(stale);

        foreach (var (project, service) in wanted)
        {
            if (Items.All(i => i.Service != service))
                Items.Add(new RunningItem(project, service));
        }

        foreach (var item in Items)
        {
            var state = _supervisor.GetState(item.Service.Id);
            item.StateText = _supervisor.GetMessage(item.Service.Id) ?? StatePresentation.TextFor(state);
            item.StateBrush = StatePresentation.BrushFor(state);
            item.IsActive = ProjectActions.IsActive(state);
        }
    }

    private void ReloadLines()
    {
        Lines.Clear();
        _lastSequences.Clear();
        PullLines();
    }

    private void PullLines()
    {
        var sources = ShowAll ? Items.ToList() : Selected is null ? [] : [Selected];
        var fresh = new List<LogLine>();
        foreach (var item in sources)
        {
            var entries = _supervisor.GetLogSince(item.Service.Id, _lastSequences.GetValueOrDefault(item.Service.Id));
            if (entries.Count == 0)
                continue;

            _lastSequences[item.Service.Id] = entries[^1].Sequence;
            fresh.AddRange(entries.Where(Matches).Select(e => new LogLine(e, ShowAll ? item.Service.Name : "")));
        }

        if (fresh.Count == 0)
            return;

        foreach (var line in fresh.OrderBy(l => l.Entry.Timestamp))
            Lines.Add(line);
        while (Lines.Count > MaxVisibleLines)
            Lines.RemoveAt(0);

        if (FollowOutput)
            LinesAppended?.Invoke();
    }

    private bool Matches(LogEntry entry) =>
        (!OnlyErrors || entry.Kind == LogKind.Error)
        && (Search.Length == 0 || entry.Text.Contains(Search, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private Task StopAsync(RunningItem item) => _supervisor.StopServiceAsync(item.Service.Id);

    [RelayCommand]
    private async Task RestartAsync(RunningItem item)
    {
        await _supervisor.StopServiceAsync(item.Service.Id);
        await _actions.StartServiceAsync(item.Project, item.Service);
        ReloadLines();
    }

    [RelayCommand]
    private void OpenUrl(RunningItem item) => _actions.OpenUrl(item.Service.OpenUrl);

    [RelayCommand]
    private void OpenFolder(RunningItem item) => _actions.OpenFolder(item.Service.WorkingDirectory);

    [RelayCommand]
    private void OpenVsCode(RunningItem item) => _actions.OpenInVsCode(item.Service.WorkingDirectory);

    [RelayCommand]
    private void ClearLog()
    {
        foreach (var item in ShowAll ? Items.ToList() : Selected is null ? [] : [Selected])
            _supervisor.ClearLog(item.Service.Id);
        Lines.Clear();
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (Lines.Count > 0)
            Clipboard.SetText(ExportText());
    }

    [RelayCommand]
    private void ExportLog()
    {
        if (Lines.Count == 0)
            return;

        var dialog = new SaveFileDialog { FileName = "log.txt", Filter = "Textdateien|*.txt|Alle Dateien|*.*" };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, ExportText());
            _notifier.Show("Log exportiert", dialog.FileName, InfoBarSeverity.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _notifier.Show("Export fehlgeschlagen", e.Message, InfoBarSeverity.Error);
        }
    }

    private string ExportText() => string.Join(Environment.NewLine, Lines.Select(l => l.ExportText));
}
