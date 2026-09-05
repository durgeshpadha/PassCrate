using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class LegalAcceptancePage : ContentPage
{
    public LegalAcceptancePage(LegalAcceptanceViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
