using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Sync;

public sealed class CloudPassphraseRotationTests
{
    private const string OldPassphrase = "old correct horse battery staple 2026";
    private const string NewPassphrase = "new correct horse battery staple 2026";

    [Fact]
    public async Task Enable_WhenOAuthLocksVault_RequiresUnlockAndCompletesOnRetry()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(OldPassphrase);
        var storage = new InMemoryCloudStorageProvider();
        var authorization = new LockOnFirstAuthorizationService(context.Session);
        var sync = CreateService(context, storage, authorization);

        await Assert.ThrowsAsync<CloudSetupRequiresUnlockException>(() =>
            sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase));

        Assert.False(context.Session.IsUnlocked);
        Assert.Null(await context.Repository.GetConfigurationAsync());

        await context.Keys.UnlockVaultAsync(OldPassphrase);
        await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        var configuration = await context.Repository.GetConfigurationAsync();
        Assert.NotNull(configuration);
        Assert.True(configuration.IsEnabled);
        Assert.Equal(2, authorization.AuthorizationCount);
    }

    [Fact]
    public async Task RestoreReplacingLocal_InvalidCloudPassphrasePreservesLocalVault()
    {
        var storage = new InMemoryCloudStorageProvider();
        string cloudVaultId;
        await using (var cloudSource = await TestContext.CreateAsync())
        {
            await cloudSource.Keys.InitializeVaultAsync(OldPassphrase);
            await cloudSource.Vault.CreateGroupAsync("Cloud group", "☁", "#4F46E5");
            var cloudSync = CreateService(cloudSource, storage);
            await cloudSync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);
            cloudVaultId = (await cloudSource.Repository.GetConfigurationAsync())!.VaultId;
        }

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        await local.Vault.CreateGroupAsync("Local group", "⌂", "#10B981");
        var sync = CreateService(local, storage);
        var wrongRequest = new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = cloudVaultId,
            VaultPassphrase = "wrong cloud passphrase 123456789",
        };

        await Assert.ThrowsAsync<CloudRestoreFailedException>(() =>
            sync.ValidateCloudVaultAsync(wrongRequest));
        Assert.Equal("Local group", Assert.Single(await local.Vault.GetGroupsAsync()).Name);

        var request = wrongRequest with { VaultPassphrase = OldPassphrase };
        var summary = await sync.ValidateCloudVaultAsync(request);
        Assert.Equal(1, summary.GroupCount);
        await sync.RestoreReplacingLocalAsync(request);

        Assert.Equal("Cloud group", Assert.Single(await local.Vault.GetGroupsAsync()).Name);
        Assert.True((await local.Repository.GetConfigurationAsync())!.IsEnabled);
    }

    [Fact]
    public async Task ReplaceCloudVault_UploadsNewVaultBeforeRemovingSelectedBackup()
    {
        var storage = new InMemoryCloudStorageProvider();
        string oldCloudVaultId;
        await using (var cloudSource = await TestContext.CreateAsync())
        {
            await cloudSource.Keys.InitializeVaultAsync(OldPassphrase);
            await cloudSource.Vault.CreateGroupAsync("Old cloud group", "☁", "#4F46E5");
            var cloudSync = CreateService(cloudSource, storage);
            await cloudSync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);
            oldCloudVaultId = (await cloudSource.Repository.GetConfigurationAsync())!.VaultId;
        }

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        await local.Vault.CreateGroupAsync("Current phone group", "⌂", "#10B981");
        var sync = CreateService(local, storage);
        await sync.ReplaceCloudVaultAsync(new CloudVaultReplacementRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            CloudVaultId = oldCloudVaultId,
            LocalVaultPassphrase = NewPassphrase,
            TypedConfirmation = "REPLACE CLOUD BACKUP",
        });

        var configuration = await local.Repository.GetConfigurationAsync();
        Assert.NotNull(configuration);
        Assert.True(configuration.IsEnabled);
        var remaining = Assert.Single(await sync.FindVaultsAsync(CloudProviderKind.GoogleDrive));
        Assert.Equal(configuration.VaultId, remaining.VaultId);
        Assert.NotEqual(oldCloudVaultId, remaining.VaultId);
    }

    [Fact]
    public async Task MergeDifferentVault_ReencryptsCloudSecretsAndKeepsLocalData()
    {
        var storage = new InMemoryCloudStorageProvider();
        string cloudVaultId;
        await using (var cloudSource = await TestContext.CreateAsync())
        {
            await cloudSource.Keys.InitializeVaultAsync(OldPassphrase);
            var cloudGroup = await cloudSource.Vault.CreateGroupAsync("Cloud group", "☁", "#4F46E5");
            await cloudSource.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument
            {
                Name = "Cloud secret",
                Notes = "Imported safely",
            });
            var cloudSync = CreateService(cloudSource, storage);
            await cloudSync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);
            cloudVaultId = (await cloudSource.Repository.GetConfigurationAsync())!.VaultId;
        }

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        var localGroup = await local.Vault.CreateGroupAsync("Local group", "⌂", "#10B981");
        await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument
        {
            Name = "Local secret",
            Notes = "Kept locally",
        });
        var sync = CreateService(local, storage);
        var request = new CloudVaultMergeRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            CloudVaultId = cloudVaultId,
            CloudVaultPassphrase = OldPassphrase,
            LocalVaultPassphrase = NewPassphrase,
        };

        var preview = await sync.PreviewMergeAsync(request);
        Assert.Equal(1, preview.CloudOnlyGroups);
        Assert.Equal(1, preview.CloudOnlySecrets);
        Assert.Equal(1, preview.LocalOnlyGroups);
        Assert.Equal(1, preview.LocalOnlySecrets);

        await sync.MergeDifferentVaultAsync(request);

        Assert.Equal(2, (await local.Vault.GetGroupsAsync()).Count);
        var secrets = await local.Vault.GetSecretsAsync();
        Assert.Equal(2, secrets.Count);
        var imported = Assert.Single(secrets, secret => secret.Name == "Cloud secret");
        Assert.Equal("Imported safely", (await local.Vault.ReadSecretAsync(imported.Id)).Notes);
        var configuration = await local.Repository.GetConfigurationAsync();
        Assert.Equal(configuration!.VaultId,
            Assert.Single(await sync.FindVaultsAsync(CloudProviderKind.GoogleDrive)).VaultId);
    }

    [Fact]
    public async Task Restore_AfterPassphraseChangeRejectsOlderRecoveryEnvelope()
    {
        var storage = new InMemoryCloudStorageProvider();
        await using (var source = await TestContext.CreateAsync())
        {
            await source.Keys.InitializeVaultAsync(OldPassphrase);
            await source.Vault.CreateGroupAsync("Passwords", "🔑", "#4F46E5");
            var sync = CreateService(source, storage);
            await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

            // Simulate another device that restored before the passphrase changed
            // and therefore still uses the former passphrase for local unlock.
            await using var olderDevice = await TestContext.CreateAsync();
            var olderDeviceSync = CreateService(olderDevice, storage);
            var initialBackup = Assert.Single(
                await olderDeviceSync.FindVaultsAsync(CloudProviderKind.GoogleDrive));
            await olderDeviceSync.RestoreAsync(new CloudRestoreRequest
            {
                Provider = CloudProviderKind.GoogleDrive,
                VaultId = initialBackup.VaultId,
                VaultPassphrase = OldPassphrase,
            });

            await sync.ChangePassphraseAsync(OldPassphrase, NewPassphrase);
            await olderDeviceSync.SynchronizeAsync();
        }

        await using var destination = await TestContext.CreateAsync();
        var restore = CreateService(destination, storage);
        var backup = Assert.Single(await restore.FindVaultsAsync(CloudProviderKind.GoogleDrive));

        await Assert.ThrowsAsync<CloudRestoreFailedException>(() => restore.RestoreAsync(new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = backup.VaultId,
            VaultPassphrase = OldPassphrase,
        }));
        Assert.Null(await destination.Repository.GetVaultMetadataAsync());

        await restore.RestoreAsync(new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = backup.VaultId,
            VaultPassphrase = NewPassphrase,
        });

        Assert.NotNull(await destination.Repository.GetVaultMetadataAsync());
        Assert.Equal("Passwords", Assert.Single(await destination.Vault.GetGroupsAsync()).Name);
    }

    private static CloudSyncService CreateService(
        TestContext context,
        InMemoryCloudStorageProvider storage,
        ICloudAuthorizationService? authorization = null) => new(
            context.Repository,
            context.Repository,
            context.Session,
            context.Keys,
            context.Encryption,
            new CloudSnapshotService(
                context.Kdf,
                new TestParameterProvider(),
                context.Encryption),
            new SyncMergeService(),
            new InMemoryProviderFactory(storage),
            authorization ?? new TestCloudAuthorizationService(),
            new TestCloudNetworkPolicy());

    private sealed class InMemoryCloudStorageProvider : ICloudStorageProvider
    {
        private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
        private int _nextId;

        public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;

        public Task<IReadOnlyList<CloudFileInfo>> ListAsync(
            int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudFileInfo>>(
            _files.Values.Select(item => item.Info).Take(maximumFiles).ToArray());

        public Task<Stream> DownloadAsync(
            string providerFileId,
            long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
            CancellationToken cancellationToken = default) => Task.FromResult<Stream>(
            new MemoryStream(_files[providerFileId].Content.ToArray(), writable: false));

        public Task<CloudFileInfo> UploadImmutableAsync(
            string opaqueName,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken = default)
        {
            var id = $"file-{++_nextId}";
            var info = new CloudFileInfo(
                id,
                opaqueName,
                content.Length,
                DateTimeOffset.UtcNow.AddSeconds(_nextId));
            _files.Add(id, new StoredFile(info, content.ToArray()));
            return Task.FromResult(info);
        }

        public Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default)
        {
            _files.Remove(providerFileId);
            return Task.CompletedTask;
        }

        private sealed record StoredFile(CloudFileInfo Info, byte[] Content);
    }

    private sealed class InMemoryProviderFactory(ICloudStorageProvider storage) : ICloudStorageProviderFactory
    {
        public ICloudStorageProvider Get(CloudProviderKind provider) => storage;
    }

    private sealed class TestCloudAuthorizationService : ICloudAuthorizationService
    {
        public Task AuthorizeAsync(CloudProviderKind provider, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(
            CloudProviderKind provider,
            CancellationToken cancellationToken = default) => Task.FromResult("test-token");

        public Task DisconnectAsync(CloudProviderKind provider, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class LockOnFirstAuthorizationService(IVaultSession session) : ICloudAuthorizationService
    {
        public int AuthorizationCount { get; private set; }

        public Task AuthorizeAsync(
            CloudProviderKind provider,
            CancellationToken cancellationToken = default)
        {
            AuthorizationCount++;
            if (AuthorizationCount == 1)
            {
                session.Lock();
            }

            return Task.CompletedTask;
        }

        public Task<string> GetAccessTokenAsync(
            CloudProviderKind provider,
            CancellationToken cancellationToken = default) => Task.FromResult("test-token");

        public Task DisconnectAsync(
            CloudProviderKind provider,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestCloudNetworkPolicy : ICloudNetworkPolicy
    {
        public bool IsInternetAvailable => true;
        public bool IsUsingWifi => true;
    }
}
