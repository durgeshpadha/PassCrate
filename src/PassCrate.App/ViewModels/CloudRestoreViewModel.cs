using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

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
    [ObservableProperty] private string discoveryStatus = "Authorize a provider to find your encrypted vault.";

    partial void OnSelectedProviderChanged(string value)
    {
        AvailableVaults.Clear();
        SelectedVault = null;
        DiscoveryStatus = "Authorize this provider to find your encrypted vault.";
    }

    [RelayCommand]
    private async Task FindVaultsAsync() => await RunBusyAsync(async () =>
    {
        var provider = ParseProvider();
        var vaults = await cloudSync.FindVaultsAsync(provider);
        AvailableVaults.Clear();
        foreach (var vault in vaults)
        {
            AvailableVaults.Add(new CloudVaultChoice(
                vault.VaultId,
                $"Vault {vault.VaultId[..Math.Min(8, vault.VaultId.Length)]} · {vault.SnapshotCount} snapshot(s)"));
        }

        SelectedVault = AvailableVaults.FirstOrDefault();
        DiscoveryStatus = AvailableVaults.Count == 0
            ? "No PassCrate cloud vault was found in this provider account."
            : "Select the vault to restore.";
    }, "Unable to find cloud vaults.");

    [RelayCommand]
    private async Task RestoreAsync() => await RunBusyAsync(async () =>
    {
        if (SelectedVault is null)
        {
            throw new InvalidOperationException("Select a cloud vault to restore.");
        }

        await cloudSync.RestoreAsync(new CloudRestoreRequest
        {
            Provider = ParseProvider(),
            VaultId = SelectedVault.VaultId,
            VaultPassphrase = VaultPassphrase,
        });
        ClearSensitiveState();
        scheduler.NotifyVaultUnlocked();
        await Shell.Current.GoToAsync("//main/dashboard");
    }, "Unable to restore the cloud vault.");

    [RelayCommand]
    private static Task CancelAsync() => Shell.Current.GoToAsync("//welcome");

    private CloudProviderKind ParseProvider() => SelectedProvider == "Dropbox"
        ? CloudProviderKind.Dropbox
        : CloudProviderKind.GoogleDrive;

    public void ClearSensitiveState()
    {
        VaultPassphrase = string.Empty;
        SelectedVault = null;
    }
}

public sealed record CloudVaultChoice(string VaultId, string DisplayName);
