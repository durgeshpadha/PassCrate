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
        AuthenticationFailedException => "Unable to authenticate.",
        CredentialValidationException validation => validation.Message,
        VaultUnlockException => "Unable to unlock your vault.",
        VaultUnlockThrottledException throttled => throttled.Message,
        DeviceKeyUnavailableException => "This device can no longer open the local vault. Restore from cloud.",
        CloudAuthorizationRequiredException => "Reconnect your cloud provider to continue.",
        CloudQuotaException => "Your cloud provider is temporarily rate-limited or out of space.",
        CloudResourceLimitException => "Cloud cleanup is required before PassCrate can sync.",
        CloudRestoreFailedException => "The vault passphrase did not unlock a valid cloud vault.",
        CloudSnapshotUnavailableException => "No valid PassCrate cloud snapshot was found.",
        CloudVaultMismatchException => "This provider account contains a different PassCrate vault.",
        GroupNotEmptyException => "Move the secrets in this group before deleting it.",
        TimeoutException => "The operation timed out. Your local changes are safe.",
        HttpRequestException => "The cloud provider request failed. Your local changes are safe.",
        IOException => "PassCrate could not complete the local storage operation.",
        _ => "PassCrate could not complete that operation.",
    };
}

public interface IFirstLaunchSecurityService
{
    Task EnsureCleanInstallAsync(CancellationToken cancellationToken = default);
}

public sealed class FirstLaunchSecurityService(
    IDeviceCredentialStore credentials,
    IDeviceKeyProtectionService deviceKeys,
    IGoogleDrivePlatformAuthorization googleAuthorization) : IFirstLaunchSecurityService
{
    private const string InstallSentinel = "passcrate.install.cleaned.v2";

    public async Task EnsureCleanInstallAsync(CancellationToken cancellationToken = default)
    {
        if (Preferences.Default.Get(InstallSentinel, false))
        {
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

        Preferences.Default.Set(InstallSentinel, true);
    }
}
