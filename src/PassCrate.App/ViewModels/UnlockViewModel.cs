using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public sealed partial class UnlockViewModel(
    IKeyManagementService keyManagement,
    IBiometricUnlockService biometricUnlock,
    ICloudSyncScheduler cloudSync) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string passphrase = string.Empty;
    [ObservableProperty] private bool isBiometricAvailable;

    public async Task LoadAsync() =>
        IsBiometricAvailable = await biometricUnlock.IsEnabledAsync();

    [RelayCommand]
    private async Task UnlockAsync() => await RunBusyAsync(async () =>
    {
        await keyManagement.UnlockVaultAsync(Passphrase);
        cloudSync.NotifyVaultUnlocked();
        Passphrase = string.Empty;
        await Shell.Current.GoToAsync("//main/dashboard");
    }, "Unable to unlock your vault.");

    [RelayCommand]
    private async Task UnlockWithBiometricAsync() => await RunBusyAsync(async () =>
    {
        if (!await biometricUnlock.UnlockAsync())
        {
            throw new InvalidOperationException("Biometric authentication was not completed.");
        }

        cloudSync.NotifyVaultUnlocked();
        await Shell.Current.GoToAsync("//main/dashboard");
    }, "Unable to unlock your vault.");

    public void ClearSensitiveState() => Passphrase = string.Empty;
}
