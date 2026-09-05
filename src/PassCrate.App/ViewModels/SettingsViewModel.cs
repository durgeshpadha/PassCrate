using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using VaultAppTheme = PassCrate.Core.Models.AppTheme;

namespace PassCrate.App.ViewModels;

public sealed partial class SettingsViewModel(
    IVaultRepository repository,
    IKeyManagementService keyManagement,
    IBiometricUnlockService biometricUnlock,
    IAutoLockService autoLockService,
    ICloudSyncScheduler cloudSync,
    ICloudSyncService cloudSyncService,
    IConflictRepository conflictRepository,
    ISensitiveClipboardService clipboard) : BaseViewModel
{
    private bool isLoading;
    public IReadOnlyList<string> AutoLockOptions { get; } =
        ["When leaving the app", "1 minute", "5 minutes", "15 minutes"];

    public IReadOnlyList<string> ThemeOptions { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> ClipboardOptions { get; } = ["15 seconds", "30 seconds", "60 seconds"];

    [ObservableProperty] private bool biometricEnabled;
    [ObservableProperty] private string selectedAutoLock = "1 minute";
    [ObservableProperty] private string selectedTheme = "System";
    [ObservableProperty] private string selectedClipboardTimeout = "30 seconds";
    [ObservableProperty] private string conflictStatusText = "No unresolved sync conflicts";
    [ObservableProperty] private string cloudStatusText = "Cloud sync is off";

    public async Task LoadAsync()
    {
        isLoading = true;
        try
        {
            var settings = await repository.GetSettingsAsync();
            BiometricEnabled = settings.BiometricUnlockEnabled && await biometricUnlock.IsEnabledAsync();
            SelectedTheme = settings.Theme.ToString();
            SelectedAutoLock = settings.AutoLock switch
            {
                AutoLockTimeout.OnAppSwitch => "When leaving the app",
                AutoLockTimeout.OneMinute => "1 minute",
                AutoLockTimeout.FiveMinutes => "5 minutes",
                AutoLockTimeout.FifteenMinutes => "15 minutes",
                _ => "When leaving the app",
            };
            SelectedClipboardTimeout = settings.ClipboardClearSeconds switch
            {
                <= 15 => "15 seconds",
                >= 60 => "60 seconds",
                _ => "30 seconds",
            };
            ApplyTheme(settings.Theme);
            var conflicts = await conflictRepository.ListConflictsAsync();
            ConflictStatusText = conflicts.Count == 0
                ? "No unresolved sync conflicts"
                : $"{conflicts.Count} sync conflict(s) need review";
            CloudStatusText = cloudSyncService.Status.Message;
        }
        finally
        {
            isLoading = false;
        }
    }

    public void ResetState()
    {
        isLoading = true;
        BiometricEnabled = false;
        SelectedAutoLock = "1 minute";
        SelectedTheme = "System";
        SelectedClipboardTimeout = "30 seconds";
        ConflictStatusText = "No unresolved sync conflicts";
        CloudStatusText = "Cloud sync is off";
        ErrorMessage = string.Empty;
        isLoading = false;
    }

    partial void OnBiometricEnabledChanged(bool value)
    {
        if (!isLoading)
        {
            _ = ConfigureBiometricAsync(value);
        }
    }
    partial void OnSelectedAutoLockChanged(string value)
    {
        if (!isLoading)
        {
            SaveCommand.Execute(null);
        }
    }

    partial void OnSelectedThemeChanged(string value)
    {
        if (!isLoading)
        {
            SaveCommand.Execute(null);
        }
    }

    partial void OnSelectedClipboardTimeoutChanged(string value)
    {
        if (!isLoading)
        {
            SaveCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunBusyAsync(async () =>
    {
        var theme = Enum.TryParse<PassCrate.Core.Models.AppTheme>(SelectedTheme, true, out var parsedTheme)
            ? parsedTheme
            : VaultAppTheme.System;
        var autoLock = SelectedAutoLock switch
        {
            "When leaving the app" => AutoLockTimeout.OnAppSwitch,
            "5 minutes" => AutoLockTimeout.FiveMinutes,
            "15 minutes" => AutoLockTimeout.FifteenMinutes,
            _ => AutoLockTimeout.OneMinute,
        };
        await repository.SaveSettingsAsync(new ApplicationSettings
        {
            Theme = theme,
            AutoLock = autoLock,
            BiometricUnlockEnabled = BiometricEnabled,
            ClipboardClearSeconds = SelectedClipboardTimeout switch
            {
                "15 seconds" => 15,
                "60 seconds" => 60,
                _ => 30,
            },
        });
        autoLockService.UpdateTimeout(autoLock);
        ApplyTheme(theme);
    }, "Unable to save settings.");

    [RelayCommand]
    private static Task ChangePasswordAsync() => Shell.Current.GoToAsync(nameof(Views.ChangePasswordPage));

    [RelayCommand]
    private static Task CloudSyncAsync() => Shell.Current.GoToAsync(nameof(Views.CloudSyncPage));

    [RelayCommand]
    private static Task ReviewConflictsAsync() =>
        Shell.Current.GoToAsync(nameof(Views.ConflictReviewPage));

    [RelayCommand]
    private static Task HelpAsync() => Shell.Current.GoToAsync(nameof(Views.HelpPage));

    [RelayCommand]
    private static Task ResetAsync() => Shell.Current.GoToAsync($"{nameof(Views.ResetPage)}?next=welcome");

    [RelayCommand]
    private async Task LockAsync()
    {
        cloudSync.NotifyVaultLocked();
        keyManagement.LockVault();
        await clipboard.ClearAsync();
        await Shell.Current.GoToAsync("//unlock");
    }

    private async Task ConfigureBiometricAsync(bool enable)
    {
        if (enable)
        {
            var enabled = await biometricUnlock.EnableAsync();
            if (!enabled)
            {
                isLoading = true;
                BiometricEnabled = false;
                isLoading = false;
            }
        }
        else
        {
            await biometricUnlock.DisableAsync();
        }

        await SaveAsync();
    }

    private static void ApplyTheme(PassCrate.Core.Models.AppTheme theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.UserAppTheme = theme switch
        {
            VaultAppTheme.Light => Microsoft.Maui.ApplicationModel.AppTheme.Light,
            VaultAppTheme.Dark => Microsoft.Maui.ApplicationModel.AppTheme.Dark,
            _ => Microsoft.Maui.ApplicationModel.AppTheme.Unspecified,
        };
    }
}
