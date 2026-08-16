using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class ConflictReviewPage : ContentPage
{
    private readonly ConflictReviewViewModel _viewModel;

    public ConflictReviewPage(ConflictReviewViewModel viewModel)
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
        _viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }
}
