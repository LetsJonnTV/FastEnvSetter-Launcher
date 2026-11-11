using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastEnvSetter.Core;

namespace FastEnvSetter.App;

public partial class ProjectEditorPage : UserControl, INavigationTarget
{
    private readonly ProjectEditorViewModel _viewModel;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public ProjectEditorPage(ProjectEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        _refreshTimer.Tick += (_, _) => _viewModel.RefreshCurrentProject();
        Loaded += (_, _) =>
        {
            _viewModel.RefreshCurrentProject();
            _refreshTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            _viewModel.Development.Stop();
        };
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is Project project)
            _viewModel.Load(project);
    }
}

public sealed class SettingFieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? EditableChoice { get; set; }
    public DataTemplate? Path { get; set; }
    public DataTemplate? Toggle { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not SettingFieldViewModel field)
            return null;

        return field.Field.Kind switch
        {
            SettingKind.Choice => field.Field.AllowCustomValue ? EditableChoice : Choice,
            SettingKind.FilePath or SettingKind.FolderPath => Path,
            SettingKind.Toggle => Toggle,
            _ => Text,
        };
    }
}
