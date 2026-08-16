using PassCrate.Core.Interfaces;

namespace PassCrate.App.Services;

public sealed class ApplicationResetService(
    IVaultRepository repository,
    IVaultSession session,
    IDeviceCredentialStore credentials,
    IDeviceKeyProtectionService deviceKeys,
    ISensitiveClipboardService clipboard,
    ICloudSyncService cloudSync,
    ISyncRepository syncRepository,
    ICloudAuthorizationService cloudAuthorization,
    HttpClient httpClient) : IApplicationResetService
{
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        // Destructive confirmation is owned by the caller. Once started, local
        // cleanup is deliberately non-cancellable and is retried at first launch
        // if Preferences were cleared before an interruption.
        cancellationToken.ThrowIfCancellationRequested();
        PassCrate.Core.Models.CloudProviderKind? provider = null;
        try
        {
            provider = (await syncRepository.GetConfigurationAsync(CancellationToken.None))?.Provider;
        }
        catch
        {
            // A damaged sync configuration cannot block local destruction.
        }
        string? dropboxRevocationToken = null;
        if (provider == PassCrate.Core.Models.CloudProviderKind.Dropbox)
        {
            using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                dropboxRevocationToken = await cloudAuthorization.GetAccessTokenAsync(
                    provider.Value,
                    captureTimeout.Token);
            }
            catch
            {
            }
        }
        cloudSync.CancelActiveSync();
        session.Lock();
        await clipboard.ClearAsync(CancellationToken.None);
        await repository.DeleteAllAsync(CancellationToken.None);
        await deviceKeys.DeleteAllKeysAsync(CancellationToken.None);
        await credentials.RemoveNamespaceAsync(CancellationToken.None);
        Preferences.Default.Clear();
        ClearCacheDirectory();

        try
        {
            if (provider == PassCrate.Core.Models.CloudProviderKind.Dropbox &&
                !string.IsNullOrWhiteSpace(dropboxRevocationToken))
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.dropboxapi.com/2/auth/token/revoke");
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        dropboxRevocationToken);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var response = await httpClient.SendAsync(request, timeout.Token);
            }
            else if (provider is not null)
            {
                await cloudAuthorization.DisconnectAsync(
                    provider.Value,
                    CancellationToken.None);
            }
        }
        catch
        {
            // Provider revocation is best effort and can never retain local data.
        }
    }

    private static void ClearCacheDirectory()
    {
        try
        {
            var cacheRoot = Path.GetFullPath(FileSystem.CacheDirectory);
            foreach (var file in Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories))
            {
                var resolved = Path.GetFullPath(file);
                if (resolved.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(resolved);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
