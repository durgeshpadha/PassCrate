using PassCrate.App.ViewModels;
namespace PassCrate.App.Views;
public partial class ChangePasswordPage : ContentPage
{
    public ChangePasswordPage(ChangePasswordViewModel viewModel) { InitializeComponent(); BindingContext = viewModel; }
    protected override void OnDisappearing()
    {
        (BindingContext as ISensitiveStateViewModel)?.ClearSensitiveState();
        base.OnDisappearing();
    }
}
