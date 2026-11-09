using System.Windows.Controls;

namespace FastEnvSetter.App;

public partial class RunningPage : UserControl, INavigationTarget
{
    private readonly RunningViewModel _viewModel;

    public RunningPage(RunningViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.LinesAppended += ScrollToEnd;
    }

    public void OnNavigatedTo(object? parameter) => _viewModel.Activate(parameter);
    public void OnNavigatedFrom() => _viewModel.Deactivate();

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
