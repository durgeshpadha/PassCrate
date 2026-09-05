using System.ComponentModel;
using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class CloudSyncPage : ContentPage
{
    private readonly CloudSyncViewModel _viewModel;

    public CloudSyncPage(CloudSyncViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        await _viewModel.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Unload();
        _viewModel.ClearSensitiveState();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.TryReturnToResolutionChoices())
        {
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CloudSyncViewModel.ResolutionStep))
        {
            Dispatcher.Dispatch(async () =>
            {
                await Task.Yield();
                var target = _viewModel.IsCompletingResolution
                    ? ResolutionDetails
                    : ResolutionChoices;
                await PageScrollView.ScrollToAsync(target, ScrollToPosition.Start, true);
            });
        }

        if (e.PropertyName is nameof(CloudSyncViewModel.BusyMessage) or nameof(CloudSyncViewModel.IsBusy) &&
            _viewModel.IsBusy &&
            !string.IsNullOrWhiteSpace(_viewModel.BusyMessage))
        {
            SemanticScreenReader.Announce(_viewModel.BusyMessage);
        }
    }
}
