using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class UnlockPage : ContentPage
{
    private readonly UnlockViewModel viewModel;

    public UnlockPage(UnlockViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await viewModel.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }
}
