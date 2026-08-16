using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

[QueryProperty(nameof(GroupId), "id")]
public partial class GroupDetailsPage : ContentPage
{
    private readonly GroupDetailsViewModel _viewModel;
    private string groupId = string.Empty;

    public GroupDetailsPage(GroupDetailsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public string GroupId
    {
        get => groupId;
        set
        {
            groupId = value;
            _ = _viewModel.LoadAsync(value);
        }
    }
}
