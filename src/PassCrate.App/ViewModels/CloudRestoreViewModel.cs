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
    public ObservableCollection<CloudVaultChoice> AvailableVaults { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoogleDriveSelected))]
    [NotifyPropertyChangedFor(nameof(IsDropboxSelected))]
    private string selectedProvider = "Google Drive";
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
    public bool IsGoogleDriveSelected => SelectedProvider == "Google Drive";
    public bool IsDropboxSelected => SelectedProvider == "Dropbox";

    partial void OnSelectedProviderChanged(string value)
    {
        ResetDiscoveryState();
    }

    [RelayCommand]
    private void SelectProvider(string provider) => SelectedProvider = provider;

    [RelayCommand]
    private async Task FindVaultsAsync() => await RunBusyAsync(async () =>
    {
        ResetDiscoveryState();
        VaultPassphrase = string.Empty;
        var provider = ParseProvider();
        var vaults = await cloudSync.FindVaultsAsync(provider);
        AvailableVaults.Clear();
        var orderedVaults = vaults
            .OrderByDescending(vault => vault.LastModifiedAt)
            .ThenByDescending(vault => vault.LatestGeneration)
            .ToArray();
        for (var index = 0; index < orderedVaults.Length; index++)
        {
            AvailableVaults.Add(CreateVaultChoice(
                orderedVaults[index],
                index,
                orderedVaults.Length));
        }

        SelectedVault = AvailableVaults.FirstOrDefault();
        HasSearched = true;
        HasAvailableVaults = AvailableVaults.Count > 0;
        HasNoBackups = !HasAvailableVaults;
        DiscoveryStatus = AvailableVaults.Count == 0
            ? "No PassCrate backup was found in this cloud account."
            : "Select a backup. PassCrate will restore its newest valid saved state.";
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
        await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
    }, "Unable to restore the cloud backup.");

    [RelayCommand]
    private static Task CancelAsync() => Shell.Current.GoToAsync("..");

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
        foreach (var vault in AvailableVaults)
        {
            vault.IsSelected = vault == value;
        }

        IsVaultSelectionInvalid = false;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void SelectVault(CloudVaultChoice vault) => SelectedVault = vault;

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

    private static CloudVaultChoice CreateVaultChoice(
        CloudVaultDescriptor vault,
        int index,
        int totalVaults)
    {
        var lastModified = vault.LastModifiedAt?.ToLocalTime();
        var title = totalVaults == 1
            ? "Your PassCrate backup"
            : index == 0
                ? "Most recent backup"
                : lastModified is { } timestamp
                    ? $"Backup from {timestamp:d}"
                    : $"Older backup {index}";
        var updatedText = lastModified is { } updatedAt
            ? $"Last updated {updatedAt:g}"
            : "Last updated time unavailable";
        var providerName = vault.Provider == CloudProviderKind.Dropbox ? "Dropbox" : "Google Drive";
        var isRecommended = index == 0;
        return new CloudVaultChoice(
            vault.VaultId,
            title,
            updatedText,
            providerName,
            isRecommended);
    }
}

public sealed partial class CloudVaultChoice(
    string vaultId,
    string title,
    string updatedText,
    string providerName,
    bool isRecommended) : ObservableObject
{
    public string VaultId { get; } = vaultId;
    public string Title { get; } = title;
    public string UpdatedText { get; } = updatedText;
    public string ProviderName { get; } = providerName;
    public bool IsRecommended { get; } = isRecommended;
    public string AccessibilityDescription => IsRecommended
        ? $"{Title}, recommended. {UpdatedText}. {ProviderName}. Double tap to select."
        : $"{Title}. {UpdatedText}. {ProviderName}. Double tap to select.";

    [ObservableProperty] private bool isSelected;
}
