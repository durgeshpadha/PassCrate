using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class CloudRestoreViewModel(
    ICloudSyncService cloudSync,
    ICloudSyncScheduler scheduler) : BaseViewModel, ISensitiveStateViewModel
{
    public IReadOnlyList<string> ProviderOptions { get; } = ["Google Drive", "Dropbox"];
    public ObservableCollection<CloudVaultChoice> AvailableVaults { get; } = [];

    [ObservableProperty] private string selectedProvider = "Google Drive";
    [ObservableProperty] private CloudVaultChoice? selectedVault;
    [ObservableProperty] private string vaultPassphrase = string.Empty;
    [ObservableProperty] private string discoveryStatus = "Connect Google Drive or Dropbox to find your backup.";
    [ObservableProperty] private bool isVaultSelectionInvalid;
    [ObservableProperty] private bool isVaultPassphraseInvalid;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotSearched))]
    private bool hasSearched;
    [ObservableProperty] private bool hasAvailableVaults;
    [ObservableProperty] private bool hasNoBackups;

    public bool HasNotSearched => !HasSearched;

    partial void OnSelectedProviderChanged(string value)
    {
        ResetDiscoveryState();
    }

    [RelayCommand]
    private async Task FindVaultsAsync() => await RunBusyAsync(async () =>
    {
        ResetDiscoveryState();
        VaultPassphrase = string.Empty;
        var provider = ParseProvider();
        var vaults = await cloudSync.FindVaultsAsync(provider);
        AvailableVaults.Clear();
        foreach (var vault in vaults)
        {
            AvailableVaults.Add(new CloudVaultChoice(
                vault.VaultId,
                $"Backup {vault.VaultId[..Math.Min(8, vault.VaultId.Length)]} · {vault.SnapshotCount} saved version(s)"));
        }

        SelectedVault = AvailableVaults.FirstOrDefault();
        HasSearched = true;
        HasAvailableVaults = AvailableVaults.Count > 0;
        HasNoBackups = !HasAvailableVaults;
        DiscoveryStatus = AvailableVaults.Count == 0
            ? "No PassCrate backup was found in this cloud account."
            : "Select the backup you want to restore.";
    }, "Unable to find cloud backups.");

    [RelayCommand]
    private async Task RestoreAsync() => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (SelectedVault is null)
        {
            IsVaultSelectionInvalid = true;
            throw new UserInputValidationException("Select a cloud backup to restore.");
        }

        if (string.IsNullOrWhiteSpace(VaultPassphrase))
        {
            IsVaultPassphraseInvalid = true;
            throw new UserInputValidationException("Enter the vault passphrase used for this backup.");
        }

        try
        {
            await cloudSync.RestoreAsync(new CloudRestoreRequest
            {
                Provider = ParseProvider(),
                VaultId = SelectedVault.VaultId,
                VaultPassphrase = VaultPassphrase,
            });
        }
        catch (Exception exception) when (exception is CredentialValidationException or CloudRestoreFailedException)
        {
            IsVaultPassphraseInvalid = true;
            throw;
        }
        ClearSensitiveState();
        scheduler.NotifyVaultUnlocked();
        await Shell.Current.GoToAsync("//main/dashboard");
    }, "Unable to restore the cloud backup.");

    [RelayCommand]
    private static Task CancelAsync() => Shell.Current.GoToAsync("//welcome");

    private CloudProviderKind ParseProvider() => SelectedProvider == "Dropbox"
        ? CloudProviderKind.Dropbox
        : CloudProviderKind.GoogleDrive;

    public void ClearSensitiveState()
    {
        VaultPassphrase = string.Empty;
        SelectedVault = null;
        ResetDiscoveryState();
        ClearValidationState();
    }

    partial void OnSelectedVaultChanged(CloudVaultChoice? value)
    {
        IsVaultSelectionInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnVaultPassphraseChanged(string value)
    {
        IsVaultPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    private void ClearValidationState()
    {
        IsVaultSelectionInvalid = false;
        IsVaultPassphraseInvalid = false;
    }

    private void ResetDiscoveryState()
    {
        AvailableVaults.Clear();
        SelectedVault = null;
        HasSearched = false;
        HasAvailableVaults = false;
        HasNoBackups = false;
        DiscoveryStatus = "Choose a cloud service, then tap Connect and find backups.";
    }
}

public sealed record CloudVaultChoice(string VaultId, string DisplayName);
