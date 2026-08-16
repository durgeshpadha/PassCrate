using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

[QueryProperty(nameof(SecretId), "id")]
public partial class SecretDetailsPage : ContentPage
{
    private readonly SecretDetailsViewModel _viewModel;
    private string secretId = string.Empty;

    public SecretDetailsPage(SecretDetailsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public string SecretId
    {
        get => secretId;
        set
        {
            secretId = value;
            _ = _viewModel.LoadAsync(value);
        }
    }

    protected override void OnDisappearing()
    {
        _viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }
}

