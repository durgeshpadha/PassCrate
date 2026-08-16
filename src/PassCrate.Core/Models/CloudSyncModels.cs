using System.Collections.ObjectModel;

namespace PassCrate.Core.Models;

public enum CloudProviderKind
{
    GoogleDrive,
    Dropbox,
}

public enum CloudSyncState
{
    Disabled,
    Connecting,
    Ready,
    Syncing,
    Offline,
    ReconnectRequired,
    QuotaLimited,
    RateLimited,
    CloudCleanupRequired,
    Error,
}

public enum SyncEntityKind
{
    Group,
    Secret,
}

public enum VectorComparison
{
    Equal,
    Before,
    After,
    Concurrent,
}

public sealed record VersionVector
{
    public IReadOnlyDictionary<string, long> Entries { get; init; } =
        new ReadOnlyDictionary<string, long>(new Dictionary<string, long>(StringComparer.Ordinal));

    public long Get(string deviceId) => Entries.TryGetValue(deviceId, out var value) ? value : 0;

    public VersionVector Increment(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var entries = new Dictionary<string, long>(Entries, StringComparer.Ordinal)
        {
            [deviceId] = checked(Get(deviceId) + 1),
        };
        return new VersionVector { Entries = new ReadOnlyDictionary<string, long>(entries) };
    }

    public VersionVector Merge(VersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var entries = new Dictionary<string, long>(Entries, StringComparer.Ordinal);
        foreach (var (deviceId, value) in other.Entries)
        {
            entries[deviceId] = Math.Max(Get(deviceId), value);
        }

        return new VersionVector { Entries = new ReadOnlyDictionary<string, long>(entries) };
    }

    public VectorComparison Compare(VersionVector other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var before = false;
        var after = false;
        foreach (var deviceId in Entries.Keys.Concat(other.Entries.Keys).Distinct(StringComparer.Ordinal))
        {
            var left = Get(deviceId);
            var right = other.Get(deviceId);
            before |= left < right;
            after |= left > right;
        }

        return (before, after) switch
        {
            (false, false) => VectorComparison.Equal,
            (true, false) => VectorComparison.Before,
            (false, true) => VectorComparison.After,
            _ => VectorComparison.Concurrent,
        };
    }
}

public sealed record RecoveryKeyEnvelope
{
    public int Version { get; init; } = 2;
    public required KeyDerivationParameters RecoveryKdf { get; init; }
    public required EncryptedPayload WrappedDataEncryptionKey { get; init; }
}

