using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public enum CloudVaultResolutionChoice
{
    None,
    Combine,
    UseCloud,
    UsePhone,
    AnotherAccount,
}

public enum CloudVaultResolutionStep
{
    ChooseAction,
    CompleteAction,
}

public sealed partial class CloudVaultResolutionOption(
    CloudVaultResolutionChoice choice,
    string title,
    string summary,
    bool isRecommended = false,
    bool isDanger = false) : ObservableObject
{
    public CloudVaultResolutionChoice Choice { get; } = choice;
    public string Title { get; } = title;
    public string Summary { get; } = summary;
    public bool IsRecommended { get; } = isRecommended;
    public bool IsDanger { get; } = isDanger;
    public string AccessibilityDescription =>
        $"{Title}. {Summary}{(IsRecommended ? " Recommended." : string.Empty)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionGlyph))]
    private bool isSelected;

    public string SelectionGlyph => IsSelected ? "●" : "○";
}

public sealed partial class CloudSyncViewModel(
    ICloudSyncService cloudSync,
    ISyncRepository repository,
    IVaultRepository vaultRepository,
    ICloudSyncScheduler syncScheduler,
    CloudSyncSetupContinuation setupContinuation,
    IDeviceOwnerAuthenticationService deviceAuthentication,
    IBiometricUnlockService biometricUnlock,
    ISensitiveClipboardService clipboard) : BaseViewModel, ISensitiveStateViewModel
{
    private bool _isLoading;
    public ObservableCollection<CloudVaultChoice> MismatchVaults { get; } = [];
    public IReadOnlyList<CloudVaultResolutionOption> ResolutionOptions { get; } =
    [
        new(
            CloudVaultResolutionChoice.Combine,
            "Combine both vaults",
            "Keep unique items from both. This phone's version is kept when an item exists in both vaults.",
            isRecommended: true),
        new(
            CloudVaultResolutionChoice.UseCloud,
            "Use the cloud vault",
            "Replace the vault currently stored on this phone."),
        new(
            CloudVaultResolutionChoice.UsePhone,
            "Use this phone's vault",
            "Permanently replace the selected cloud vault with this phone's vault.",
            isDanger: true),
        new(
            CloudVaultResolutionChoice.AnotherAccount,
            "Choose another cloud account",
            "Disconnect this account while leaving both vaults unchanged."),
    ];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGoogleDriveSelected))]
    [NotifyPropertyChangedFor(nameof(IsDropboxSelected))]
    private string selectedProvider = "Google Drive";
    [ObservableProperty] private string vaultPassphrase = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisabled))]
    [NotifyPropertyChangedFor(nameof(ShowStandardSetup))]
    private bool isEnabled;
    [ObservableProperty] private bool wifiOnly;
    [ObservableProperty] private string statusText = "Cloud sync is off.";
    [ObservableProperty] private string lastSyncText = "Never";
    [ObservableProperty] private string conflictText = string.Empty;
    [ObservableProperty] private string deleteConfirmation = string.Empty;
    [ObservableProperty] private string deleteAllConfirmation = string.Empty;
    [ObservableProperty] private bool canReconnect;
    [ObservableProperty] private Color syncStatusColor = Color.FromArgb("#EF4444");
    [ObservableProperty] private bool isVaultPassphraseInvalid;
    [ObservableProperty] private bool isDeleteConfirmationInvalid;
    [ObservableProperty] private bool isDeleteAllConfirmationInvalid;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStandardSetup))]
    [NotifyPropertyChangedFor(nameof(ShowNormalCloudContent))]
    [NotifyPropertyChangedFor(nameof(IsChoosingResolution))]
    [NotifyPropertyChangedFor(nameof(IsCompletingResolution))]
    [NotifyPropertyChangedFor(nameof(IsCombineResolution))]
    [NotifyPropertyChangedFor(nameof(IsUseCloudResolution))]
    [NotifyPropertyChangedFor(nameof(IsUsePhoneResolution))]
    [NotifyPropertyChangedFor(nameof(IsAnotherAccountResolution))]
    private bool hasVaultMismatch;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMismatchVaultTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedMismatchVaultDetails))]
    private CloudVaultChoice? selectedMismatchVault;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingResolution))]
    [NotifyPropertyChangedFor(nameof(IsCompletingResolution))]
    [NotifyPropertyChangedFor(nameof(IsCombineResolution))]
    [NotifyPropertyChangedFor(nameof(IsUseCloudResolution))]
    [NotifyPropertyChangedFor(nameof(IsUsePhoneResolution))]
    [NotifyPropertyChangedFor(nameof(IsAnotherAccountResolution))]
    private CloudVaultResolutionStep resolutionStep = CloudVaultResolutionStep.ChooseAction;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueResolution))]
    [NotifyPropertyChangedFor(nameof(SelectedResolutionTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedResolutionSummary))]
    [NotifyPropertyChangedFor(nameof(IsCombineResolution))]
    [NotifyPropertyChangedFor(nameof(IsUseCloudResolution))]
    [NotifyPropertyChangedFor(nameof(IsUsePhoneResolution))]
    [NotifyPropertyChangedFor(nameof(IsAnotherAccountResolution))]
    private CloudVaultResolutionChoice selectedResolutionChoice = CloudVaultResolutionChoice.None;
    [ObservableProperty] private bool hasSingleMismatchVault;
    [ObservableProperty] private bool hasMultipleMismatchVaults;
    [ObservableProperty] private string busyMessage = string.Empty;
    [ObservableProperty] private string restoreCloudPassphrase = string.Empty;
    [ObservableProperty] private string replaceLocalPassphrase = string.Empty;
    [ObservableProperty] private string replaceConfirmation = string.Empty;
    [ObservableProperty] private string mergeCloudPassphrase = string.Empty;
    [ObservableProperty] private string mergeLocalPassphrase = string.Empty;
    [ObservableProperty] private bool isRestoreCloudPassphraseInvalid;
    [ObservableProperty] private bool isReplaceLocalPassphraseInvalid;
    [ObservableProperty] private bool isReplaceConfirmationInvalid;
    [ObservableProperty] private bool isMergeCloudPassphraseInvalid;
    [ObservableProperty] private bool isMergeLocalPassphraseInvalid;
    [ObservableProperty] private bool hasMergePreview;
    [ObservableProperty] private string mergePreviewText = string.Empty;

    public bool IsDisabled => !IsEnabled;
    public bool ShowStandardSetup => IsDisabled && !HasVaultMismatch;
    public bool ShowNormalCloudContent => !HasVaultMismatch;
    public bool IsGoogleDriveSelected => SelectedProvider == "Google Drive";
    public bool IsDropboxSelected => SelectedProvider == "Dropbox";
    public bool IsChoosingResolution =>
        HasVaultMismatch && ResolutionStep == CloudVaultResolutionStep.ChooseAction;
    public bool IsCompletingResolution =>
        HasVaultMismatch && ResolutionStep == CloudVaultResolutionStep.CompleteAction;
    public bool CanContinueResolution => SelectedResolutionChoice != CloudVaultResolutionChoice.None;
    public bool IsCombineResolution =>
        IsCompletingResolution && SelectedResolutionChoice == CloudVaultResolutionChoice.Combine;
    public bool IsUseCloudResolution =>
        IsCompletingResolution && SelectedResolutionChoice == CloudVaultResolutionChoice.UseCloud;
    public bool IsUsePhoneResolution =>
        IsCompletingResolution && SelectedResolutionChoice == CloudVaultResolutionChoice.UsePhone;
    public bool IsAnotherAccountResolution =>
        IsCompletingResolution && SelectedResolutionChoice == CloudVaultResolutionChoice.AnotherAccount;
    public string SelectedMismatchVaultTitle => SelectedMismatchVault?.Title ?? "Cloud vault";
    public string SelectedMismatchVaultDetails => SelectedMismatchVault is null
        ? string.Empty
        : $"{SelectedMismatchVault.UpdatedText} · {SelectedMismatchVault.ProviderName}";
    public string SelectedResolutionTitle => ResolutionOptions
        .FirstOrDefault(option => option.Choice == SelectedResolutionChoice)?.Title ?? "Choose an action";
    public string SelectedResolutionSummary => ResolutionOptions
        .FirstOrDefault(option => option.Choice == SelectedResolutionChoice)?.Summary ?? string.Empty;

    [RelayCommand]
    private void SelectProvider(string provider) => SelectedProvider = provider;

    [RelayCommand]
    private void SelectResolution(CloudVaultResolutionOption option)
    {
        if (IsBusy)
        {
            return;
        }

        SelectedResolutionChoice = option.Choice;
    }

    [RelayCommand]
    private void ContinueResolution()
    {
        if (IsBusy || !CanContinueResolution)
        {
            return;
        }

        ErrorMessage = string.Empty;
        ResolutionStep = CloudVaultResolutionStep.CompleteAction;
    }

    [RelayCommand]
    private void ChangeResolutionChoice()
    {
        if (IsBusy)
        {
            return;
        }

        ClearMismatchSensitiveState();
        ErrorMessage = string.Empty;
        ResolutionStep = CloudVaultResolutionStep.ChooseAction;
    }

    [RelayCommand]
    private async Task NavigateBackAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (TryReturnToResolutionChoices())
        {
            return;
        }

        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private static Task HelpAsync() =>
        Shell.Current.GoToAsync($"{nameof(Views.HelpPage)}?topic={Core.Help.HelpTopicIds.CloudSync}");

    public bool TryReturnToResolutionChoices()
    {
        if (IsBusy)
        {
            return true;
        }

        if (!IsCompletingResolution)
        {
            return false;
        }

        ChangeResolutionChoice();
        return true;
    }

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

        if (configuration is { IsEnabled: true } &&
            cloudSync.Status.State == CloudSyncState.Disabled &&
            cloudSync.Status.Provider is null)
        {
            ApplyStatus(new CloudSyncStatus
            {
                State = CloudSyncState.Ready,
                Provider = configuration.Provider,
                LastSuccessfulSyncAt = configuration.LastSuccessfulSyncAt,
                Message = "Ready to synchronize.",
            });
        }
        else
        {
            ApplyStatus(cloudSync.Status);
        }

        if (!IsEnabled && setupContinuation.TryGetProvider(out var pendingProvider))
        {
            SelectedProvider = ProviderName(pendingProvider);
            StatusText = $"{SelectedProvider} access is approved. Enter your vault passphrase and select Connect to finish.";
            SyncStatusColor = Color.FromArgb("#5B5CE2");
        }

        if (!IsEnabled && cloudSync.Status.State == CloudSyncState.VaultMismatch)
        {
            await LoadMismatchVaultsAsync(ParseProvider());
        }

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
    private async Task EnableAsync() => await RunCloudBusyAsync("Connecting securely…", async () =>
    {
        ClearValidationState();
        ValidateVaultPassphraseInput();
        try
        {
            var provider = ParseProvider();
            try
            {
                await cloudSync.EnableAsync(provider, VaultPassphrase);
                setupContinuation.Clear();
            }
            catch (CloudSetupRequiresUnlockException)
            {
                setupContinuation.WaitForUnlock(provider);
                return;
            }
            catch (CloudVaultMismatchException)
            {
                setupContinuation.Clear();
                BusyMessage = "Checking cloud backups…";
                await LoadMismatchVaultsAsync(provider);
                return;
            }
            catch
            {
                setupContinuation.Clear();
                throw;
            }
        }
        catch (VaultUnlockException)
        {
            IsVaultPassphraseInvalid = true;
            throw;
        }
        IsEnabled = true;
        VaultPassphrase = string.Empty;
        ApplyStatus(cloudSync.Status);
    }, "Unable to enable cloud sync.");

    [RelayCommand]
    private async Task SyncNowAsync() => await RunCloudBusyAsync("Synchronizing your vault…", async () =>
    {
        await cloudSync.SynchronizeAsync();
        ApplyStatus(cloudSync.Status);
    }, "Unable to synchronize your vault.");

    [RelayCommand]
    private async Task ReconnectAsync() => await RunCloudBusyAsync("Reconnecting securely…", async () =>
    {
        await cloudSync.ReconnectAsync();
        ApplyStatus(cloudSync.Status);
    }, "Unable to reconnect cloud sync.");

    [RelayCommand]
    private static Task ReviewConflictsAsync() =>
        Shell.Current.GoToAsync(nameof(Views.ConflictReviewPage));

    [RelayCommand]
    private async Task DisconnectAsync() => await RunCloudBusyAsync("Disconnecting cloud sync…", async () =>
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
    private async Task DeleteCloudVaultAsync() => await RunCloudBusyAsync("Deleting the cloud vault…", async () =>
    {
        ClearValidationState();
        ValidateVaultPassphraseInput();
        if (!string.Equals(DeleteConfirmation.Trim(), "DELETE CLOUD VAULT", StringComparison.Ordinal))
        {
            IsDeleteConfirmationInvalid = true;
            throw new UserInputValidationException("Type DELETE CLOUD VAULT exactly to confirm deletion.");
        }

        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Delete this cloud backup?",
                "This permanently deletes this vault's backups from the connected cloud account. Your vault on this device will not be deleted.",
                "Delete cloud backup",
                "Cancel"))
        {
            return;
        }

        try
        {
            await cloudSync.DeleteCloudVaultAsync(VaultPassphrase, DeleteConfirmation);
        }
        catch (VaultUnlockException)
        {
            IsVaultPassphraseInvalid = true;
            throw;
        }
        VaultPassphrase = string.Empty;
        DeleteConfirmation = string.Empty;
        IsEnabled = false;
        ApplyStatus(cloudSync.Status);
    }, "Unable to delete the cloud vault.");

    [RelayCommand]
    private async Task DeleteAllProviderDataAsync() => await RunCloudBusyAsync("Deleting PassCrate cloud data…", async () =>
    {
        ClearValidationState();
        ValidateVaultPassphraseInput();
        if (!string.Equals(DeleteAllConfirmation.Trim(), "DELETE ALL CLOUD VAULTS", StringComparison.Ordinal))
        {
            IsDeleteAllConfirmationInvalid = true;
            throw new UserInputValidationException("Type DELETE ALL CLOUD VAULTS exactly to confirm deletion.");
        }

        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Delete all PassCrate cloud backups?",
                "Every PassCrate backup in the connected cloud account will be removed. Your vault on this device will not be deleted.",
                "Delete all backups",
                "Cancel"))
        {
            return;
        }

        try
        {
            await cloudSync.DeleteAllProviderDataAsync(VaultPassphrase, DeleteAllConfirmation);
        }
        catch (VaultUnlockException)
        {
            IsVaultPassphraseInvalid = true;
            throw;
        }
        ClearSensitiveState();
        IsEnabled = false;
        ApplyStatus(cloudSync.Status);
    }, "Unable to delete all PassCrate cloud data.");

    [RelayCommand]
    private async Task RestoreExistingCloudVaultAsync()
    {
        ClearMismatchValidation();
        ErrorMessage = string.Empty;
        CloudRestoreRequest request;
        try
        {
            var selected = RequireMismatchVault();
            ValidatePassphrase(RestoreCloudPassphrase, "Enter the existing cloud vault passphrase.", value => IsRestoreCloudPassphraseInvalid = value);
            request = new CloudRestoreRequest
            {
                Provider = ParseProvider(),
                VaultId = selected.VaultId,
                VaultPassphrase = RestoreCloudPassphrase,
            };
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Check the cloud vault passphrase and try again.");
            return;
        }

        CloudVaultContentsSummary? summary = null;
        await RunCloudBusyAsync("Verifying the cloud vault…", async () =>
        {
            try
            {
                summary = await cloudSync.ValidateCloudVaultAsync(request);
            }
            catch (Exception exception) when (exception is CredentialValidationException or CloudRestoreFailedException)
            {
                IsRestoreCloudPassphraseInvalid = true;
                throw;
            }
        }, "Unable to verify the existing cloud vault.");

        if (summary is null)
        {
            return;
        }

        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Replace this device's vault?",
                $"The cloud backup was verified and contains {summary.GroupCount} groups and {summary.SecretCount} secrets. The current vault on this phone will be permanently deleted and replaced.",
                "Restore cloud vault",
                "Cancel"))
        {
            return;
        }

        try
        {
            await RequireDeviceOwnerAsync(
                "Verify that you own this phone before replacing its local vault.");
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Phone verification could not be completed.");
            return;
        }

        await RunCloudBusyAsync("Restoring the cloud vault…", async () =>
        {
            await biometricUnlock.DisableAsync();
            var settings = await vaultRepository.GetSettingsAsync();
            await vaultRepository.SaveSettingsAsync(settings with { BiometricUnlockEnabled = false });
            await clipboard.ClearAsync();
            try
            {
                await cloudSync.RestoreReplacingLocalAsync(request);
            }
            catch (Exception exception) when (exception is CredentialValidationException or CloudRestoreFailedException)
            {
                IsRestoreCloudPassphraseInvalid = true;
                throw;
            }

            ClearSensitiveState();
            syncScheduler.NotifyVaultUnlocked();
            await ((AppShell)Shell.Current).NavigateToFreshMainAsync();
        }, "Unable to restore the existing cloud vault.");
    }

    [RelayCommand]
    private async Task ReplaceCloudBackupAsync()
    {
        ClearMismatchValidation();
        ErrorMessage = string.Empty;
        CloudVaultReplacementRequest request;
        try
        {
            var selected = RequireMismatchVault();
            ValidatePassphrase(ReplaceLocalPassphrase, "Enter the passphrase for the current vault.", value => IsReplaceLocalPassphraseInvalid = value);
            if (!string.Equals(ReplaceConfirmation.Trim(), "REPLACE CLOUD BACKUP", StringComparison.Ordinal))
            {
                IsReplaceConfirmationInvalid = true;
                throw new UserInputValidationException("Type REPLACE CLOUD BACKUP exactly to confirm replacement.");
            }

            request = new CloudVaultReplacementRequest
            {
                Provider = ParseProvider(),
                CloudVaultId = selected.VaultId,
                LocalVaultPassphrase = ReplaceLocalPassphrase,
                TypedConfirmation = ReplaceConfirmation,
            };
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Check the required fields and try again.");
            return;
        }

        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Replace selected cloud backup?",
                "PassCrate will first upload and verify this phone's vault. It will then permanently delete the selected older cloud vault.",
                "Replace backup",
                "Cancel"))
        {
            return;
        }

        try
        {
            await RequireDeviceOwnerAsync(
                "Verify that you own this phone before replacing the cloud backup.");
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Phone verification could not be completed.");
            return;
        }

        await RunCloudBusyAsync("Uploading and verifying this vault…", async () =>
        {
            try
            {
                await cloudSync.ReplaceCloudVaultAsync(request);
            }
            catch (VaultUnlockException)
            {
                IsReplaceLocalPassphraseInvalid = true;
                throw;
            }

            FinishMismatchResolution();
        }, "Unable to replace the selected cloud backup.");
    }

    [RelayCommand]
    private async Task PreviewMergeAsync()
    {
        ClearMismatchValidation();
        try
        {
            var request = CreateMergeRequest();
            await RunCloudBusyAsync("Preparing your merge preview…", async () =>
            {
                try
                {
                    var preview = await cloudSync.PreviewMergeAsync(request);
                    MergePreviewText =
                        $"Cloud will add {preview.CloudOnlyGroups} groups and {preview.CloudOnlySecrets} secrets. " +
                        $"This phone keeps {preview.LocalOnlyGroups} local-only groups and {preview.LocalOnlySecrets} local-only secrets. " +
                        $"For {preview.LocalPreferredCollisions} overlapping items, the version on this phone will be kept.";
                    HasMergePreview = true;
                }
                catch (VaultUnlockException)
                {
                    IsMergeLocalPassphraseInvalid = true;
                    throw;
                }
                catch (CloudRestoreFailedException)
                {
                    IsMergeCloudPassphraseInvalid = true;
                    throw;
                }
            }, "Unable to preview the vault merge.");
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Check both passphrases and try again.");
        }
    }

    [RelayCommand]
    private async Task MergeCloudVaultAsync()
    {
        ClearMismatchValidation();
        ErrorMessage = string.Empty;
        if (!HasMergePreview)
        {
            SetErrorMessage(
                new UserInputValidationException("Check the passphrases and preview the merge first."),
                "Preview the merge before continuing.");
            return;
        }

        CloudVaultMergeRequest request;
        try
        {
            request = CreateMergeRequest();
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Check both passphrases and try again.");
            return;
        }

        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Merge these vaults?",
                $"{MergePreviewText} PassCrate will upload and verify the merged vault before removing the selected older cloud backup.",
                "Merge vaults",
                "Cancel"))
        {
            return;
        }

        try
        {
            await RequireDeviceOwnerAsync(
                "Verify that you own this phone before replacing the older cloud backup with the merged vault.");
        }
        catch (Exception exception)
        {
            SetErrorMessage(exception, "Phone verification could not be completed.");
            return;
        }

        await RunCloudBusyAsync("Combining and securely uploading…", async () =>
        {
            try
            {
                await cloudSync.MergeDifferentVaultAsync(request);
            }
            catch (VaultUnlockException)
            {
                IsMergeLocalPassphraseInvalid = true;
                throw;
            }
            catch (CloudRestoreFailedException)
            {
                IsMergeCloudPassphraseInvalid = true;
                throw;
            }

            FinishMismatchResolution();
        }, "Unable to merge the local and cloud vaults.");
    }

    [RelayCommand]
    private async Task UseAnotherCloudAccountAsync()
    {
        if (Shell.Current.CurrentPage is not Page page ||
            !await page.DisplayAlertAsync(
                "Use another cloud account?",
                "The current cloud account will be disconnected from this phone. Its encrypted backups will not be deleted.",
                "Switch account",
                "Cancel"))
        {
            return;
        }

        await RunCloudBusyAsync("Disconnecting this cloud account…", async () =>
        {
            await cloudSync.SwitchCloudAccountAsync(ParseProvider());
            setupContinuation.Clear();
            ResetMismatchState();
            ApplyStatus(cloudSync.Status);
        }, "Unable to switch cloud accounts.");
    }

    [RelayCommand]
    private static Task DecideLaterAsync() => Shell.Current.GoToAsync("..");

    private async Task RunCloudBusyAsync(
        string message,
        Func<Task> operation,
        string fallbackMessage)
    {
        if (IsBusy)
        {
            return;
        }

        BusyMessage = message;
        try
        {
            await RunBusyAsync(operation, fallbackMessage);
        }
        finally
        {
            BusyMessage = string.Empty;
        }
    }

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
        SyncStatusColor = status.ConflictCount > 0
            ? Color.FromArgb("#F59E0B")
            : status.State switch
            {
                CloudSyncState.Disabled => Color.FromArgb("#EF4444"),
                CloudSyncState.Ready => Color.FromArgb("#10B981"),
                CloudSyncState.Connecting or CloudSyncState.Syncing => Color.FromArgb("#5B5CE2"),
                _ => Color.FromArgb("#F59E0B"),
            };
    }

    private async Task LoadMismatchVaultsAsync(CloudProviderKind provider)
    {
        var vaults = await cloudSync.FindVaultsAsync(provider);
        MismatchVaults.Clear();
        var ordered = vaults
            .OrderByDescending(vault => vault.LastModifiedAt)
            .ThenByDescending(vault => vault.LatestGeneration)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            MismatchVaults.Add(CreateVaultChoice(ordered[index], index, ordered.Length));
        }

        SelectedMismatchVault = MismatchVaults.FirstOrDefault();
        foreach (var candidate in MismatchVaults)
        {
            candidate.IsSelected = candidate == SelectedMismatchVault;
        }

        HasSingleMismatchVault = MismatchVaults.Count == 1;
        HasMultipleMismatchVaults = MismatchVaults.Count > 1;
        ResetResolutionSelection();
        HasVaultMismatch = MismatchVaults.Count > 0;
        if (HasVaultMismatch)
        {
            StatusText = "This account contains a different PassCrate vault. Choose how you want to continue.";
            SyncStatusColor = Color.FromArgb("#F59E0B");
        }
    }

    private CloudVaultChoice RequireMismatchVault() => SelectedMismatchVault ??
        throw new UserInputValidationException("Select the cloud backup you want to use.");

    private CloudVaultMergeRequest CreateMergeRequest()
    {
        var selected = RequireMismatchVault();
        ValidatePassphrase(MergeCloudPassphrase, "Enter the existing cloud vault passphrase.", value => IsMergeCloudPassphraseInvalid = value);
        ValidatePassphrase(MergeLocalPassphrase, "Enter the passphrase for the current vault.", value => IsMergeLocalPassphraseInvalid = value);
        return new CloudVaultMergeRequest
        {
            Provider = ParseProvider(),
            CloudVaultId = selected.VaultId,
            CloudVaultPassphrase = MergeCloudPassphrase,
            LocalVaultPassphrase = MergeLocalPassphrase,
        };
    }

    private async Task RequireDeviceOwnerAsync(string reason)
    {
        var result = await deviceAuthentication.AuthenticateAsync(reason);
        if (result == DeviceOwnerAuthenticationResult.Unavailable)
        {
            throw new UserInputValidationException(
                "Set up a phone screen lock before replacing vault data.");
        }

        if (result != DeviceOwnerAuthenticationResult.Success)
        {
            throw new UserInputValidationException(
                "Phone verification was cancelled. No vault data was changed.");
        }
    }

    private static void ValidatePassphrase(
        string passphrase,
        string emptyMessage,
        Action<bool> setInvalid)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            setInvalid(true);
            throw new UserInputValidationException(emptyMessage);
        }

        try
        {
            CredentialPolicy.ValidateVaultPassphrase(passphrase);
        }
        catch (CredentialValidationException)
        {
            setInvalid(true);
            throw;
        }
    }

    private void FinishMismatchResolution()
    {
        HasVaultMismatch = false;
        IsEnabled = true;
        MismatchVaults.Clear();
        SelectedMismatchVault = null;
        HasSingleMismatchVault = false;
        HasMultipleMismatchVaults = false;
        ResetResolutionSelection();
        ClearMismatchSensitiveState();
        ApplyStatus(cloudSync.Status);
        syncScheduler.NotifyVaultUnlocked();
    }

    private void ResetMismatchState()
    {
        HasVaultMismatch = false;
        MismatchVaults.Clear();
        SelectedMismatchVault = null;
        HasSingleMismatchVault = false;
        HasMultipleMismatchVaults = false;
        ResetResolutionSelection();
        ClearMismatchSensitiveState();
    }

    private void ResetResolutionSelection()
    {
        ResolutionStep = CloudVaultResolutionStep.ChooseAction;
        SelectedResolutionChoice = CloudVaultResolutionChoice.None;
        foreach (var option in ResolutionOptions)
        {
            option.IsSelected = false;
        }
    }

    private void ClearMismatchSensitiveState()
    {
        RestoreCloudPassphrase = string.Empty;
        ReplaceLocalPassphrase = string.Empty;
        ReplaceConfirmation = string.Empty;
        MergeCloudPassphrase = string.Empty;
        MergeLocalPassphrase = string.Empty;
        MergePreviewText = string.Empty;
        HasMergePreview = false;
        ClearMismatchValidation();
    }

    private void ClearMismatchValidation()
    {
        IsRestoreCloudPassphraseInvalid = false;
        IsReplaceLocalPassphraseInvalid = false;
        IsReplaceConfirmationInvalid = false;
        IsMergeCloudPassphraseInvalid = false;
        IsMergeLocalPassphraseInvalid = false;
    }

    private void InvalidateMergePreview()
    {
        HasMergePreview = false;
        MergePreviewText = string.Empty;
    }

    private static CloudVaultChoice CreateVaultChoice(
        CloudVaultDescriptor vault,
        int index,
        int totalVaults)
    {
        var lastModified = vault.LastModifiedAt?.ToLocalTime();
        var title = totalVaults == 1
            ? "Existing cloud vault"
            : index == 0
                ? "Most recent cloud vault"
                : lastModified is { } timestamp
                    ? $"Cloud vault from {timestamp:d}"
                    : $"Older cloud vault {index}";
        return new CloudVaultChoice(
            vault.VaultId,
            title,
            lastModified is { } updatedAt
                ? $"Last updated {updatedAt:g}"
                : "Last updated time unavailable",
            ProviderName(vault.Provider),
            index == 0);
    }

    private CloudProviderKind ParseProvider() => SelectedProvider == "Dropbox"
        ? CloudProviderKind.Dropbox
        : CloudProviderKind.GoogleDrive;

    private static string ProviderName(CloudProviderKind provider) =>
        provider == CloudProviderKind.Dropbox ? "Dropbox" : "Google Drive";

    public void ClearSensitiveState()
    {
        VaultPassphrase = string.Empty;
        DeleteConfirmation = string.Empty;
        DeleteAllConfirmation = string.Empty;
        ClearMismatchSensitiveState();
        ClearValidationState();
    }

    partial void OnVaultPassphraseChanged(string value)
    {
        IsVaultPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnDeleteConfirmationChanged(string value)
    {
        IsDeleteConfirmationInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnDeleteAllConfirmationChanged(string value)
    {
        IsDeleteAllConfirmationInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnRestoreCloudPassphraseChanged(string value)
    {
        IsRestoreCloudPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnReplaceLocalPassphraseChanged(string value)
    {
        IsReplaceLocalPassphraseInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnReplaceConfirmationChanged(string value)
    {
        IsReplaceConfirmationInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnMergeCloudPassphraseChanged(string value)
    {
        IsMergeCloudPassphraseInvalid = false;
        InvalidateMergePreview();
        ErrorMessage = string.Empty;
    }

    partial void OnMergeLocalPassphraseChanged(string value)
    {
        IsMergeLocalPassphraseInvalid = false;
        InvalidateMergePreview();
        ErrorMessage = string.Empty;
    }

    partial void OnSelectedResolutionChoiceChanged(CloudVaultResolutionChoice value)
    {
        foreach (var option in ResolutionOptions)
        {
            option.IsSelected = option.Choice == value;
        }

        ErrorMessage = string.Empty;
    }

    partial void OnSelectedMismatchVaultChanged(CloudVaultChoice? value)
    {
        OnPropertyChanged(nameof(SelectedMismatchVaultTitle));
        OnPropertyChanged(nameof(SelectedMismatchVaultDetails));
        foreach (var candidate in MismatchVaults)
        {
            candidate.IsSelected = candidate == value;
        }

        if (HasVaultMismatch)
        {
            ClearMismatchSensitiveState();
            ResetResolutionSelection();
            ErrorMessage = string.Empty;
        }
    }

    private void ValidateVaultPassphraseInput()
    {
        try
        {
            CredentialPolicy.ValidateVaultPassphrase(VaultPassphrase);
        }
        catch (CredentialValidationException)
        {
            IsVaultPassphraseInvalid = true;
            throw;
        }
    }

    private void ClearValidationState()
    {
        IsVaultPassphraseInvalid = false;
        IsDeleteConfirmationInvalid = false;
        IsDeleteAllConfirmationInvalid = false;
    }
}
