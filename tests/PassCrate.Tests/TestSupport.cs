using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Database;
using PassCrate.Infrastructure.Encryption;
using PassCrate.Infrastructure.KeyManagement;
using PassCrate.Infrastructure.Services;

namespace PassCrate.Tests;

internal sealed class TestParameterProvider : IKeyDerivationParameterProvider
{
    public KeyDerivationParameters CreateVaultPassphraseParameters() => Create();

    public KeyDerivationParameters CreateCloudRecoveryParameters() => Create();

    public bool NeedsUpgrade(KeyDerivationParameters parameters) => false;

    private static KeyDerivationParameters Create() => new()
    {
        Algorithm = SecurityAlgorithms.Pbkdf2Sha256,
        Version = 1,
        Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16),
        Iterations = 100_000,
        Parallelism = 1,
        KeyLength = 32,
    };
}

internal sealed class TestDeviceCredentialStore : IDeviceCredentialStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public Task RemoveNamespaceAsync(CancellationToken cancellationToken = default)
    {
        _values.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class TestDeviceKeyProtectionService(IEncryptionService encryption) : IDeviceKeyProtectionService
{
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    public Task<DeviceProtectedPayload> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        DeviceKeyProtectionPolicy policy,
        string keyAlias,
        CancellationToken cancellationToken = default)
    {
        if (!_keys.TryGetValue(keyAlias, out var key))
        {
            key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            _keys[keyAlias] = key;
        }

        return Task.FromResult(new DeviceProtectedPayload
        {
            Policy = policy,
            KeyAlias = keyAlias,
            Payload = encryption.Encrypt(plaintext.Span, key),
        });
    }

    public Task<byte[]> UnprotectAsync(
        DeviceProtectedPayload protectedPayload,
        CancellationToken cancellationToken = default)
    {
        if (!_keys.TryGetValue(protectedPayload.KeyAlias, out var key))
        {
            throw new PassCrate.Core.Security.DeviceKeyUnavailableException();
        }

        return Task.FromResult(encryption.Decrypt(protectedPayload.Payload, key));
    }

    public Task DeleteKeyAsync(string keyAlias, CancellationToken cancellationToken = default)
    {
        if (_keys.Remove(keyAlias, out var key))
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllKeysAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in _keys.Values)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }

        _keys.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class TestContext : IAsyncDisposable
{
    private TestContext(
        string databasePath,
        SqliteVaultRepository repository,
        IKeyDerivationService kdf,
        IEncryptionService encryption,
        IVaultSession session,
        IDeviceKeyProtectionService deviceProtection,
        IDeviceCredentialStore credentials,
        IKeyManagementService keys,
        IVaultService vault)
    {
        DatabasePath = databasePath;
        Repository = repository;
        Kdf = kdf;
        Encryption = encryption;
        Session = session;
        DeviceProtection = deviceProtection;
        Credentials = credentials;
        Keys = keys;
        Vault = vault;
    }

    public string DatabasePath { get; }
    public SqliteVaultRepository Repository { get; }
    public IKeyDerivationService Kdf { get; }
    public IEncryptionService Encryption { get; }
    public IVaultSession Session { get; }
    public IDeviceKeyProtectionService DeviceProtection { get; }
    public IDeviceCredentialStore Credentials { get; }
    public IKeyManagementService Keys { get; }
    public IVaultService Vault { get; }

    public static async Task<TestContext> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"passcrate-tests-{Guid.NewGuid():N}.db3");
        var kdf = new KeyDerivationService();
        var encryption = new AesGcmEncryptionService();
        var session = new VaultSession();
        var repository = new SqliteVaultRepository(path, encryption, session);
        await repository.InitializeAsync();
        var parameters = new TestParameterProvider();
        var credentials = new TestDeviceCredentialStore();
        var deviceProtection = new TestDeviceKeyProtectionService(encryption);
        var keys = new KeyManagementService(
            kdf,
            parameters,
            encryption,
            repository,
            session,
            deviceProtection,
            credentials);
        var vault = new VaultService(repository, encryption, session, new NullCloudSyncScheduler());
        return new TestContext(
            path,
            repository,
            kdf,
            encryption,
            session,
            deviceProtection,
            credentials,
            keys,
            vault);
    }

    public async ValueTask DisposeAsync()
    {
        Session.Lock();
        await Repository.DisposeAsync();
        foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
