using System.Text;
using Foundation;
using PassCrate.Core.Interfaces;
using Security;

namespace PassCrate.App;

public sealed class IosDeviceCredentialStore : IDeviceCredentialStore
{
    private const string KeychainService = "com.passcrate.credentials.v2";

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = CreateQuery(key);
        var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound)
        {
            return Task.FromResult<string?>(null);
        }

        if (status != SecStatusCode.Success || data is null)
        {
            throw new InvalidOperationException("iOS Keychain credential access failed.");
        }

        using (data)
        {
            return Task.FromResult<string?>(Encoding.UTF8.GetString(data.ToArray()));
        }
    }

    public Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = CreateQuery(key);
        _ = SecKeyChain.Remove(query);
        query.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
        query.ValueData = NSData.FromArray(Encoding.UTF8.GetBytes(value));
        if (SecKeyChain.Add(query) != SecStatusCode.Success)
        {
            throw new InvalidOperationException("iOS Keychain credential storage failed.");
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = CreateQuery(key);
        _ = SecKeyChain.Remove(query);
        return Task.CompletedTask;
    }

    public Task RemoveNamespaceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = KeychainService,
        };
        _ = SecKeyChain.Remove(query);
        return Task.CompletedTask;
    }

    private static SecRecord CreateQuery(string key) => new(SecKind.GenericPassword)
    {
        Service = KeychainService,
        Account = key,
    };
}
