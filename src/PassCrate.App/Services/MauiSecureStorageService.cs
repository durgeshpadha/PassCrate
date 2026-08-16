using PassCrate.Core.Interfaces;

namespace PassCrate.App.Services;

public sealed class MauiDeviceCredentialStore : IDeviceCredentialStore
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await SecureStorage.Default.GetAsync(key);
    }

    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await SecureStorage.Default.SetAsync(key, value);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.Remove(key);
        return Task.CompletedTask;
    }

    public Task RemoveNamespaceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SecureStorage.Default.RemoveAll();
        return Task.CompletedTask;
    }
}

public sealed class MauiSecureStorageService(IDeviceCredentialStore credentials) : ISecureStorageService
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        credentials.GetAsync(key, cancellationToken);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        credentials.SetAsync(key, value, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        credentials.RemoveAsync(key, cancellationToken);

    public Task RemoveAllAsync(CancellationToken cancellationToken = default) =>
        credentials.RemoveNamespaceAsync(cancellationToken);
}
