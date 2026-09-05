using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.KeyManagement;

public sealed class KeyManagementService(
    IKeyDerivationService keyDerivation,
    IKeyDerivationParameterProvider parameterProvider,
    IEncryptionService encryption,
    IVaultRepository repository,
    IVaultSession session,
    IDeviceKeyProtectionService deviceProtection,
    IDeviceCredentialStore credentialStore) : IKeyManagementService
{
    private const string DeviceKeyAlias = "passcrate.vault.device.v3";
    private const string ThrottleCredentialKey = "auth.passphrase.throttle.v3";
    private static readonly byte[] WrappedKeyAssociatedData = Encoding.UTF8.GetBytes("passcrate:wrapped-dek:v3:local");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsUnlocked => session.IsUnlocked;

    public async Task InitializeVaultAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(passphrase);
        if (await repository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A vault already exists.");

        var dek = RandomNumberGenerator.GetBytes(SecurityDefaults.KeySize);
        try { await InitializeWithDataEncryptionKeyAsync(passphrase, dek, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task InitializeRestoredVaultAsync(string passphrase, ReadOnlyMemory<byte> dataEncryptionKey, CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(passphrase);
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
            throw new ArgumentException("The recovered data encryption key is invalid.", nameof(dataEncryptionKey));
        if (await repository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false) is not null)
            throw new InvalidOperationException("A vault already exists.");
        await InitializeWithDataEncryptionKeyAsync(passphrase, dataEncryptionKey, cancellationToken).ConfigureAwait(false);
    }

    public Task<VaultMetadataV3> PrepareRestoredVaultMetadataAsync(
        string passphrase,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(passphrase);
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
            throw new ArgumentException("The recovered data encryption key is invalid.", nameof(dataEncryptionKey));

        var now = DateTimeOffset.UtcNow;
        var metadata = new VaultMetadataV3
        {
            Version = 3,
            PassphraseKdf = parameterProvider.CreateVaultPassphraseParameters(),
            DeviceProtectedWrappedDataEncryptionKey = null!,
            PlatformKeyAlias = DeviceKeyAlias,
            CreatedAt = now,
            UpdatedAt = now,
        };
        return BuildV3MetadataAsync(metadata, passphrase, dataEncryptionKey, cancellationToken);
    }

    public async Task<VaultMetadataV3> PreparePassphraseChangeMetadataAsync(
        string newPassphrase,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(newPassphrase);
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
            throw new ArgumentException("The data encryption key is invalid.", nameof(dataEncryptionKey));
        var metadata = await repository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No local vault exists.");
        return await BuildV3MetadataAsync(
            metadata,
            newPassphrase,
            dataEncryptionKey,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateRestoredVaultAsync(
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken = default)
    {
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
            throw new ArgumentException("The recovered data encryption key is invalid.", nameof(dataEncryptionKey));
        session.Unlock(dataEncryptionKey.Span);
        try
        {
            await credentialStore.RemoveAsync(ThrottleCredentialKey, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The restored vault is already valid and unlocked. A stale throttle
            // entry is conservative and can be cleared on a later successful unlock.
        }
    }

    public async Task UnlockVaultAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        var dek = await UnwrapDataEncryptionKeyAsync(passphrase, cancellationToken).ConfigureAwait(false);
        try { session.Unlock(dek); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task VerifyPassphraseAsync(string passphrase, CancellationToken cancellationToken = default)
    {
        var dek = await UnwrapDataEncryptionKeyAsync(passphrase, cancellationToken).ConfigureAwait(false);
        CryptographicOperations.ZeroMemory(dek);
    }

    public async Task ChangePassphraseAsync(string currentPassphrase, string newPassphrase, CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(newPassphrase);
        var dek = await UnwrapDataEncryptionKeyAsync(currentPassphrase, cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await repository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No local vault exists.");
            await CreateV3MetadataAsync(metadata, newPassphrase, dek, cancellationToken).ConfigureAwait(false);
            session.Unlock(dek);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public void LockVault() => session.Lock();

    private async Task InitializeWithDataEncryptionKeyAsync(string passphrase, ReadOnlyMemory<byte> dek, CancellationToken cancellationToken)
    {
        var metadata = await PrepareRestoredVaultMetadataAsync(passphrase, dek, cancellationToken).ConfigureAwait(false);
        await repository.SaveVaultMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
        await ActivateRestoredVaultAsync(dek, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> UnwrapDataEncryptionKeyAsync(string passphrase, CancellationToken cancellationToken)
    {
        CredentialPolicy.ValidateVaultPassphrase(passphrase);
        var metadata = await repository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No local vault exists.");
        await EnforceThrottleAsync(metadata, cancellationToken).ConfigureAwait(false);
        byte[]? serialized = null;
        byte[]? kek = null;
        try
        {
            if (metadata.Version != 3)
                throw new NotSupportedException("This development vault uses an obsolete security format and must be recreated.");
            try { serialized = await deviceProtection.UnprotectAsync(metadata.DeviceProtectedWrappedDataEncryptionKey, cancellationToken).ConfigureAwait(false); }
            catch (DeviceKeyUnavailableException) { throw; }
            catch (Exception exception) when (exception is CryptographicException or InvalidOperationException) { throw new DeviceKeyUnavailableException(); }

            var wrapped = JsonSerializer.Deserialize<EncryptedPayload>(serialized, JsonOptions) ?? throw new VaultCorruptedException();
            kek = await keyDerivation.DeriveKeyAsync(passphrase, metadata.PassphraseKdf.Salt, metadata.PassphraseKdf, cancellationToken).ConfigureAwait(false);
            var dek = encryption.Decrypt(wrapped, kek, WrappedKeyAssociatedData);
            await ResetThrottleAsync(metadata, cancellationToken).ConfigureAwait(false);
            return dek;
        }
        catch (CryptographicException)
        {
            await RecordFailedAttemptAsync(metadata, cancellationToken).ConfigureAwait(false);
            throw new VaultUnlockException();
        }
        finally
        {
            if (serialized is not null) CryptographicOperations.ZeroMemory(serialized);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
        }
    }

    private async Task<VaultMetadataV3> CreateV3MetadataAsync(VaultMetadataV3 metadata, string passphrase, ReadOnlyMemory<byte> dek, CancellationToken cancellationToken)
    {
        var updated = await BuildV3MetadataAsync(metadata, passphrase, dek, cancellationToken).ConfigureAwait(false);
        await repository.SaveVaultMetadataAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private async Task<VaultMetadataV3> BuildV3MetadataAsync(VaultMetadataV3 metadata, string passphrase, ReadOnlyMemory<byte> dek, CancellationToken cancellationToken)
    {
        var parameters = parameterProvider.CreateVaultPassphraseParameters();
        var kek = await keyDerivation.DeriveKeyAsync(passphrase, parameters.Salt, parameters, cancellationToken).ConfigureAwait(false);
        byte[]? serialized = null;
        try
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(encryption.Encrypt(dek.Span, kek, WrappedKeyAssociatedData), JsonOptions);
            var deviceProtected = await deviceProtection.ProtectAsync(serialized, DeviceKeyProtectionPolicy.DeviceOnly, DeviceKeyAlias, cancellationToken).ConfigureAwait(false);
            var verification = await deviceProtection.UnprotectAsync(deviceProtected, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(serialized, verification))
                    throw new CryptographicException("Device-protected envelope verification failed.");
            }
            finally { CryptographicOperations.ZeroMemory(verification); }
            return metadata with
            {
                Version = 3,
                PassphraseKdf = parameters,
                DeviceProtectedWrappedDataEncryptionKey = deviceProtected,
                PlatformKeyAlias = deviceProtected.KeyAlias,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
            if (serialized is not null) CryptographicOperations.ZeroMemory(serialized);
        }
    }

    private async Task EnforceThrottleAsync(VaultMetadataV3 metadata, CancellationToken token)
    {
        var persisted = await ReadDeviceThrottleAsync(token).ConfigureAwait(false);
        var next = Later(metadata.NextUnlockAllowedAt, persisted?.NextAllowedAt);
        if (next is DateTimeOffset allowedAt && allowedAt > DateTimeOffset.UtcNow) throw new VaultUnlockThrottledException(allowedAt);
    }

    private async Task RecordFailedAttemptAsync(VaultMetadataV3 metadata, CancellationToken token)
    {
        var deviceState = await ReadDeviceThrottleAsync(token).ConfigureAwait(false);
        var attempts = checked(Math.Max(metadata.FailedUnlockAttempts, deviceState?.FailedAttempts ?? 0) + 1);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? next = null;
        if (attempts >= 5) next = now.AddSeconds(Math.Min(900, 30 * (1L << Math.Min(attempts - 5, 30))));
        var updated = metadata with { FailedUnlockAttempts = attempts, LastFailedUnlockAt = now, NextUnlockAllowedAt = next, UpdatedAt = now };
        await repository.SaveVaultMetadataAsync(updated, token).ConfigureAwait(false);
        await WriteDeviceThrottleAsync(new UnlockThrottleState { FailedAttempts = attempts, LastFailedAt = now, NextAllowedAt = next }, token).ConfigureAwait(false);
    }

    private async Task ResetThrottleAsync(VaultMetadataV3 metadata, CancellationToken token)
    {
        if (metadata.FailedUnlockAttempts > 0 || metadata.NextUnlockAllowedAt is not null)
            await repository.SaveVaultMetadataAsync(metadata with { FailedUnlockAttempts = 0, LastFailedUnlockAt = null, NextUnlockAllowedAt = null, UpdatedAt = DateTimeOffset.UtcNow }, token).ConfigureAwait(false);
        await credentialStore.RemoveAsync(ThrottleCredentialKey, token).ConfigureAwait(false);
    }

    private async Task<UnlockThrottleState?> ReadDeviceThrottleAsync(CancellationToken token)
    {
        var json = await credentialStore.GetAsync(ThrottleCredentialKey, token).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<UnlockThrottleState>(json, JsonOptions);
    }

    private Task WriteDeviceThrottleAsync(UnlockThrottleState state, CancellationToken token) => credentialStore.SetAsync(ThrottleCredentialKey, JsonSerializer.Serialize(state, JsonOptions), token);
    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) => left is null ? right : right is null ? left : left > right ? left : right;
}
