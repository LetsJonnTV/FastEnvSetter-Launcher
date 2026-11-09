using System.Windows.Controls;

namespace FastEnvSetter.App;

public partial class PluginsPage : UserControl, INavigationTarget
{
    private readonly PluginsViewModel _viewModel;

    public PluginsPage(PluginsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OnNavigatedTo(object? parameter) => _viewModel.Reload();
}