public sealed record CloudSyncConfiguration
{
    public int Version { get; init; } = 2;
    public bool IsEnabled { get; init; }
    public required CloudProviderKind Provider { get; init; }
    public required string VaultId { get; init; }
    public required string DeviceId { get; init; }
    public required RecoveryKeyEnvelope Recovery { get; init; }
    public bool WifiOnly { get; init; }
    public IReadOnlyList<string> HeadSnapshotIds { get; init; } = [];
    public long Generation { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
}

public sealed record CloudSyncStatus
{
    public CloudSyncState State { get; init; } = CloudSyncState.Disabled;
    public CloudProviderKind? Provider { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
    public int ConflictCount { get; init; }
    public string Message { get; init; } = "Cloud sync is off.";
}

public static class CloudResourceLimits
{
    public const long MaximumSnapshotBytes = 100L * 1024 * 1024;
    public const int MaximumFilesPerOperation = 500;
    public const long MaximumDownloadedBytesPerOperation = 512L * 1024 * 1024;
    public const int MaximumRecordsPerSnapshot = 100_000;
    public const int MaximumFieldsPerSecret = 256;
    public const int MaximumSecretPlaintextBytes = 1024 * 1024;
    public const int MaximumJsonDepth = 64;
}

public sealed record SyncRecordRevision<T>
{
    public required string RevisionId { get; init; }
    public required VersionVector Vector { get; init; }
    public bool IsDeleted { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public bool IsConflict { get; init; }
    public string ContentFingerprint { get; init; } = string.Empty;
    public T? Payload { get; init; }
}

public sealed record SyncRecordEnvelope<T>
{
    public required SyncEntityKind EntityKind { get; init; }
    public required string RecordId { get; init; }
    public IReadOnlyList<SyncRecordRevision<T>> Revisions { get; init; } = [];
}

public sealed record CloudVaultStateV2
{
    public IReadOnlyList<SyncRecordEnvelope<VaultGroup>> Groups { get; init; } = [];
    public IReadOnlyList<SyncRecordEnvelope<VaultSecret>> Secrets { get; init; } = [];
}

public sealed record CloudVaultSnapshotHeaderV2
{
    public const string ExpectedFormat = "PassCrate.CloudVaultSnapshot";
    public string Format { get; init; } = ExpectedFormat;
    public int Version { get; init; } = 2;
    public required string VaultId { get; init; }
    public required string SnapshotId { get; init; }
    public IReadOnlyList<string> ParentSnapshotIds { get; init; } = [];
    public long Generation { get; init; }
    public required RecoveryKeyEnvelope Recovery { get; init; }
}

public sealed record CloudVaultSnapshotV2
{
    public required CloudVaultSnapshotHeaderV2 Header { get; init; }
    public required EncryptedPayload EncryptedVaultState { get; init; }
}

public enum ConflictResolutionKind
{
    KeepRevision,
    ManualMerge,
    KeepBoth,
    AcceptDeletion,
    RestoreGroup,
    MoveSecretsAndDeleteGroup,
}

public sealed record SyncConflictSet
{
    public required SyncEntityKind EntityKind { get; init; }
    public required string RecordId { get; init; }
    public IReadOnlyList<string> RevisionIds { get; init; } = [];
    public bool IncludesDeletion { get; init; }
}

public sealed record ConflictResolutionRequest
{
    public required SyncEntityKind EntityKind { get; init; }
    public required string RecordId { get; init; }
    public required ConflictResolutionKind Resolution { get; init; }
    public string? SelectedRevisionId { get; init; }
    public VaultGroup? MergedGroup { get; init; }
    public VaultSecret? MergedSecret { get; init; }
    public SecretDocument? MergedSecretDocument { get; init; }
    public string? DestinationGroupId { get; init; }
}

public sealed record SyncConflictRevisionView
{
    public required string RevisionId { get; init; }
    public bool IsDeleted { get; init; }
    public required string DisplayName { get; init; }
    public VaultGroup? Group { get; init; }
    public SecretDocument? Secret { get; init; }
}

public sealed record SyncConflictDetails
{
    public required SyncConflictSet Conflict { get; init; }
    public IReadOnlyList<SyncConflictRevisionView> Revisions { get; init; } = [];
}

public sealed record CloudSnapshotCatalogEntry
{
    public required string SnapshotId { get; init; }
    public required string ProviderFileId { get; init; }
    public required string OpaqueName { get; init; }
    public required string NamespaceId { get; init; }
    public IReadOnlyList<string> ParentSnapshotIds { get; init; } = [];
    public long Generation { get; init; }
    public bool UploadComplete { get; init; }
}

public sealed record CloudMergeResultV2(
    CloudVaultStateV2 State,
    IReadOnlyList<SyncConflictSet> Conflicts);

public sealed record CloudFileInfo(
    string ProviderFileId,
    string OpaqueName,
    long Size,
    DateTimeOffset? ModifiedAt);

public sealed record CloudVaultDescriptor(
    string VaultId,
    long LatestGeneration,
    int SnapshotCount,
    CloudProviderKind Provider);

public sealed record CloudSyncResult(
    string SnapshotId,
    int ConflictCount,
    DateTimeOffset CompletedAt);

public sealed record CloudRestoreRequest
{
    public required CloudProviderKind Provider { get; init; }
    public required string VaultId { get; init; }
    public required string VaultPassphrase { get; init; }
}
