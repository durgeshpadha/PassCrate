using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Encryption;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Sync;

public sealed class CloudSnapshotV2Tests
{
    [Fact]
    public async Task Snapshot_IsPrivateAuthenticatedAndRecoverable()
    {
        var encryption = new AesGcmEncryptionService();
        var service = new CloudSnapshotService(
            new KeyDerivationService(),
            new TestParameterProvider(),
            encryption);
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            const string vaultId = "0123456789abcdef0123456789abcdef";
            const string passphrase = "correct horse battery staple";
            var recovery = await service.CreateRecoveryEnvelopeAsync(vaultId, passphrase, key);
            var state = CreateSensitiveState(key, encryption);
            var header = new CloudVaultSnapshotHeaderV2
            {
                VaultId = vaultId,
                SnapshotId = "11111111111111111111111111111111",
                Generation = 1,
                Recovery = recovery,
            };

            var firstBytes = service.SerializeEncryptedSnapshot(header, state, key);
            var secondBytes = service.SerializeEncryptedSnapshot(
                header with { SnapshotId = "22222222222222222222222222222222" },
                state,
                key);
            var first = service.DeserializeSnapshotV2(firstBytes);
            var second = service.DeserializeSnapshotV2(secondBytes);

            Assert.NotEqual(first.EncryptedVaultState.Nonce, second.EncryptedVaultState.Nonce);
            Assert.Equal(
                Assert.Single(state.Groups).RecordId,
                Assert.Single(service.DecryptState(first, key).Groups).RecordId);
            var recoveredKey = await service.RecoverDataEncryptionKeyAsync(first, passphrase);
            try
            {
                Assert.Equal(key, recoveredKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveredKey);
            }

            var serialized = Encoding.UTF8.GetString(firstBytes);
            foreach (var canary in new[]
                     {
                         "Private Banking", "Production API key", "Account number",
                         "super-secret-value", "private note canary", "local-login-password",
                         "864209", "oauth-refresh-token", "biometric-template", "dark-theme-setting",
                     })
            {
                Assert.DoesNotContain(canary, serialized, StringComparison.OrdinalIgnoreCase);
            }

            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                service.RecoverDataEncryptionKeyAsync(first, "wrong vault passphrase"));
            Assert.ThrowsAny<CryptographicException>(() =>
                service.DecryptState(first with
                {
                    Header = first.Header with { Generation = 2 },
                }, key));

            var tamperedCiphertext = first.EncryptedVaultState.Ciphertext.ToArray();
            tamperedCiphertext[0] ^= 0x40;
            Assert.ThrowsAny<CryptographicException>(() => service.DecryptState(first with
            {
                EncryptedVaultState = first.EncryptedVaultState with { Ciphertext = tamperedCiphertext },
            }, key));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task RecoveryEnvelope_UsesFreshSaltAndNonce()
    {
        var service = new CloudSnapshotService(
            new KeyDerivationService(),
            new TestParameterProvider(),
            new AesGcmEncryptionService());
        var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            var first = await service.CreateRecoveryEnvelopeAsync(
                "0123456789abcdef0123456789abcdef",
                "a sufficiently long vault phrase",
                key);
            var second = await service.CreateRecoveryEnvelopeAsync(
                "0123456789abcdef0123456789abcdef",
                "a sufficiently long vault phrase",
                key);

            Assert.NotEqual(first.RecoveryKdf.Salt, second.RecoveryKdf.Salt);
            Assert.NotEqual(first.WrappedDataEncryptionKey.Nonce, second.WrappedDataEncryptionKey.Nonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static CloudVaultStateV2 CreateSensitiveState(
        byte[] key,
        AesGcmEncryptionService encryption)
    {
        const string groupId = "group-one";
        const string secretId = "secret-one";
        var now = DateTimeOffset.UnixEpoch;
        var group = new VaultGroup
        {
            Id = groupId,
            Name = "Private Banking",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var document = new SecretDocument
        {
            Name = "Production API key",
            Fields =
            [
                new SecretField
                {
                    Key = "Account number",
                    Value = "super-secret-value",
                    IsSensitive = true,
                },
            ],
            Notes = "private note canary",
        };
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            document,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        VaultSecret secret;
        try
        {
            secret = new VaultSecret
            {
                Id = secretId,
                GroupId = groupId,
                Name = document.Name,
                SearchableFieldNames = "Account number",
                EncryptedPayload = encryption.Encrypt(
                    plaintext,
                    key,
                    Encoding.UTF8.GetBytes($"passcrate:secret:1:{secretId}:{groupId}")),
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        var groupRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            groupId,
            new VersionVector().Increment("device-a"),
            false,
            null,
            group);
        var secretRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Secret,
            secretId,
            new VersionVector().Increment("device-a"),
            false,
            null,
            secret);
        return new CloudVaultStateV2
        {
            Groups =
            [
                new SyncRecordEnvelope<VaultGroup>
                {
                    EntityKind = SyncEntityKind.Group,
                    RecordId = groupId,
                    Revisions = [groupRevision],
                },
            ],
            Secrets =
            [
                new SyncRecordEnvelope<VaultSecret>
                {
                    EntityKind = SyncEntityKind.Secret,
                    RecordId = secretId,
                    Revisions = [secretRevision],
                },
            ],
        };
    }
}
