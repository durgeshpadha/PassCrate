using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

[QueryProperty(nameof(GroupId), "id")]
public partial class GroupEditorPage : ContentPage
{
    private readonly GroupEditorViewModel _viewModel;

    public GroupEditorPage(GroupEditorViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public string GroupId
    {
        set => _ = _viewModel.LoadAsync(value);
    }
}
