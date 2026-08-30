using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
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

    public async Task LoadAsync()
    {
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
        if (string.IsNullOrWhiteSpace(Passphrase))
        {
            IsPassphraseInvalid = true;
            throw new UserInputValidationException("Enter your vault passphrase.");
        }

        try
        {
            await keyManagement.UnlockVaultAsync(Passphrase);
        }
        catch (Exception exception) when (exception is CredentialValidationException or VaultUnlockException)
        {
            IsPassphraseInvalid = true;
            throw;
        }
        cloudSync.NotifyVaultUnlocked();
        Passphrase = string.Empty;
        await Shell.Current.GoToAsync("//main/dashboard");
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
        await Shell.Current.GoToAsync("//main/dashboard");
    }, "Unable to unlock your vault.");

    [RelayCommand]
    private static Task ForgotPassphraseAsync() => Shell.Current.GoToAsync(nameof(Views.ResetPage));

    public void ClearSensitiveState()
    {
        Passphrase = string.Empty;
        IsPassphraseInvalid = false;
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
