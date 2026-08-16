using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class RegistrationPage : ContentPage
{
    public RegistrationPage(RegistrationViewModel viewModel)
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
