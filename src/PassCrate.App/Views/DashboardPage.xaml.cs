using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class DashboardPage : ContentPage, IResettablePageState
{
    private readonly DashboardViewModel _viewModel;

    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    public void ResetPageState()
    {
        _viewModel.ResetState();
        Dispatcher.Dispatch(() => _ = DashboardScrollView.ScrollToAsync(0, 0, animated: false));
    }
}
