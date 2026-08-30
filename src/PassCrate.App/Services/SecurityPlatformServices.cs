using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;
#if ANDROID
using Android.App;
#elif IOS
using Foundation;
#endif

namespace PassCrate.App.Services;

public sealed class LocalDataPathProvider : ILocalDataPathProvider
{
    public LocalDataPathProvider()
    {
#if ANDROID
        var directory = Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android no-backup storage is unavailable.");
#elif IOS
        var directory = NSSearchPath.GetDirectories(
            NSSearchPathDirectory.ApplicationSupportDirectory,
            NSSearchPathDomain.User)[0];
#else
        var directory = FileSystem.AppDataDirectory;
#endif
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "passcrate.db3");
        ReapplyBackupExclusion();
    }

    public string DatabasePath { get; }

    public void ReapplyBackupExclusion()
    {
#if IOS
        using var url = NSUrl.FromFilename(Path.GetDirectoryName(DatabasePath)!);
        url.SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
#endif
    }
}

public sealed class SensitiveClipboardService : ISensitiveClipboardService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _scheduledClear;
    private string? _lastWrittenValue;

    public async Task CopyAsync(
        string sensitiveValue,
        TimeSpan? clearAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sensitiveValue);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _scheduledClear?.Cancel();
            _scheduledClear?.Dispose();
            _scheduledClear = new CancellationTokenSource();
            _lastWrittenValue = sensitiveValue;
            await Clipboard.Default.SetTextAsync(sensitiveValue);
            _ = ClearAfterAsync(clearAfter ?? TimeSpan.FromSeconds(30), _scheduledClear.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _scheduledClear?.Cancel();
            var current = await Clipboard.Default.GetTextAsync();
            if (_lastWrittenValue is not null &&
                string.Equals(current, _lastWrittenValue, StringComparison.Ordinal))
            {
                await Clipboard.Default.SetTextAsync(string.Empty);
            }

            _lastWrittenValue = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ClearAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await ClearAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

public sealed class UserErrorMessageMapper : IUserErrorMessageMapper
{
    public string ToUserMessage(Exception exception) => exception switch
    {
        AuthenticationFailedException => "Authentication was not completed. Try again.",
        UserInputValidationException validation => validation.Message,
        CredentialValidationException validation => validation.Message,
        VaultUnlockException => "That passphrase did not unlock your vault. Check it and try again.",
        VaultUnlockThrottledException throttled => throttled.Message,
        DeviceKeyUnavailableException => "This device can no longer open the saved vault. Restore a cloud backup or reset PassCrate.",
        CloudProviderConfigurationException configuration => configuration.Message,
        CloudAuthorizationRequiredException => "Reconnect your Google Drive or Dropbox account to continue.",
        CloudQuotaException => "Your cloud storage is temporarily unavailable or full. Try again later or free some space.",
        CloudResourceLimitException => "PassCrate found too much cloud data to process safely. Remove old backups and try again.",
        CloudRestoreFailedException => "That passphrase could not open this cloud backup. Check it and try again.",
        CloudSnapshotUnavailableException => "No usable PassCrate backup was found in this cloud account.",
        CloudVaultMismatchException => "This cloud account already contains a backup for a different PassCrate vault.",
        GroupNotEmptyException => "Move the secrets in this group before deleting it.",
        TimeoutException => "This is taking longer than expected. Try again; your changes on this device are safe.",
        HttpRequestException => "PassCrate could not reach the cloud service. Your changes on this device are safe.",
        IOException => "PassCrate could not read or save data on this device. Try again.",
        _ => "PassCrate could not complete this action. Try again.",
    };
}

public interface IFirstLaunchSecurityService
{
    Task EnsureCleanInstallAsync(CancellationToken cancellationToken = default);
}

public sealed class FirstLaunchSecurityService(
    IDeviceCredentialStore credentials,
    IDeviceKeyProtectionService deviceKeys,
    IGoogleDrivePlatformAuthorization googleAuthorization,
    IVaultRepository repository,
    IInstallationStateStore installationState) : IFirstLaunchSecurityService
{
    public async Task EnsureCleanInstallAsync(CancellationToken cancellationToken = default)
    {
        if (installationState.IsCleanupComplete)
        {
            return;
        }

        await repository.InitializeAsync(cancellationToken);
        var hasLocalVault = await repository.GetVaultMetadataAsync(cancellationToken) is not null;
        if (!InstallationSecurityPolicy.ShouldClearDeviceSecurityState(
                installationState.IsCleanupComplete,
                hasLocalVault))
        {
            // A missing preference can follow an in-app reset or preference migration.
            // Vault metadata is authoritative: never destroy keys for an existing vault.
            installationState.MarkCleanupComplete();
            return;
        }

        await credentials.RemoveNamespaceAsync(cancellationToken);
        await deviceKeys.DeleteAllKeysAsync(cancellationToken);
        try
        {
            await googleAuthorization.DisconnectAsync(cancellationToken);
        }
        catch
        {
            // The platform may not have an active account/activity on first boot.
            // Local credential cleanup remains authoritative.
        }

        installationState.MarkCleanupComplete();
    }
}
