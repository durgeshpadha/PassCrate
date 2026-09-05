using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class ChangePasswordViewModel(ICloudSyncService cloudSync) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string currentPassphrase = string.Empty;
    [ObservableProperty] private string newPassphrase = string.Empty;
    [ObservableProperty] private string confirmNewPassphrase = string.Empty;
    [ObservableProperty] private bool isCurrentPassphraseInvalid;
    [ObservableProperty] private bool isNewPassphraseInvalid;
    [ObservableProperty] private bool isConfirmNewPassphraseInvalid;

    [RelayCommand]
    private async Task ChangeAsync() => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (string.IsNullOrWhiteSpace(CurrentPassphrase))
        {
            IsCurrentPassphraseInvalid = true;
            throw new UserInputValidationException("Enter your current vault passphrase.");
        }

        if (string.IsNullOrWhiteSpace(NewPassphrase))
        {
            IsNewPassphraseInvalid = true;
            throw new UserInputValidationException("Enter a new vault passphrase.");
        }

        if (string.IsNullOrWhiteSpace(ConfirmNewPassphrase))
        {
            IsConfirmNewPassphraseInvalid = true;
            throw new UserInputValidationException("Enter the new passphrase again to confirm it.");
        }

        if (NewPassphrase != ConfirmNewPassphrase)
        {
            IsConfirmNewPassphraseInvalid = true;
            throw new UserInputValidationException("The new passphrases do not match. Re-enter the confirmation.");
        }

        try
        {
            CredentialPolicy.ValidateVaultPassphrase(NewPassphrase);
        }
        catch (CredentialValidationException)
        {
            IsNewPassphraseInvalid = true;
            throw;
        }

        try
        {
            await cloudSync.ChangePassphraseAsync(CurrentPassphrase, NewPassphrase);
        }
        catch (VaultUnlockException)
        {
            IsCurrentPassphraseInvalid = true;
            throw;
        }
        CurrentPassphrase = NewPassphrase = ConfirmNewPassphrase = string.Empty;
        var cloudRecoveryPending = cloudSync.Status.State == Core.Models.CloudSyncState.Error &&
            cloudSync.Status.Message.Contains("Cloud recovery will retry", StringComparison.Ordinal);
        await Shell.Current.DisplayAlertAsync(
            "Passphrase changed",
            cloudRecoveryPending
                ? "Your new passphrase protects this phone. PassCrate will update cloud recovery automatically when the connection is available. Keep your previous passphrase until Cloud Sync shows Ready."
                : "Your vault passphrase now protects local and cloud recovery access.",
            "Done");
        await Shell.Current.GoToAsync("..");
    }, "Unable to change the vault passphrase.");

    public void ClearSensitiveState()
    {
        CurrentPassphrase = NewPassphrase = ConfirmNewPassphrase = string.Empty;
        ClearValidationState();
    }

    partial void OnCurrentPassphraseChanged(string value)
    {
        IsCurrentPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnNewPassphraseChanged(string value)
    {
        IsNewPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnConfirmNewPassphraseChanged(string value)
    {
        IsConfirmNewPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    private void ClearValidationState()
    {
        IsCurrentPassphraseInvalid = false;
        IsNewPassphraseInvalid = false;
        IsConfirmNewPassphraseInvalid = false;
    }
}
