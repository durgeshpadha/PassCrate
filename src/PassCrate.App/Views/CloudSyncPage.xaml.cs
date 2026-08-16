using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class CloudSyncPage : ContentPage
{
    private readonly CloudSyncViewModel _viewModel;

    public CloudSyncPage(CloudSyncViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        _viewModel.Unload();
        _viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }
}
