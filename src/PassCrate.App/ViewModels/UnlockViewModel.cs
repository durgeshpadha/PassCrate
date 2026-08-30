using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Help;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class UnlockViewModel(
    IVaultRepository repository,
    IKeyManagementService keyManagement,
    IBiometricUnlockService biometricUnlock,
    ICloudSyncScheduler cloudSync) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string passphrase = string.Empty;
    [ObservableProperty] private bool isBiometricAvailable;
    [ObservableProperty] private bool isPassphraseInvalid;
    [ObservableProperty] private bool isDeviceRecoveryRequired;

    public async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        IsDeviceRecoveryRequired = false;
        if (!await EnsureLocalVaultExistsAsync())
        {
            return;
        }

        IsBiometricAvailable = await biometricUnlock.IsEnabledAsync();
    }

    [RelayCommand]
    private async Task UnlockAsync() => await RunBusyAsync(async () =>
    {
        if (!await EnsureLocalVaultExistsAsync())
        {
            return;
        }

        IsPassphraseInvalid = false;
        IsDeviceRecoveryRequired = false;
        if (string.IsNullOrWhiteSpace(Passphrase))
        {
            IsPassphraseInvalid = true;
            throw new UserInputValidationException("Enter your vault passphrase.");
        }

        try
        {
            await keyManagement.UnlockVaultAsync(Passphrase);
        }
        catch (DeviceKeyUnavailableException)
        {
            IsDeviceRecoveryRequired = true;
            throw;
        }
        catch (Exception exception) when (exception is CredentialValidationException or VaultUnlockException)
        {
            IsPassphraseInvalid = true;
            throw;
        }
        cloudSync.NotifyVaultUnlocked();
        Passphrase = string.Empty;
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
    }, "Unable to unlock your vault.");

    [RelayCommand]
    private async Task UnlockWithBiometricAsync() => await RunBusyAsync(async () =>
    {
        if (!await EnsureLocalVaultExistsAsync())
        {
            return;
        }

        if (!await biometricUnlock.UnlockAsync())
        {
            throw new UserInputValidationException(
                "Biometric authentication was cancelled or not recognized. Try again.");
        }

        cloudSync.NotifyVaultUnlocked();
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
    }, "Unable to unlock your vault.");

    [RelayCommand]
    private static Task ForgotPassphraseAsync() => Shell.Current.GoToAsync(nameof(Views.ResetPage));

    [RelayCommand]
    private static async Task RestoreFromCloudAsync()
    {
        var continueToReset = await Shell.Current.DisplayAlertAsync(
            "Restore from cloud",
            "The unreadable vault saved on this device must be reset before a cloud backup can be restored. Existing encrypted cloud backups will be kept. Continue to the protected reset screen?",
            "Continue",
            "Cancel");
        if (continueToReset)
        {
            await Shell.Current.GoToAsync($"{nameof(Views.ResetPage)}?next=restore");
        }
    }

    [RelayCommand]
    private static Task HelpAsync() =>
        Shell.Current.GoToAsync($"{nameof(Views.HelpPage)}?topic={HelpTopicIds.UnlockRecovery}");

    public void ClearSensitiveState()
    {
        Passphrase = string.Empty;
        IsPassphraseInvalid = false;
        IsDeviceRecoveryRequired = false;
        ErrorMessage = string.Empty;
    }

    partial void OnPassphraseChanged(string value)
    {
        IsPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    private async Task<bool> EnsureLocalVaultExistsAsync()
    {
        if (await repository.GetVaultMetadataAsync() is not null)
        {
            return true;
        }

        ClearSensitiveState();
        IsBiometricAvailable = false;
        await Shell.Current.GoToAsync("//welcome");
        return false;
    }
}
