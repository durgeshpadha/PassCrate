using System.Text;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Sync;

public sealed class CloudBackupDiscoveryTests
{
    [Fact]
    public async Task FindVaults_UsesLatestProviderTimestampAndOrdersNewestFirst()
    {
        var older = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 9, 5, 9, 30, 0, TimeSpan.Zero);
        var files = new[]
        {
            new CloudFileInfo("vault-a-1", "opaque-a1", 1, older),
            new CloudFileInfo("vault-a-2", "opaque-a2", 1, newer),
            new CloudFileInfo("vault-b-1", "opaque-b1", 1, older.AddDays(5)),
        };
        var snapshots = new Dictionary<string, CloudVaultSnapshotV2>(StringComparer.Ordinal)
        {
            ["vault-a-1"] = Snapshot("vault-a", "snapshot-a1", generation: 1),
            ["vault-a-2"] = Snapshot("vault-a", "snapshot-a2", generation: 2),
            ["vault-b-1"] = Snapshot("vault-b", "snapshot-b1", generation: 8),
        };
        var storage = new DiscoveryStorageProvider(files);
        var service = new CloudSyncService(
            null!,
            null!,
            null!,
            null!,
            null!,
            new DiscoverySnapshotService(snapshots),
            null!,
            new DiscoveryProviderFactory(storage),
            new DiscoveryAuthorizationService(),
            new DiscoveryNetworkPolicy());

        var found = await service.FindVaultsAsync(CloudProviderKind.GoogleDrive);

        Assert.Equal(2, found.Count);
        Assert.Equal("vault-a", found[0].VaultId);
        Assert.Equal(newer, found[0].LastModifiedAt);
        Assert.Equal(2, found[0].SnapshotCount);
        Assert.Equal("vault-b", found[1].VaultId);
    }

    private static CloudVaultSnapshotV2 Snapshot(string vaultId, string snapshotId, long generation) => new()
    {
        Header = new CloudVaultSnapshotHeaderV2
        {
            VaultId = vaultId,
            SnapshotId = snapshotId,
            Generation = generation,
            Recovery = new RecoveryKeyEnvelope
            {
                RecoveryKdf = new KeyDerivationParameters
                {
                    Algorithm = SecurityAlgorithms.Pbkdf2Sha256,
                    Salt = new byte[16],
                    Iterations = 100_000,
                },
                WrappedDataEncryptionKey = new EncryptedPayload
                {
                    Nonce = new byte[12],
                    Ciphertext = new byte[32],
                    AuthenticationTag = new byte[16],
                },
            },
        },
        EncryptedVaultState = new EncryptedPayload
        {
            Nonce = [],
            Ciphertext = [],
            AuthenticationTag = [],
        },
    };

    private sealed class DiscoveryStorageProvider(IReadOnlyList<CloudFileInfo> files) : ICloudStorageProvider
    {
        public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;

        public Task<IReadOnlyList<CloudFileInfo>> ListAsync(
            int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
            CancellationToken cancellationToken = default) => Task.FromResult(files);

        public Task<Stream> DownloadAsync(
            string providerFileId,
            long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(providerFileId)));

        public Task<CloudFileInfo> UploadImmutableAsync(
            string opaqueName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DiscoverySnapshotService(
        IReadOnlyDictionary<string, CloudVaultSnapshotV2> snapshots) : ICloudSnapshotService
    {
        public CloudVaultSnapshotV2 DeserializeSnapshotV2(ReadOnlySpan<byte> content) =>
            snapshots[Encoding.UTF8.GetString(content)];

        public Task<RecoveryKeyEnvelope> CreateRecoveryEnvelopeAsync(
            string vaultId,
            string vaultPassphrase,
            ReadOnlyMemory<byte> dataEncryptionKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public byte[] SerializeEncryptedSnapshot(
            CloudVaultSnapshotHeaderV2 header,
            CloudVaultStateV2 state,
            ReadOnlyMemory<byte> dataEncryptionKey) => throw new NotSupportedException();

        public CloudVaultStateV2 DecryptState(
            CloudVaultSnapshotV2 snapshot,
            ReadOnlyMemory<byte> dataEncryptionKey) => throw new NotSupportedException();

        public Task<byte[]> RecoverDataEncryptionKeyAsync(
            CloudVaultSnapshotV2 snapshot,
            string vaultPassphrase,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class DiscoveryProviderFactory(ICloudStorageProvider provider) : ICloudStorageProviderFactory
    {
        public ICloudStorageProvider Get(CloudProviderKind providerKind) => provider;
    }

    private sealed class DiscoveryAuthorizationService : ICloudAuthorizationService
    {
        public Task AuthorizeAsync(CloudProviderKind provider, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(
            CloudProviderKind provider,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DisconnectAsync(CloudProviderKind provider, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DiscoveryNetworkPolicy : ICloudNetworkPolicy
    {
        public bool IsInternetAvailable => true;
        public bool IsUsingWifi => true;
    }
}
