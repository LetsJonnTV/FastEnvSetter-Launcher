using System.Windows.Controls;

namespace FastEnvSetter.App;

public partial class ProjectsPage : UserControl, INavigationTarget
{
    private readonly ProjectsViewModel _viewModel;

    public ProjectsPage(ProjectsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public void OnNavigatedTo(object? parameter) => _viewModel.Reload();
}
