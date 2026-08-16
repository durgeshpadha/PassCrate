using SQLite;

namespace PassCrate.Infrastructure.Database;

[Table("VaultMetadata")]
internal sealed class VaultMetadataEntity
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public int Version { get; set; }
    public string PassphraseKdfJson { get; set; } = string.Empty;
    public string DeviceProtectedWrappedDekJson { get; set; } = string.Empty;
    public string PlatformKeyAlias { get; set; } = string.Empty;
    public int FailedUnlockAttempts { get; set; }
    public long? LastFailedUnlockAtUnixMs { get; set; }
    public long? NextUnlockAllowedAtUnixMs { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

[Table("SchemaVersion")]
internal sealed class SchemaVersionEntity
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public int Version { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

[Table("Groups")]
internal sealed class GroupEntity
{
    [PrimaryKey] public string Id { get; set; } = string.Empty;
    [Indexed] public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "◇";
    public string Color { get; set; } = "#64748B";
    public int SortOrder { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

[Table("Secrets")]
internal sealed class SecretEntity
{
    [PrimaryKey] public string Id { get; set; } = string.Empty;
    [Indexed] public string GroupId { get; set; } = string.Empty;
    [Indexed] public string Name { get; set; } = string.Empty;
    public string SearchableFieldNames { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] AuthenticationTag { get; set; } = [];
    public int EncryptionVersion { get; set; }
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
}

[Table("ApplicationSettings")]
internal sealed class SettingsEntity
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public string Json { get; set; } = string.Empty;
}

[Table("CloudSyncConfiguration")]
internal sealed class CloudSyncConfigurationEntity
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public int Version { get; set; }
    public bool IsEnabled { get; set; }
    public int Provider { get; set; }
    [Indexed] public string VaultId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string RecoveryJson { get; set; } = string.Empty;
    public bool WifiOnly { get; set; }
    public string HeadSnapshotIdsJson { get; set; } = "[]";
    public long Generation { get; set; }
    public long? LastSuccessfulSyncAtUnixMs { get; set; }
}

[Table("SyncRecordRevisions")]
internal sealed class SyncRecordRevisionEntity
{
    [PrimaryKey] public string RevisionId { get; set; } = string.Empty;
    [Indexed(Name = "IX_SyncRevision_Record", Order = 1)] public int EntityKind { get; set; }
    [Indexed(Name = "IX_SyncRevision_Record", Order = 2)] public string RecordId { get; set; } = string.Empty;
    public string VersionVectorJson { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public long? DeletedAtUnixMs { get; set; }
    public bool IsConflict { get; set; }
    public string ContentFingerprint { get; set; } = string.Empty;
    public string? PayloadJson { get; set; }
}

[Table("CloudSnapshotCatalog")]
internal sealed class CloudSnapshotCatalogEntity
{
    [PrimaryKey] public string SnapshotId { get; set; } = string.Empty;
    [Unique] public string ProviderFileId { get; set; } = string.Empty;
    [Indexed] public string OpaqueName { get; set; } = string.Empty;
    [Indexed] public string NamespaceId { get; set; } = string.Empty;
    public string ParentSnapshotIdsJson { get; set; } = "[]";
    public long Generation { get; set; }
    public bool UploadComplete { get; set; }
}

[Table("CloudRestoreJournal")]
internal sealed class CloudRestoreJournalEntity
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public long StartedAtUnixMs { get; set; }
}
