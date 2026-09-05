using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class ResetViewModel(
    IApplicationResetService resetService,
    IDeviceOwnerAuthenticationService deviceAuthentication) : BaseViewModel, IQueryAttributable
{
    private bool restoreAfterReset;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [NotifyPropertyChangedFor(nameof(IsConfirmationInvalid))]
    [NotifyPropertyChangedFor(nameof(ConfirmationMessage))]
    private string confirmation = string.Empty;

    private bool CanReset() => Confirmation == "RESET";
    public bool IsConfirmationInvalid => Confirmation.Length > 0 && Confirmation != "RESET";
    public string ConfirmationMessage => IsConfirmationInvalid
        ? "Type RESET using capital letters to enable the reset button."
        : string.Empty;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync() => await RunBusyAsync(async () =>
    {
        var authentication = await deviceAuthentication.AuthenticateAsync(
            "Use your fingerprint, face unlock, or phone PIN/password to allow permanent deletion.");
        if (authentication == DeviceOwnerAuthenticationResult.Unavailable)
        {
            throw new UserInputValidationException(
                "Set up a screen lock on this phone before resetting PassCrate.");
        }

        if (authentication != DeviceOwnerAuthenticationResult.Success)
        {
            throw new UserInputValidationException(
                "Phone verification was cancelled. Nothing was deleted.");
        }

        var continueToRestore = restoreAfterReset;
        restoreAfterReset = false;
        await resetService.ResetAsync();
        Confirmation = string.Empty;
        await ((AppShell)Shell.Current).NavigateToWelcomeAfterResetAsync();
        if (continueToRestore)
        {
            await Shell.Current.GoToAsync(nameof(Views.CloudRestorePage));
        }
    }, "Unable to reset PassCrate.");

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        restoreAfterReset = query.TryGetValue("next", out var next) &&
            string.Equals(next?.ToString(), "restore", StringComparison.OrdinalIgnoreCase);
    }
}
