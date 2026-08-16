using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class CloudRestorePage : ContentPage
{
    public CloudRestorePage(CloudRestoreViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    protected override void OnDisappearing()
    {
        (BindingContext as ISensitiveStateViewModel)?.ClearSensitiveState();
        base.OnDisappearing();
    }
}
