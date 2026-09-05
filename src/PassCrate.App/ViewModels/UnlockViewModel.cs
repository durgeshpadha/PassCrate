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
    ICloudSyncScheduler cloudSyncScheduler,
    ICloudSyncService cloudSync,
    CloudSyncSetupContinuation setupContinuation) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string passphrase = string.Empty;
    [ObservableProperty] private bool isBiometricAvailable;
    [ObservableProperty] private bool isPassphraseInvalid;
    [ObservableProperty] private bool isDeviceRecoveryRequired;
    [ObservableProperty] private bool isCloudSetupPending;
    [ObservableProperty] private string cloudSetupMessage = string.Empty;

    public async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        IsDeviceRecoveryRequired = false;
        if (!await EnsureLocalVaultExistsAsync())
        {
            return;
        }

        IsBiometricAvailable = await biometricUnlock.IsEnabledAsync();
        IsCloudSetupPending = setupContinuation.TryGetProvider(out var pendingProvider);
        CloudSetupMessage = IsCloudSetupPending
            ? $"Unlock to finish connecting {ProviderName(pendingProvider)}. Your cloud permission was approved."
            : string.Empty;
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
        var resumeCloudSetup = setupContinuation.TryGetProvider(out var pendingProvider);
        if (resumeCloudSetup)
        {
            try
            {
                await cloudSync.EnableAsync(pendingProvider, Passphrase);
                setupContinuation.Clear();
            }
            catch (CloudSetupRequiresUnlockException)
            {
                setupContinuation.WaitForUnlock(pendingProvider);
                Passphrase = string.Empty;
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Unlock succeeded. CloudSyncService has recorded the provider error,
                // which the Cloud Sync page will show with a safe retry path.
                setupContinuation.Clear();
            }
        }

        cloudSyncScheduler.NotifyVaultUnlocked();
        Passphrase = string.Empty;
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
        if (resumeCloudSetup)
        {
            await Shell.Current.GoToAsync(nameof(Views.CloudSyncPage));
        }
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

        cloudSyncScheduler.NotifyVaultUnlocked();
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
        if (setupContinuation.TryGetProvider(out _))
        {
            await Shell.Current.GoToAsync(nameof(Views.CloudSyncPage));
        }
    }, "Unable to unlock your vault.");

    [RelayCommand]
    private static Task ForgotPassphraseAsync() =>
        Shell.Current.GoToAsync($"{nameof(Views.ResetPage)}?next=welcome");

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
        IsCloudSetupPending = false;
        CloudSetupMessage = string.Empty;
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

    private static string ProviderName(PassCrate.Core.Models.CloudProviderKind provider) =>
        provider == PassCrate.Core.Models.CloudProviderKind.Dropbox ? "Dropbox" : "Google Drive";
}
