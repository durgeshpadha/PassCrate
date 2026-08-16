using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public sealed partial class RegistrationViewModel(
    IKeyManagementService keyManagement,
    IVaultService vaultService,
    IBiometricUnlockService biometricUnlock) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string passphrase = string.Empty;
    [ObservableProperty] private string confirmPassphrase = string.Empty;
    [ObservableProperty] private bool acceptsRecoveryWarning;

    [RelayCommand]
    private async Task CreateVaultAsync() => await RunBusyAsync(async () =>
    {
        if (Passphrase != ConfirmPassphrase)
        {
            throw new InvalidOperationException("Vault passphrases do not match.");
        }

        if (!AcceptsRecoveryWarning)
        {
            throw new InvalidOperationException("Confirm that you understand the recovery warning.");
        }

        // Keychain entries can survive an iOS uninstall. A new local account must
        // never inherit a biometric-wrapped DEK from an earlier installation.
        await biometricUnlock.DisableAsync();
        await keyManagement.InitializeVaultAsync(Passphrase);
        await SeedDefaultGroupsAsync();
        ClearSensitiveState();
        await Shell.Current.GoToAsync("//main/dashboard");
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
    }
}
