using PassCrate.Core.Models;

namespace PassCrate.Core.Interfaces;

public interface IKeyDerivationService
{
    Task<byte[]> DeriveKeyAsync(
        string secret,
        byte[] salt,
        KeyDerivationParameters parameters,
        CancellationToken cancellationToken = default);
}

public interface IKeyDerivationParameterProvider
{
    KeyDerivationParameters CreateVaultPassphraseParameters();
    KeyDerivationParameters CreateCloudRecoveryParameters();
    bool NeedsUpgrade(KeyDerivationParameters parameters);
}

public interface IDeviceKeyProtectionService
{
    Task<DeviceProtectedPayload> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        DeviceKeyProtectionPolicy policy,
        string keyAlias,
        CancellationToken cancellationToken = default);

    Task<byte[]> UnprotectAsync(
        DeviceProtectedPayload protectedPayload,
        CancellationToken cancellationToken = default);

    Task DeleteKeyAsync(string keyAlias, CancellationToken cancellationToken = default);
    Task DeleteAllKeysAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceCredentialStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveNamespaceAsync(CancellationToken cancellationToken = default);
}

public interface IEncryptionService
{
    EncryptedPayload Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default);

    byte[] Decrypt(
        EncryptedPayload payload,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default);
}

public interface IVaultSession
{
    bool IsUnlocked { get; }
    T UseKey<T>(Func<ReadOnlyMemory<byte>, T> operation);
    void Unlock(ReadOnlySpan<byte> dataEncryptionKey);
    void Lock();
}

public interface IKeyManagementService
{
    bool IsUnlocked { get; }
    Task InitializeVaultAsync(string passphrase, CancellationToken cancellationToken = default);
    Task InitializeRestoredVaultAsync(string passphrase, ReadOnlyMemory<byte> dataEncryptionKey, CancellationToken cancellationToken = default);
    Task<VaultMetadataV3> PrepareRestoredVaultMetadataAsync(string passphrase, ReadOnlyMemory<byte> dataEncryptionKey, CancellationToken cancellationToken = default);
    Task<VaultMetadataV3> PreparePassphraseChangeMetadataAsync(string newPassphrase, ReadOnlyMemory<byte> dataEncryptionKey, CancellationToken cancellationToken = default);
    Task ActivateRestoredVaultAsync(ReadOnlyMemory<byte> dataEncryptionKey, CancellationToken cancellationToken = default);
    Task UnlockVaultAsync(string passphrase, CancellationToken cancellationToken = default);
    Task VerifyPassphraseAsync(string passphrase, CancellationToken cancellationToken = default);
    Task ChangePassphraseAsync(string currentPassphrase, string newPassphrase, CancellationToken cancellationToken = default);
    void LockVault();
}
