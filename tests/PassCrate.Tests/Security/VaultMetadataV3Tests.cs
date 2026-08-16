using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Security;

public sealed class VaultMetadataV3Tests
{
    [Fact]
    public async Task NewVaultStoresOnlyPassphraseEnvelopeMetadata()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var metadata = await context.Repository.GetVaultMetadataAsync();
        Assert.NotNull(metadata);
        Assert.Equal(3, metadata!.Version);
        Assert.NotEmpty(metadata.PassphraseKdf.Salt);
        Assert.NotEmpty(metadata.DeviceProtectedWrappedDataEncryptionKey.Payload.Ciphertext);
    }

    [Fact]
    public async Task LocalAndCloudEnvelopesUseIndependentSaltsAndContexts()
    {
        await using var context = await TestContext.CreateAsync();
        const string passphrase = "correct horse battery staple 2026";
        await context.Keys.InitializeVaultAsync(passphrase);
        var metadata = await context.Repository.GetVaultMetadataAsync();
        var dek = context.Session.UseKey(key => key.ToArray());
        try
        {
            var cloud = new CloudSnapshotService(
                context.Kdf,
                new TestParameterProvider(),
                context.Encryption);
            var recovery = await cloud.CreateRecoveryEnvelopeAsync("vault-v3-test", passphrase, dek);
            Assert.NotEqual(metadata!.PassphraseKdf.Salt, recovery.RecoveryKdf.Salt);

            var serialized = await context.DeviceProtection.UnprotectAsync(
                metadata.DeviceProtectedWrappedDataEncryptionKey);
            var localKey = await context.Kdf.DeriveKeyAsync(
                passphrase,
                metadata.PassphraseKdf.Salt,
                metadata.PassphraseKdf);
            try
            {
                var envelope = JsonSerializer.Deserialize<EncryptedPayload>(
                    serialized,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                var unwrapped = context.Encryption.Decrypt(
                    envelope,
                    localKey,
                    Encoding.UTF8.GetBytes("passcrate:wrapped-dek:v3:local"));
                try
                {
                    Assert.Equal(dek, unwrapped);
                    Assert.ThrowsAny<CryptographicException>(() => context.Encryption.Decrypt(
                        envelope,
                        localKey,
                        Encoding.UTF8.GetBytes("passcrate:cloud-recovery:v2:vault-v3-test")));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(unwrapped);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(localKey);
                CryptographicOperations.ZeroMemory(serialized);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}
