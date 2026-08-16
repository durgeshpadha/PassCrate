using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Sync;

public sealed class CloudSnapshotService(
    IKeyDerivationService keyDerivation,
    IKeyDerivationParameterProvider parameterProvider,
    IEncryptionService encryption) : ICloudSnapshotService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = CloudResourceLimits.MaximumJsonDepth,
    };

    public async Task<RecoveryKeyEnvelope> CreateRecoveryEnvelopeAsync(
        string vaultId,
        string vaultPassphrase,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultId);
        CredentialPolicy.ValidateVaultPassphrase(vaultPassphrase);
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
        {
            throw new ArgumentException("The vault data encryption key is invalid.", nameof(dataEncryptionKey));
        }

        var parameters = parameterProvider.CreateCloudRecoveryParameters();
        var recoveryKey = await keyDerivation
            .DeriveKeyAsync(vaultPassphrase, parameters.Salt, parameters, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new RecoveryKeyEnvelope
            {
                RecoveryKdf = parameters,
                WrappedDataEncryptionKey = encryption.Encrypt(
                    dataEncryptionKey.Span,
                    recoveryKey,
                    CreateRecoveryAssociatedData(vaultId)),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }
    }

    public byte[] SerializeEncryptedSnapshot(
        CloudVaultSnapshotHeaderV2 header,
        CloudVaultStateV2 state,
        ReadOnlyMemory<byte> dataEncryptionKey)
    {
        ValidateHeader(header);
        ValidateState(state);
        if (dataEncryptionKey.Length != SecurityDefaults.KeySize)
        {
            throw new ArgumentException("The vault data encryption key is invalid.", nameof(dataEncryptionKey));
        }

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        try
        {
            var payload = encryption.Encrypt(plaintext, dataEncryptionKey.Span, headerBytes);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(new CloudVaultSnapshotV2
            {
                Header = header,
                EncryptedVaultState = payload,
            }, JsonOptions);
            if (serialized.LongLength > CloudResourceLimits.MaximumSnapshotBytes)
            {
                throw new CloudResourceLimitException("The encrypted snapshot exceeds PassCrate's size limit.");
            }

            return serialized;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public CloudVaultSnapshotV2 DeserializeSnapshotV2(ReadOnlySpan<byte> content)
    {
        if (content.Length > CloudResourceLimits.MaximumSnapshotBytes)
        {
            throw new CloudResourceLimitException("The encrypted snapshot exceeds PassCrate's size limit.");
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<CloudVaultSnapshotV2>(content, JsonOptions)
                ?? throw new InvalidDataException("The cloud snapshot is empty.");
            ValidateHeader(snapshot.Header);
            ValidatePayload(snapshot.EncryptedVaultState);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The cloud snapshot format is invalid.", exception);
        }
    }

    public CloudVaultStateV2 DecryptState(
        CloudVaultSnapshotV2 snapshot,
        ReadOnlyMemory<byte> dataEncryptionKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateHeader(snapshot.Header);
        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot.Header, JsonOptions);
        byte[]? plaintext = null;
        try
        {
            plaintext = encryption.Decrypt(snapshot.EncryptedVaultState, dataEncryptionKey.Span, headerBytes);
            var state = JsonSerializer.Deserialize<CloudVaultStateV2>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The encrypted vault state is empty.");
            ValidateState(state);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The encrypted vault state is invalid.", exception);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public async Task<byte[]> RecoverDataEncryptionKeyAsync(
        CloudVaultSnapshotV2 snapshot,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(vaultPassphrase);
        ValidateHeader(snapshot.Header);
        var parameters = snapshot.Header.Recovery.RecoveryKdf;
        var recoveryKey = await keyDerivation
            .DeriveKeyAsync(vaultPassphrase, parameters.Salt, parameters, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var dataEncryptionKey = encryption.Decrypt(
                snapshot.Header.Recovery.WrappedDataEncryptionKey,
                recoveryKey,
                CreateRecoveryAssociatedData(snapshot.Header.VaultId));
            try
            {
                _ = DecryptState(snapshot, dataEncryptionKey);
                return dataEncryptionKey;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKey);
        }
    }

    private static byte[] CreateRecoveryAssociatedData(string vaultId) =>
        Encoding.UTF8.GetBytes($"passcrate:cloud-recovery:v2:{vaultId}");

    private static void ValidateHeader(CloudVaultSnapshotHeaderV2 header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.Format != CloudVaultSnapshotHeaderV2.ExpectedFormat || header.Version != 2)
        {
            throw new NotSupportedException("Only PassCrate cloud snapshot V2 is supported.");
        }

        if (string.IsNullOrWhiteSpace(header.VaultId) || header.VaultId.Length > 64 ||
            string.IsNullOrWhiteSpace(header.SnapshotId) || header.SnapshotId.Length > 64 ||
            header.Generation < 1 ||
            header.ParentSnapshotIds.Count > 32 ||
            header.ParentSnapshotIds.Any(parent => string.IsNullOrWhiteSpace(parent) || parent.Length > 64))
        {
            throw new InvalidDataException("The cloud snapshot header is invalid.");
        }

        if (header.Recovery?.RecoveryKdf is null || header.Recovery.WrappedDataEncryptionKey is null)
        {
            throw new InvalidDataException("The recovery key envelope is missing.");
        }

        ValidatePayload(header.Recovery.WrappedDataEncryptionKey);
    }

    private static void ValidateState(CloudVaultStateV2 state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var count = checked(state.Groups.Count + state.Secrets.Count);
        if (count > CloudResourceLimits.MaximumRecordsPerSnapshot)
        {
            throw new CloudResourceLimitException("The cloud snapshot contains too many records.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var envelope in state.Groups.Cast<object>().Concat(state.Secrets))
        {
            var (kind, id, revisionIds) = envelope switch
            {
                SyncRecordEnvelope<VaultGroup> group =>
                    (group.EntityKind, group.RecordId, group.Revisions.Select(revision => revision.RevisionId)),
                SyncRecordEnvelope<VaultSecret> secret =>
                    (secret.EntityKind, secret.RecordId, secret.Revisions.Select(revision => revision.RevisionId)),
                _ => throw new InvalidDataException("The cloud snapshot contains an invalid record."),
            };
            if (string.IsNullOrWhiteSpace(id) || !ids.Add($"{(int)kind}:{id}") ||
                revisionIds.Any(string.IsNullOrWhiteSpace) ||
                revisionIds.Distinct(StringComparer.Ordinal).Count() != revisionIds.Count())
            {
                throw new InvalidDataException("The cloud snapshot record set is invalid.");
            }
        }

        foreach (var envelope in state.Groups)
        {
            foreach (var revision in envelope.Revisions)
            {
                if (revision.Payload?.Id != envelope.RecordId ||
                    (revision.IsDeleted && revision.Payload is null))
                {
                    throw new InvalidDataException("A group revision is missing required presentation metadata.");
                }

                ValidateRevisionIdentity(
                    SyncEntityKind.Group,
                    envelope.RecordId,
                    revision);
            }
        }

        foreach (var envelope in state.Secrets)
        {
            foreach (var revision in envelope.Revisions)
            {
                if ((!revision.IsDeleted && revision.Payload?.Id != envelope.RecordId) ||
                    (revision.IsDeleted && revision.Payload is not null))
                {
                    throw new InvalidDataException("A secret revision payload is invalid.");
                }

                ValidateRevisionIdentity(
                    SyncEntityKind.Secret,
                    envelope.RecordId,
                    revision);
            }
        }
    }

    private static void ValidateRevisionIdentity<T>(
        SyncEntityKind kind,
        string recordId,
        SyncRecordRevision<T> revision)
    {
        var expected = SyncRevisionFactory.Create(
            kind,
            recordId,
            revision.Vector,
            revision.IsDeleted,
            revision.DeletedAt,
            revision.Payload,
            revision.IsConflict);
        if (!StringComparer.Ordinal.Equals(expected.RevisionId, revision.RevisionId) ||
            !StringComparer.Ordinal.Equals(expected.ContentFingerprint, revision.ContentFingerprint))
        {
            throw new InvalidDataException("A sync revision fingerprint is invalid.");
        }
    }

    private static void ValidatePayload(EncryptedPayload payload)
    {
        if (payload.Algorithm != SecurityAlgorithms.Aes256Gcm ||
            payload.Nonce.Length != SecurityDefaults.GcmNonceSize ||
            payload.AuthenticationTag.Length != SecurityDefaults.GcmTagSize ||
            payload.Ciphertext.Length == 0)
        {
            throw new InvalidDataException("The cloud snapshot payload is invalid.");
        }
    }
}
