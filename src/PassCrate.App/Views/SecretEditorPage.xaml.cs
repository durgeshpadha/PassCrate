using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class SecretEditorPage : ContentPage, IQueryAttributable
{
    private readonly SecretEditorViewModel _viewModel;

    public SecretEditorPage(SecretEditorViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        query.TryGetValue("id", out var id);
        query.TryGetValue("groupId", out var groupId);
        _ = _viewModel.LoadAsync(id?.ToString(), groupId?.ToString());
    }

    protected override void OnDisappearing()
    {
        _viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }
}

