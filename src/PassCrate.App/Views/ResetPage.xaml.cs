using PassCrate.App.ViewModels;
namespace PassCrate.App.Views;
public partial class ResetPage : ContentPage
{
    public ResetPage(ResetViewModel viewModel) { InitializeComponent(); BindingContext = viewModel; }
    private async void OnCancelClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("..");
}
