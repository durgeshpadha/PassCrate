using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class SettingsPage : ContentPage, IResettablePageState
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
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
        Dispatcher.Dispatch(() => _ = SettingsScrollView.ScrollToAsync(0, 0, animated: false));
    }
}
