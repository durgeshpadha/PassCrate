using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public sealed partial class ResetViewModel(IApplicationResetService resetService) : BaseViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private string confirmation = string.Empty;

    private bool CanReset() => Confirmation == "RESET";

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync() => await RunBusyAsync(async () =>
    {
        await resetService.ResetAsync();
        Confirmation = string.Empty;
        await Shell.Current.GoToAsync("//welcome");
    }, "Unable to reset PassCrate.");
}
