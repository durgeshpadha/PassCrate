using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class CloudSyncViewModel(
    ICloudSyncService cloudSync,
    ISyncRepository repository) : BaseViewModel, ISensitiveStateViewModel
{
    private bool _isLoading;
    public IReadOnlyList<string> ProviderOptions { get; } = ["Google Drive", "Dropbox"];

    [ObservableProperty] private string selectedProvider = "Google Drive";
    [ObservableProperty] private string vaultPassphrase = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    private bool isEnabled;
    [ObservableProperty] private bool wifiOnly;
    [ObservableProperty] private string statusText = "Cloud sync is off.";
    [ObservableProperty] private string lastSyncText = "Never";
    [ObservableProperty] private string conflictText = string.Empty;
    [ObservableProperty] private string deleteConfirmation = string.Empty;
    [ObservableProperty] private string deleteAllConfirmation = string.Empty;
    [ObservableProperty] private bool canReconnect;

    public bool IsDisabled => !IsEnabled;

    public async Task LoadAsync()
    {
        _isLoading = true;
        var configuration = await repository.GetConfigurationAsync();
        IsEnabled = configuration?.IsEnabled == true;
        if (configuration is not null)
        {
            SelectedProvider = configuration.Provider == CloudProviderKind.GoogleDrive ? "Google Drive" : "Dropbox";
            WifiOnly = configuration.WifiOnly;
            LastSyncText = configuration.LastSuccessfulSyncAt?.ToLocalTime().ToString("g") ?? "Never";
            StatusText = cloudSync.Status.Provider == configuration.Provider
                ? cloudSync.Status.Message
                : IsEnabled ? "Ready to synchronize." : "Cloud sync is off.";
        }

        ApplyStatus(cloudSync.Status);
        cloudSync.StatusChanged += OnStatusChanged;
        _isLoading = false;
    }

    public void Unload() => cloudSync.StatusChanged -= OnStatusChanged;

    partial void OnWifiOnlyChanged(bool value)
    {
        if (!_isLoading && IsEnabled)
        {
            _ = SaveWifiOnlyAsync(value);
        }
    }

    [RelayCommand]
    private async Task EnableAsync() => await RunBusyAsync(async () =>
    {
        await cloudSync.EnableAsync(ParseProvider(), VaultPassphrase);
        IsEnabled = true;
        VaultPassphrase = string.Empty;
        ApplyStatus(cloudSync.Status);
    }, "Unable to enable cloud sync.");

    [RelayCommand]
    private async Task SyncNowAsync() => await RunBusyAsync(async () =>
    {
        await cloudSync.SynchronizeAsync();
        ApplyStatus(cloudSync.Status);
    }, "Unable to synchronize your vault.");

    [RelayCommand]
    private async Task ReconnectAsync() => await RunBusyAsync(async () =>
    {
        await cloudSync.ReconnectAsync();
        ApplyStatus(cloudSync.Status);
    }, "Unable to reconnect cloud sync.");

    [RelayCommand]
    private static Task ReviewConflictsAsync() =>
        Shell.Current.GoToAsync(nameof(Views.ConflictReviewPage));

    [RelayCommand]
    private async Task DisconnectAsync() => await RunBusyAsync(async () =>
    {
        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Disconnect cloud sync?",
                "PassCrate will stop syncing on this device. Existing encrypted cloud files will be kept.",
                "Disconnect",
                "Cancel"))
        {
            return;
        }

        await cloudSync.DisconnectAsync();
        IsEnabled = false;
        ApplyStatus(cloudSync.Status);
    }, "Unable to disconnect cloud sync.");

    [RelayCommand]
    private async Task DeleteCloudVaultAsync() => await RunBusyAsync(async () =>
    {
        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Delete encrypted cloud vault?",
                "This permanently deletes this vault's PassCrate snapshots from the connected provider. Local data remains on this device.",
                "Delete cloud vault",
                "Cancel"))
        {
            return;
        }

        await cloudSync.DeleteCloudVaultAsync(VaultPassphrase, DeleteConfirmation);
        VaultPassphrase = string.Empty;
        DeleteConfirmation = string.Empty;
        IsEnabled = false;
        ApplyStatus(cloudSync.Status);
    }, "Unable to delete the cloud vault.");

    [RelayCommand]
    private async Task DeleteAllProviderDataAsync() => await RunBusyAsync(async () =>
    {
        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Delete all PassCrate cloud data?",
                "Every PassCrate vault in this provider app folder will be removed. Provider retention policies may still apply.",
                "Delete all cloud vaults",
                "Cancel"))
        {
            return;
        }

        await cloudSync.DeleteAllProviderDataAsync(VaultPassphrase, DeleteAllConfirmation);
        ClearSensitiveState();
        IsEnabled = false;
        ApplyStatus(cloudSync.Status);
    }, "Unable to delete all PassCrate cloud data.");

    private async Task SaveWifiOnlyAsync(bool value)
    {
        try
        {
            await cloudSync.SetWifiOnlyAsync(value);
        }
        catch (Exception)
        {
            ErrorMessage = "Unable to update the cloud connection policy.";
        }
    }

    private void OnStatusChanged(object? sender, CloudSyncStatus status) =>
        MainThread.BeginInvokeOnMainThread(() => ApplyStatus(status));

    private void ApplyStatus(CloudSyncStatus status)
    {
        if (status.Provider is not null && status.Provider != ParseProvider())
        {
            return;
        }

        StatusText = status.Message;
        LastSyncText = status.LastSuccessfulSyncAt?.ToLocalTime().ToString("g") ?? LastSyncText;
        ConflictText = status.ConflictCount == 0
            ? string.Empty
            : $"{status.ConflictCount} conflict item(s) preserved for review.";
        CanReconnect = status.State == CloudSyncState.ReconnectRequired;
    }

    private CloudProviderKind ParseProvider() => SelectedProvider == "Dropbox"
        ? CloudProviderKind.Dropbox
        : CloudProviderKind.GoogleDrive;

    public void ClearSensitiveState()
    {
        VaultPassphrase = string.Empty;
        DeleteConfirmation = string.Empty;
        DeleteAllConfirmation = string.Empty;
    }
}
