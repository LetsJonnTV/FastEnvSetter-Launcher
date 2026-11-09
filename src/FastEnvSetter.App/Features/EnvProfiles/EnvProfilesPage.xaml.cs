using System.Windows.Controls;

namespace FastEnvSetter.App;

public partial class EnvProfilesPage : UserControl, INavigationTarget
{
    private readonly EnvProfilesViewModel _viewModel;

    public EnvProfilesPage(EnvProfilesViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OnNavigatedTo(object? parameter) => _viewModel.Reload();
}
