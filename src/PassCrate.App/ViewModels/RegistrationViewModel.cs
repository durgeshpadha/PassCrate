using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class RegistrationViewModel(
    IKeyManagementService keyManagement,
    IVaultService vaultService,
    IBiometricUnlockService biometricUnlock) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string passphrase = string.Empty;
    [ObservableProperty] private string confirmPassphrase = string.Empty;
    [ObservableProperty] private bool acceptsRecoveryWarning;
    [ObservableProperty] private bool isPassphraseInvalid;
    [ObservableProperty] private bool isConfirmPassphraseInvalid;
    [ObservableProperty] private bool isRecoveryWarningInvalid;

    [RelayCommand]
    private async Task CreateVaultAsync() => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (string.IsNullOrWhiteSpace(Passphrase))
        {
            IsPassphraseInvalid = true;
            throw new UserInputValidationException("Enter a vault passphrase.");
        }

        try
        {
            CredentialPolicy.ValidateVaultPassphrase(Passphrase);
        }
        catch (CredentialValidationException)
        {
            IsPassphraseInvalid = true;
            throw;
        }

        if (string.IsNullOrWhiteSpace(ConfirmPassphrase))
        {
            IsConfirmPassphraseInvalid = true;
            throw new UserInputValidationException("Enter the passphrase again to confirm it.");
        }

        if (Passphrase != ConfirmPassphrase)
        {
            IsConfirmPassphraseInvalid = true;
            throw new UserInputValidationException("The passphrases do not match. Re-enter the confirmation.");
        }

        if (!AcceptsRecoveryWarning)
        {
            IsRecoveryWarningInvalid = true;
            throw new UserInputValidationException(
                "Check the box to confirm that you understand the passphrase cannot be recovered.");
        }

        // Keychain entries can survive an iOS uninstall. A new local account must
        // never inherit a biometric-wrapped DEK from an earlier installation.
        await biometricUnlock.DisableAsync();
        await keyManagement.InitializeVaultAsync(Passphrase);
        await SeedDefaultGroupsAsync();
        ClearSensitiveState();
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
    }, "Unable to create your local vault.");

    [RelayCommand]
    private static Task CancelAsync() => Shell.Current.GoToAsync("//welcome");

    private async Task SeedDefaultGroupsAsync()
    {
        if ((await vaultService.GetGroupsAsync()).Count > 0)
        {
            return;
        }

        await vaultService.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        await vaultService.CreateGroupAsync("Banking", "▰", "#10B981");
        await vaultService.CreateGroupAsync("API Keys", "⌁", "#F59E0B");
        await vaultService.CreateGroupAsync("Other", "◇", "#64748B");
    }

    public void ClearSensitiveState()
    {
        Passphrase = string.Empty;
        ConfirmPassphrase = string.Empty;
        AcceptsRecoveryWarning = false;
        ClearValidationState();
    }

    partial void OnPassphraseChanged(string value)
    {
        IsPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnConfirmPassphraseChanged(string value)
    {
        IsConfirmPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnAcceptsRecoveryWarningChanged(bool value)
    {
        IsRecoveryWarningInvalid = false;
        ErrorMessage = string.Empty;
    }

    private void ClearValidationState()
    {
        IsPassphraseInvalid = false;
        IsConfirmPassphraseInvalid = false;
        IsRecoveryWarningInvalid = false;
    }
}
