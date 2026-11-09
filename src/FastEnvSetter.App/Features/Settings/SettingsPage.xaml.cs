using System.Windows.Controls;

namespace FastEnvSetter.App;

public partial class SettingsPage : UserControl, INavigationTarget
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OnNavigatedTo(object? parameter) => _viewModel.ReloadThemes();
}
