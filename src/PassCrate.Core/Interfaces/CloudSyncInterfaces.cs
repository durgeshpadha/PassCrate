using PassCrate.Core.Models;

namespace PassCrate.Core.Interfaces;

public interface ICloudAuthorizationService
{
    Task AuthorizeAsync(CloudProviderKind provider, CancellationToken cancellationToken = default);
    Task<string> GetAccessTokenAsync(CloudProviderKind provider, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CloudProviderKind provider, CancellationToken cancellationToken = default);
}

public interface ICloudStorageProvider
{
    CloudProviderKind Kind { get; }
    Task<IReadOnlyList<CloudFileInfo>> ListAsync(
        int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
        CancellationToken cancellationToken = default);
    Task<Stream> DownloadAsync(
        string providerFileId,
        long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
        CancellationToken cancellationToken = default);
    Task<CloudFileInfo> UploadImmutableAsync(string opaqueName, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default);
}

public interface ICloudStorageProviderFactory
{
    ICloudStorageProvider Get(CloudProviderKind provider);
}

public interface ICloudNetworkPolicy
{
    bool IsInternetAvailable { get; }
    bool IsUsingWifi { get; }
}

public interface ICloudSnapshotService
{
    Task<RecoveryKeyEnvelope> CreateRecoveryEnvelopeAsync(
        string vaultId,
        string vaultPassphrase,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken = default);

    byte[] SerializeEncryptedSnapshot(
        CloudVaultSnapshotHeaderV2 header,
        CloudVaultStateV2 state,
        ReadOnlyMemory<byte> dataEncryptionKey);
    CloudVaultSnapshotV2 DeserializeSnapshotV2(ReadOnlySpan<byte> content);
    CloudVaultStateV2 DecryptState(CloudVaultSnapshotV2 snapshot, ReadOnlyMemory<byte> dataEncryptionKey);
    Task<byte[]> RecoverDataEncryptionKeyAsync(
        CloudVaultSnapshotV2 snapshot,
        string vaultPassphrase,
        CancellationToken cancellationToken = default);
}

public interface ISyncMergeService
{
    CloudMergeResultV2 Merge(CloudVaultStateV2 left, CloudVaultStateV2 right);
}

public interface ISyncRepository
{
    Task<CloudSyncConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(CloudSyncConfiguration configuration, CancellationToken cancellationToken = default);
    Task SetWifiOnlyAsync(bool wifiOnly, CancellationToken cancellationToken = default);
    Task DisableAsync(CancellationToken cancellationToken = default);
    Task InitializeRecordVectorsAsync(string deviceId, CancellationToken cancellationToken = default);
    Task BeginRestoreAsync(CancellationToken cancellationToken = default);
    Task CompleteRestoreAsync(CancellationToken cancellationToken = default);
    Task<CloudVaultStateV2> ExportStateV2Async(
        string deviceId,
        CancellationToken cancellationToken = default);
    Task ApplyMergedStateV2Async(
        CloudVaultStateV2 state,
        CloudSyncConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudSnapshotCatalogEntry>> GetSnapshotCatalogAsync(
        CancellationToken cancellationToken = default);
    Task SaveSnapshotCatalogEntryAsync(
        CloudSnapshotCatalogEntry entry,
        CancellationToken cancellationToken = default);
    Task DeleteSnapshotCatalogEntriesAsync(
        IReadOnlyList<string> snapshotIds,
        CancellationToken cancellationToken = default);
}

public interface IConflictRepository
{
    Task<IReadOnlyList<SyncConflictSet>> ListConflictsAsync(CancellationToken cancellationToken = default);
    Task<SyncConflictDetails> GetConflictDetailsAsync(
        SyncEntityKind entityKind,
        string recordId,
        CancellationToken cancellationToken = default);
    Task ResolveAsync(
        ConflictResolutionRequest request,
        string resolvingDeviceId,
        CancellationToken cancellationToken = default);
}

public interface ICloudSyncService
{
    event EventHandler<CloudSyncStatus>? StatusChanged;
    CloudSyncStatus Status { get; }
    Task<IReadOnlyList<CloudVaultDescriptor>> FindVaultsAsync(CloudProviderKind provider, CancellationToken cancellationToken = default);
    Task EnableAsync(CloudProviderKind provider, string vaultPassphrase, CancellationToken cancellationToken = default);
    Task ChangePassphraseAsync(string currentPassphrase, string newPassphrase, CancellationToken cancellationToken = default);
    Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(CloudRestoreRequest request, CancellationToken cancellationToken = default);
    Task SetWifiOnlyAsync(bool wifiOnly, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);
    Task DeleteCloudVaultAsync(string vaultPassphrase, string typedConfirmation, CancellationToken cancellationToken = default);
    Task DeleteAllProviderDataAsync(string vaultPassphrase, string typedConfirmation, CancellationToken cancellationToken = default);
    void CancelActiveSync();
    void ResetLocalState();
}

public interface ICloudSyncScheduler
{
    void NotifyVaultChanged();
    void NotifyVaultUnlocked();
    void NotifyVaultLocked();
    void NotifyForegrounded();
    void NotifyBackgrounded();
}
