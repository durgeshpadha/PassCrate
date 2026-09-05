using System.Security.Cryptography;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;
using PassCrate.Infrastructure.Sync;
using SQLite;

namespace PassCrate.Infrastructure.Database;

public sealed class SqliteVaultRepository : IVaultRepository, ISyncRepository, IConflictRepository, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private SQLiteAsyncConnection _database;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;
    private readonly IEncryptionService? _encryption;
    private readonly IVaultSession? _session;

    public SqliteVaultRepository(
        string databasePath,
        IEncryptionService? encryption = null,
        IVaultSession? session = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _encryption = encryption;
        _session = session;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _database = CreateConnection(DatabasePath);
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _database.CreateTablesAsync(
                CreateFlags.None,
                typeof(VaultMetadataEntity),
                typeof(SchemaVersionEntity),
                typeof(GroupEntity),
                typeof(SecretEntity),
                typeof(SettingsEntity),
                typeof(CloudSyncConfigurationEntity),
                typeof(SyncRecordRevisionEntity),
                typeof(CloudSnapshotCatalogEntity),
                typeof(CloudRestoreJournalEntity)).ConfigureAwait(false);
            var existingSchema = await _database.FindAsync<SchemaVersionEntity>(1).ConfigureAwait(false);
            if (existingSchema is { Version: < 3 })
            {
                // PassCrate has not shipped. The v2 account credential model is
                // intentionally incompatible with the v3 single-passphrase vault.
                await DeleteLegacyDevelopmentVaultAsync().ConfigureAwait(false);
            }
            await _database.InsertOrReplaceAsync(new SchemaVersionEntity
            {
                Id = 1,
                Version = 3,
                UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ConfigureAwait(false);
            await _database.ExecuteAsync("PRAGMA foreign_keys = ON").ConfigureAwait(false);
            if (await _database.FindAsync<CloudRestoreJournalEntity>(1).ConfigureAwait(false) is not null)
            {
                await RollBackInterruptedRestoreAsync().ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<VaultMetadataV3?> GetVaultMetadataAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entity = await _database.FindAsync<VaultMetadataEntity>(1).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task SaveVaultMetadataAsync(VaultMetadataV3 metadata, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.InsertOrReplaceAsync(new VaultMetadataEntity
        {
            Id = 1,
            Version = metadata.Version,
            PassphraseKdfJson = JsonSerializer.Serialize(metadata.PassphraseKdf, JsonOptions),
            DeviceProtectedWrappedDekJson =
                JsonSerializer.Serialize(metadata.DeviceProtectedWrappedDataEncryptionKey, JsonOptions),
            PlatformKeyAlias = metadata.PlatformKeyAlias,
            FailedUnlockAttempts = metadata.FailedUnlockAttempts,
            LastFailedUnlockAtUnixMs = metadata.LastFailedUnlockAt?.ToUnixTimeMilliseconds(),
            NextUnlockAllowedAtUnixMs = metadata.NextUnlockAllowedAt?.ToUnixTimeMilliseconds(),
            CreatedAtUnixMs = metadata.CreatedAt.ToUnixTimeMilliseconds(),
            UpdatedAtUnixMs = metadata.UpdatedAt.ToUnixTimeMilliseconds(),
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<VaultGroup>> GetGroupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entities = await _database.Table<GroupEntity>()
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Name)
            .ToListAsync().ConfigureAwait(false);
        return entities.Select(ToModel).ToArray();
    }

    public async Task<VaultGroup?> GetGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entity = await _database.FindAsync<GroupEntity>(id).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task SaveGroupAsync(VaultGroup group, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.InsertOrReplace(ToEntity(group));
            MarkChangedV2(connection, SyncEntityKind.Group, group.Id, group, isDeleted: false);
        }).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            var existing = connection.Find<GroupEntity>(id);
            connection.Delete<GroupEntity>(id);
            MarkChangedV2(
                connection,
                SyncEntityKind.Group,
                id,
                existing is null ? null : ToModel(existing),
                isDeleted: true);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SecretSummary>> GetSecretSummariesAsync(
        string? groupId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var query = _database.Table<SecretEntity>();
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            query = query.Where(secret => secret.GroupId == groupId);
        }

        var entities = await query.OrderByDescending(secret => secret.UpdatedAtUnixMs).ToListAsync().ConfigureAwait(false);
        return entities.Select(entity => new SecretSummary(
            entity.Id,
            entity.GroupId,
            entity.Name,
            DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs))).ToArray();
    }

    public async Task<IReadOnlyList<SecretSummary>> SearchSecretsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var summaries = await GetSecretSummariesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(query))
        {
            return summaries;
        }

        var normalized = query.Trim();
        var entities = await _database.Table<SecretEntity>().ToListAsync().ConfigureAwait(false);
        var matches = entities
            .Where(entity =>
                entity.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                entity.SearchableFieldNames.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entity => entity.UpdatedAtUnixMs)
            .Select(entity => new SecretSummary(
                entity.Id,
                entity.GroupId,
                entity.Name,
                DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs)))
            .ToArray();
        return matches;
    }

    public async Task<VaultSearchResults> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var normalized = query?.Trim() ?? string.Empty;
        var groups = await GetGroupsAsync(cancellationToken).ConfigureAwait(false);
        var matchingGroups = string.IsNullOrWhiteSpace(normalized)
            ? groups
            : groups.Where(group => group.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new VaultSearchResults
        {
            Groups = matchingGroups,
            Secrets = await SearchSecretsAsync(normalized, cancellationToken).ConfigureAwait(false),
        };
    }

    public async Task<VaultSecret?> GetSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entity = await _database.FindAsync<SecretEntity>(id).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task<IReadOnlyList<VaultSecret>> GetAllSecretsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return (await _database.Table<SecretEntity>().ToListAsync().ConfigureAwait(false))
            .Select(ToModel)
            .ToArray();
    }

    public async Task SaveSecretAsync(VaultSecret secret, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.InsertOrReplace(ToEntity(secret));
            MarkChangedV2(connection, SyncEntityKind.Secret, secret.Id, secret, isDeleted: false);
        }).ConfigureAwait(false);
    }

    public async Task DeleteSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.Delete<SecretEntity>(id);
            MarkChangedV2<VaultSecret>(
                connection,
                SyncEntityKind.Secret,
                id,
                payload: null,
                isDeleted: true);
        }).ConfigureAwait(false);
    }

    public async Task MoveSecretsAsync(
        string fromGroupId,
        string toGroupId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        // This low-level operation is intentionally not used by VaultService because
        // the group id is authenticated as AAD and records must be re-encrypted when moved.
        throw new NotSupportedException("Move secrets through IVaultService so authenticated metadata is re-encrypted.");
    }

    public async Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entity = await _database.FindAsync<SettingsEntity>(1).ConfigureAwait(false);
        return entity is null
            ? new ApplicationSettings()
            : JsonSerializer.Deserialize<ApplicationSettings>(entity.Json, JsonOptions) ?? new ApplicationSettings();
    }

    public async Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.InsertOrReplaceAsync(new SettingsEntity
        {
            Id = 1,
            Json = JsonSerializer.Serialize(settings, JsonOptions),
        }).ConfigureAwait(false);
    }

    public async Task<CloudSyncConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entity = await _database.FindAsync<CloudSyncConfigurationEntity>(1).ConfigureAwait(false);
        return entity is null ? null : ToModel(entity);
    }

    public async Task SaveConfigurationAsync(
        CloudSyncConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.InsertOrReplaceAsync(ToEntity(configuration)).ConfigureAwait(false);
    }

    public async Task SetWifiOnlyAsync(bool wifiOnly, CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cloud sync is not configured.");
        await SaveConfigurationAsync(configuration with { WifiOnly = wifiOnly }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is not null)
        {
            await SaveConfigurationAsync(configuration with { IsEnabled = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InitializeRecordVectorsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            foreach (var group in connection.Table<GroupEntity>())
            {
                EnsureInitialRevision(connection, SyncEntityKind.Group, group.Id, ToModel(group), deviceId);
            }

            foreach (var secret in connection.Table<SecretEntity>())
            {
                EnsureInitialRevision(connection, SyncEntityKind.Secret, secret.Id, ToModel(secret), deviceId);
            }
        }).ConfigureAwait(false);
    }

    public async Task BeginRestoreAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.InsertOrReplaceAsync(new CloudRestoreJournalEntity
        {
            Id = 1,
            StartedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }).ConfigureAwait(false);
    }

    public async Task CompleteRestoreAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.DeleteAsync<CloudRestoreJournalEntity>(1).ConfigureAwait(false);
    }

    public async Task ClearVaultForReplacementAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.DeleteAll<VaultMetadataEntity>();
            connection.DeleteAll<GroupEntity>();
            connection.DeleteAll<SecretEntity>();
            connection.DeleteAll<CloudSyncConfigurationEntity>();
            connection.DeleteAll<SyncRecordRevisionEntity>();
            connection.DeleteAll<CloudSnapshotCatalogEntity>();
        }).ConfigureAwait(false);
    }

    public async Task<CloudVaultStateV2> ExportStateV2Async(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await InitializeRecordVectorsAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var entities = await _database.Table<SyncRecordRevisionEntity>().ToListAsync().ConfigureAwait(false);
        return new CloudVaultStateV2
        {
            Groups = entities
                .Where(entity => entity.EntityKind == (int)SyncEntityKind.Group)
                .GroupBy(entity => entity.RecordId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new SyncRecordEnvelope<VaultGroup>
                {
                    EntityKind = SyncEntityKind.Group,
                    RecordId = group.Key,
                    Revisions = group.Select(ToGroupRevision)
                        .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                        .ToArray(),
                }).ToArray(),
            Secrets = entities
                .Where(entity => entity.EntityKind == (int)SyncEntityKind.Secret)
                .GroupBy(entity => entity.RecordId, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new SyncRecordEnvelope<VaultSecret>
                {
                    EntityKind = SyncEntityKind.Secret,
                    RecordId = group.Key,
                    Revisions = group.Select(ToSecretRevision)
                        .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                        .ToArray(),
                }).ToArray(),
        };
    }

    public async Task ApplyMergedStateV2Async(
        CloudVaultStateV2 state,
        CloudSyncConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var recordCount = checked(state.Groups.Count + state.Secrets.Count);
        if (recordCount > CloudResourceLimits.MaximumRecordsPerSnapshot)
        {
            throw new CloudResourceLimitException("The cloud snapshot contains too many records.");
        }

        var activeSecrets = state.Secrets
            .Select(envelope => SelectPrimary(envelope.Revisions))
            .Where(revision => revision is { IsDeleted: false, Payload: not null })
            .Select(revision => revision!.Payload!)
            .ToArray();
        var referencedGroupIds = activeSecrets.Select(secret => secret.GroupId).ToHashSet(StringComparer.Ordinal);
        var materializedGroups = new Dictionary<string, VaultGroup>(StringComparer.Ordinal);
        foreach (var envelope in state.Groups)
        {
            ValidateEnvelope(envelope, SyncEntityKind.Group);
            var primary = SelectPrimary(envelope.Revisions);
            if (primary is { IsDeleted: false, Payload: not null })
            {
                materializedGroups[envelope.RecordId] = primary.Payload;
            }
            else if (referencedGroupIds.Contains(envelope.RecordId))
            {
                var presentation = envelope.Revisions
                    .Where(revision => revision.IsDeleted && revision.Payload is not null)
                    .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                    .Select(revision => revision.Payload)
                    .FirstOrDefault()
                    ?? throw new InvalidDataException("A referenced deleted group has no presentation metadata.");
                materializedGroups[envelope.RecordId] = presentation with
                {
                    Name = presentation.Name.EndsWith(" (deletion conflict)", StringComparison.Ordinal)
                        ? presentation.Name
                        : $"{presentation.Name} (deletion conflict)",
                };
            }
        }

        foreach (var envelope in state.Secrets)
        {
            ValidateEnvelope(envelope, SyncEntityKind.Secret);
        }

        if (activeSecrets.Any(secret => !materializedGroups.ContainsKey(secret.GroupId)))
        {
            throw new InvalidDataException("An active secret references a missing group.");
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            connection.DeleteAll<GroupEntity>();
            connection.DeleteAll<SecretEntity>();
            connection.DeleteAll<SyncRecordRevisionEntity>();
            foreach (var group in materializedGroups.Values)
            {
                connection.Insert(ToEntity(group));
            }

            foreach (var secret in activeSecrets)
            {
                connection.Insert(ToEntity(secret));
            }

            foreach (var envelope in state.Groups)
            {
                foreach (var revision in envelope.Revisions)
                {
                    connection.Insert(ToEntity(envelope.RecordId, SyncEntityKind.Group, revision));
                }
            }

            foreach (var envelope in state.Secrets)
            {
                foreach (var revision in envelope.Revisions)
                {
                    connection.Insert(ToEntity(envelope.RecordId, SyncEntityKind.Secret, revision));
                }
            }

            connection.InsertOrReplace(ToEntity(configuration));
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudSnapshotCatalogEntry>> GetSnapshotCatalogAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return (await _database.Table<CloudSnapshotCatalogEntity>().ToListAsync().ConfigureAwait(false))
            .Select(entity => new CloudSnapshotCatalogEntry
            {
                SnapshotId = entity.SnapshotId,
                ProviderFileId = entity.ProviderFileId,
                OpaqueName = entity.OpaqueName,
                NamespaceId = entity.NamespaceId,
                ParentSnapshotIds = Deserialize<string[]>(entity.ParentSnapshotIdsJson),
                Generation = entity.Generation,
                UploadComplete = entity.UploadComplete,
            })
            .ToArray();
    }

    public async Task SaveSnapshotCatalogEntryAsync(
        CloudSnapshotCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.InsertOrReplaceAsync(new CloudSnapshotCatalogEntity
        {
            SnapshotId = entry.SnapshotId,
            ProviderFileId = entry.ProviderFileId,
            OpaqueName = entry.OpaqueName,
            NamespaceId = entry.NamespaceId,
            ParentSnapshotIdsJson = JsonSerializer.Serialize(entry.ParentSnapshotIds, JsonOptions),
            Generation = entry.Generation,
            UploadComplete = entry.UploadComplete,
        }).ConfigureAwait(false);
    }

    public async Task DeleteSnapshotCatalogEntriesAsync(
        IReadOnlyList<string> snapshotIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshotIds);
        if (snapshotIds.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var uniqueIds = snapshotIds.Distinct(StringComparer.Ordinal).ToArray();
        await _database.RunInTransactionAsync(connection =>
        {
            foreach (var snapshotId in uniqueIds)
            {
                connection.Delete<CloudSnapshotCatalogEntity>(snapshotId);
            }
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncConflictSet>> ListConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entities = await _database.Table<SyncRecordRevisionEntity>().ToListAsync().ConfigureAwait(false);
        return entities
            .GroupBy(entity => (entity.EntityKind, entity.RecordId))
            .Where(group => group.Count() > 1 || group.Any(entity => entity.IsConflict))
            .Select(group => CreateConflictSet(
                (SyncEntityKind)group.Key.EntityKind,
                group.Key.RecordId,
                group.ToArray()))
            .OrderBy(conflict => conflict.EntityKind)
            .ThenBy(conflict => conflict.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<SyncConflictDetails> GetConflictDetailsAsync(
        SyncEntityKind entityKind,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var entities = await _database.Table<SyncRecordRevisionEntity>()
            .Where(entity => entity.EntityKind == (int)entityKind && entity.RecordId == recordId)
            .ToListAsync().ConfigureAwait(false);
        if (entities.Count == 0)
        {
            throw new KeyNotFoundException("Conflict record not found.");
        }

        var conflict = CreateConflictSet(entityKind, recordId, entities);
        if (entityKind == SyncEntityKind.Group)
        {
            return new SyncConflictDetails
            {
                Conflict = conflict,
                Revisions = entities.Select(ToGroupRevision)
                    .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                    .Select(revision => new SyncConflictRevisionView
                    {
                        RevisionId = revision.RevisionId,
                        IsDeleted = revision.IsDeleted,
                        DisplayName = revision.IsDeleted
                            ? $"{revision.Payload?.Name ?? "Group"} (deleted)"
                            : revision.Payload?.Name ?? "Group",
                        ChangedAt = revision.IsDeleted ? revision.DeletedAt : revision.Payload?.UpdatedAt,
                        Group = revision.Payload,
                    }).ToArray(),
            };
        }

        if (_encryption is null || _session is null)
        {
            throw new VaultLockedException();
        }

        var views = new List<SyncConflictRevisionView>();
        foreach (var revision in entities.Select(ToSecretRevision).OrderBy(item => item.RevisionId, StringComparer.Ordinal))
        {
            SecretDocument? document = null;
            if (!revision.IsDeleted && revision.Payload is not null)
            {
                byte[]? plaintext = null;
                try
                {
                    plaintext = _session.UseKey(key => _encryption.Decrypt(
                        revision.Payload.EncryptedPayload,
                        key.Span,
                        CreateSecretAssociatedData(
                            revision.Payload.Id,
                            revision.Payload.GroupId,
                            revision.Payload.EncryptionVersion)));
                    document = JsonSerializer.Deserialize<SecretDocument>(plaintext, JsonOptions)
                        ?? throw new VaultCorruptedException();
                }
                catch (CryptographicException)
                {
                    throw new VaultCorruptedException();
                }
                finally
                {
                    if (plaintext is not null)
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
            }

            views.Add(new SyncConflictRevisionView
            {
                RevisionId = revision.RevisionId,
                IsDeleted = revision.IsDeleted,
                DisplayName = revision.IsDeleted ? "Secret (deleted)" : document?.Name ?? "Secret",
                ChangedAt = revision.IsDeleted ? revision.DeletedAt : revision.Payload?.UpdatedAt,
                Secret = document,
            });
        }

        return new SyncConflictDetails { Conflict = conflict, Revisions = views };
    }

    public async Task ResolveAsync(
        ConflictResolutionRequest request,
        string resolvingDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvingDeviceId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        await _database.RunInTransactionAsync(connection =>
        {
            var entities = connection.Table<SyncRecordRevisionEntity>()
                .Where(entity =>
                    entity.EntityKind == (int)request.EntityKind &&
                    entity.RecordId == request.RecordId)
                .ToArray();
            if (entities.Length < 2 && entities.All(entity => !entity.IsConflict))
            {
                throw new InvalidOperationException("This record no longer has a conflict.");
            }

            if (request.EntityKind == SyncEntityKind.Group)
            {
                ResolveGroupConflict(connection, entities, request, resolvingDeviceId);
            }
            else
            {
                ResolveSecretConflict(connection, entities, request, resolvingDeviceId);
            }
        }).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _database.CloseAsync().ConfigureAwait(false);
        _initialized = false;

        foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        // Keep the repository usable after reset while ensuring the previous
        // database pages and sidecar files are physically removed first.
        _database = CreateConnection(DatabasePath);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.CloseAsync().ConfigureAwait(false);
        _initializationLock.Dispose();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task RollBackInterruptedRestoreAsync() => _database.RunInTransactionAsync(connection =>
    {
        connection.DeleteAll<VaultMetadataEntity>();
        connection.DeleteAll<GroupEntity>();
        connection.DeleteAll<SecretEntity>();
        connection.DeleteAll<CloudSyncConfigurationEntity>();
        connection.DeleteAll<SyncRecordRevisionEntity>();
        connection.DeleteAll<CloudSnapshotCatalogEntity>();
        connection.DeleteAll<CloudRestoreJournalEntity>();
    });

    private async Task DeleteLegacyDevelopmentVaultAsync()
    {
        await _database.RunInTransactionAsync(connection =>
        {
            connection.DeleteAll<VaultMetadataEntity>();
            connection.DeleteAll<GroupEntity>();
            connection.DeleteAll<SecretEntity>();
            connection.DeleteAll<CloudSyncConfigurationEntity>();
            connection.DeleteAll<SyncRecordRevisionEntity>();
            connection.DeleteAll<CloudSnapshotCatalogEntity>();
            connection.DeleteAll<CloudRestoreJournalEntity>();
        }).ConfigureAwait(false);
        await _database.ExecuteAsync("DROP TABLE IF EXISTS Users").ConfigureAwait(false);
    }

    private static VaultMetadataV3 ToModel(VaultMetadataEntity entity) => new()
    {
        Version = entity.Version,
        PassphraseKdf = Deserialize<KeyDerivationParameters>(entity.PassphraseKdfJson),
        DeviceProtectedWrappedDataEncryptionKey =
            Deserialize<DeviceProtectedPayload>(entity.DeviceProtectedWrappedDekJson),
        PlatformKeyAlias = entity.PlatformKeyAlias,
        FailedUnlockAttempts = entity.FailedUnlockAttempts,
        LastFailedUnlockAt = entity.LastFailedUnlockAtUnixMs is long lastFailed
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastFailed)
            : null,
        NextUnlockAllowedAt = entity.NextUnlockAllowedAtUnixMs is long nextAllowed
            ? DateTimeOffset.FromUnixTimeMilliseconds(nextAllowed)
            : null,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs),
    };

    private static VaultGroup ToModel(GroupEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Icon = entity.Icon,
        Color = entity.Color,
        SortOrder = entity.SortOrder,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs),
    };

    private static VaultSecret ToModel(SecretEntity entity) => new()
    {
        Id = entity.Id,
        GroupId = entity.GroupId,
        Name = entity.Name,
        SearchableFieldNames = entity.SearchableFieldNames,
        EncryptedPayload = new EncryptedPayload
        {
            Version = entity.EncryptionVersion,
            Algorithm = entity.Algorithm,
            Nonce = entity.Nonce,
            Ciphertext = entity.Ciphertext,
            AuthenticationTag = entity.AuthenticationTag,
        },
        EncryptionVersion = entity.EncryptionVersion,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.CreatedAtUnixMs),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(entity.UpdatedAtUnixMs),
    };

    private static CloudSyncConfiguration ToModel(CloudSyncConfigurationEntity entity) => new()
    {
        Version = entity.Version,
        IsEnabled = entity.IsEnabled,
        Provider = (CloudProviderKind)entity.Provider,
        VaultId = entity.VaultId,
        DeviceId = entity.DeviceId,
        Recovery = Deserialize<RecoveryKeyEnvelope>(entity.RecoveryJson),
        WifiOnly = entity.WifiOnly,
        HeadSnapshotIds = Deserialize<string[]>(entity.HeadSnapshotIdsJson),
        Generation = entity.Generation,
        LastSuccessfulSyncAt = entity.LastSuccessfulSyncAtUnixMs is long lastSync
            ? DateTimeOffset.FromUnixTimeMilliseconds(lastSync)
            : null,
    };

    private static GroupEntity ToEntity(VaultGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Icon = group.Icon,
        Color = group.Color,
        SortOrder = group.SortOrder,
        CreatedAtUnixMs = group.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUnixMs = group.UpdatedAt.ToUnixTimeMilliseconds(),
    };

    private static SecretEntity ToEntity(VaultSecret secret) => new()
    {
        Id = secret.Id,
        GroupId = secret.GroupId,
        Name = secret.Name,
        SearchableFieldNames = secret.SearchableFieldNames,
        Algorithm = secret.EncryptedPayload.Algorithm,
        Nonce = secret.EncryptedPayload.Nonce,
        Ciphertext = secret.EncryptedPayload.Ciphertext,
        AuthenticationTag = secret.EncryptedPayload.AuthenticationTag,
        EncryptionVersion = secret.EncryptionVersion,
        CreatedAtUnixMs = secret.CreatedAt.ToUnixTimeMilliseconds(),
        UpdatedAtUnixMs = secret.UpdatedAt.ToUnixTimeMilliseconds(),
    };

    private static CloudSyncConfigurationEntity ToEntity(CloudSyncConfiguration configuration) => new()
    {
        Id = 1,
        Version = configuration.Version,
        IsEnabled = configuration.IsEnabled,
        Provider = (int)configuration.Provider,
        VaultId = configuration.VaultId,
        DeviceId = configuration.DeviceId,
        RecoveryJson = JsonSerializer.Serialize(configuration.Recovery, JsonOptions),
        WifiOnly = configuration.WifiOnly,
        HeadSnapshotIdsJson = JsonSerializer.Serialize(configuration.HeadSnapshotIds, JsonOptions),
        Generation = configuration.Generation,
        LastSuccessfulSyncAtUnixMs = configuration.LastSuccessfulSyncAt?.ToUnixTimeMilliseconds(),
    };

    private static SyncRecordRevisionEntity ToEntity<T>(
        string recordId,
        SyncEntityKind kind,
        SyncRecordRevision<T> revision) => new()
    {
        RevisionId = revision.RevisionId,
        EntityKind = (int)kind,
        RecordId = recordId,
        VersionVectorJson = JsonSerializer.Serialize(revision.Vector, JsonOptions),
        IsDeleted = revision.IsDeleted,
        DeletedAtUnixMs = revision.DeletedAt?.ToUnixTimeMilliseconds(),
        IsConflict = revision.IsConflict,
        ContentFingerprint = revision.ContentFingerprint,
        PayloadJson = revision.Payload is null ? null : JsonSerializer.Serialize(revision.Payload, JsonOptions),
    };

    private static SyncRecordRevision<VaultGroup> ToGroupRevision(SyncRecordRevisionEntity entity) => new()
    {
        RevisionId = entity.RevisionId,
        Vector = Deserialize<VersionVector>(entity.VersionVectorJson),
        IsDeleted = entity.IsDeleted,
        DeletedAt = entity.DeletedAtUnixMs is long deletedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(deletedAt)
            : null,
        IsConflict = entity.IsConflict,
        ContentFingerprint = entity.ContentFingerprint,
        Payload = string.IsNullOrWhiteSpace(entity.PayloadJson)
            ? null
            : Deserialize<VaultGroup>(entity.PayloadJson),
    };

    private static SyncRecordRevision<VaultSecret> ToSecretRevision(SyncRecordRevisionEntity entity) => new()
    {
        RevisionId = entity.RevisionId,
        Vector = Deserialize<VersionVector>(entity.VersionVectorJson),
        IsDeleted = entity.IsDeleted,
        DeletedAt = entity.DeletedAtUnixMs is long deletedAt
            ? DateTimeOffset.FromUnixTimeMilliseconds(deletedAt)
            : null,
        IsConflict = entity.IsConflict,
        ContentFingerprint = entity.ContentFingerprint,
        Payload = string.IsNullOrWhiteSpace(entity.PayloadJson)
            ? null
            : Deserialize<VaultSecret>(entity.PayloadJson),
    };

    private static void MarkChangedV2<T>(
        SQLiteConnection connection,
        SyncEntityKind kind,
        string recordId,
        T? payload,
        bool isDeleted)
    {
        var configuration = connection.Find<CloudSyncConfigurationEntity>(1);
        if (configuration is null || !configuration.IsEnabled)
        {
            return;
        }

        var existing = connection.Table<SyncRecordRevisionEntity>()
            .Where(entity => entity.EntityKind == (int)kind && entity.RecordId == recordId)
            .ToArray();
        var primary = existing
            .OrderBy(entity => entity.IsDeleted)
            .ThenBy(entity => entity.RevisionId, StringComparer.Ordinal)
            .FirstOrDefault();
        var vector = (primary is null
            ? new VersionVector()
            : Deserialize<VersionVector>(primary.VersionVectorJson))
            .Increment(configuration.DeviceId);
        var revision = SyncRevisionFactory.Create(
            kind,
            recordId,
            vector,
            isDeleted,
            isDeleted ? DateTimeOffset.UtcNow : null,
            payload,
            isConflict: existing.Length > 1);
        foreach (var prior in existing)
        {
            var priorVector = Deserialize<VersionVector>(prior.VersionVectorJson);
            if (priorVector.Compare(vector) == VectorComparison.Before)
            {
                connection.Delete<SyncRecordRevisionEntity>(prior.RevisionId);
            }
        }

        connection.InsertOrReplace(ToEntity(recordId, kind, revision));
        UpdateConflictFlags(connection, kind, recordId);
    }

    private static void EnsureInitialRevision<T>(
        SQLiteConnection connection,
        SyncEntityKind kind,
        string recordId,
        T payload,
        string deviceId)
    {
        if (connection.Table<SyncRecordRevisionEntity>()
            .Any(entity => entity.EntityKind == (int)kind && entity.RecordId == recordId))
        {
            return;
        }

        var revision = SyncRevisionFactory.Create(
            kind,
            recordId,
            new VersionVector().Increment(deviceId),
            isDeleted: false,
            deletedAt: null,
            payload);
        connection.Insert(ToEntity(recordId, kind, revision));
    }

    private static void UpdateConflictFlags(
        SQLiteConnection connection,
        SyncEntityKind kind,
        string recordId)
    {
        var revisions = connection.Table<SyncRecordRevisionEntity>()
            .Where(entity => entity.EntityKind == (int)kind && entity.RecordId == recordId)
            .ToArray();
        var hasConflict = revisions.Length > 1;
        foreach (var revision in revisions)
        {
            if (revision.IsConflict != hasConflict)
            {
                revision.IsConflict = hasConflict;
                connection.Update(revision);
            }
        }
    }

    private static SyncRecordRevision<T>? SelectPrimary<T>(
        IReadOnlyList<SyncRecordRevision<T>> revisions) =>
        revisions.OrderBy(revision => revision.IsDeleted)
            .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static void ValidateEnvelope<T>(
        SyncRecordEnvelope<T> envelope,
        SyncEntityKind expectedKind)
    {
        if (envelope.EntityKind != expectedKind ||
            string.IsNullOrWhiteSpace(envelope.RecordId) ||
            envelope.Revisions.Count == 0 ||
            envelope.Revisions.Select(revision => revision.RevisionId)
                .Distinct(StringComparer.Ordinal).Count() != envelope.Revisions.Count ||
            envelope.Revisions.Any(revision =>
                string.IsNullOrWhiteSpace(revision.RevisionId) ||
                (!revision.IsDeleted && revision.Payload is null) ||
                revision.RevisionId != SyncRevisionFactory.Create(
                    expectedKind,
                    envelope.RecordId,
                    revision.Vector,
                    revision.IsDeleted,
                    revision.DeletedAt,
                    revision.Payload,
                    revision.IsConflict).RevisionId ||
                revision.Vector.Entries.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.Key) || entry.Value < 0)))
        {
            throw new InvalidDataException("The cloud sync record is invalid.");
        }
    }

    private static SyncConflictSet? ToConflictSet<T>(SyncRecordEnvelope<T> envelope)
    {
        if (envelope.Revisions.Count < 2 && envelope.Revisions.All(revision => !revision.IsConflict))
        {
            return null;
        }

        return new SyncConflictSet
        {
            EntityKind = envelope.EntityKind,
            RecordId = envelope.RecordId,
            DisplayName = GetConflictDisplayName(envelope),
            RevisionIds = envelope.Revisions.Select(revision => revision.RevisionId).ToArray(),
            IncludesDeletion = envelope.Revisions.Any(revision => revision.IsDeleted),
        };
    }

    private static SyncConflictSet CreateConflictSet(
        SyncEntityKind entityKind,
        string recordId,
        IReadOnlyList<SyncRecordRevisionEntity> entities)
    {
        var displayName = entityKind == SyncEntityKind.Group
            ? entities.Select(ToGroupRevision)
                .OrderBy(revision => revision.IsDeleted)
                .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
                .Select(revision => revision.Payload?.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            : entities.Select(ToSecretRevision)
                .OrderBy(revision => revision.IsDeleted)
                .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
                .Select(revision => revision.Payload?.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        return new SyncConflictSet
        {
            EntityKind = entityKind,
            RecordId = recordId,
            DisplayName = displayName ?? (entityKind == SyncEntityKind.Group ? "Deleted group" : "Deleted secret"),
            RevisionIds = entities.Select(entity => entity.RevisionId)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            IncludesDeletion = entities.Any(entity => entity.IsDeleted),
        };
    }

    private static string GetConflictDisplayName<T>(SyncRecordEnvelope<T> envelope)
    {
        var payload = envelope.Revisions
            .OrderBy(revision => revision.IsDeleted)
            .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
            .Select(revision => revision.Payload)
            .FirstOrDefault(candidate => candidate is not null);
        return payload switch
        {
            VaultGroup group => group.Name,
            VaultSecret secret => secret.Name,
            _ => envelope.EntityKind == SyncEntityKind.Group ? "Deleted group" : "Deleted secret",
        };
    }

    private static void ResolveGroupConflict(
        SQLiteConnection connection,
        IReadOnlyList<SyncRecordRevisionEntity> entities,
        ConflictResolutionRequest request,
        string deviceId)
    {
        if (request.Resolution == ConflictResolutionKind.KeepBoth)
        {
            throw new UserInputValidationException(
                "Keep both is available for secrets only. Choose one group version or combine the group details.");
        }

        var revisions = entities.Select(ToGroupRevision).ToArray();
        var selected = string.IsNullOrWhiteSpace(request.SelectedRevisionId)
            ? null
            : revisions.FirstOrDefault(revision => revision.RevisionId == request.SelectedRevisionId);
        var delete = request.Resolution switch
        {
            ConflictResolutionKind.AcceptDeletion => true,
            ConflictResolutionKind.RestoreGroup => false,
            ConflictResolutionKind.MoveSecretsAndDeleteGroup => true,
            _ => selected?.IsDeleted == true,
        };
        var payload = request.MergedGroup ?? selected?.Payload ??
            revisions.Where(revision => revision.Payload is not null)
                .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                .Select(revision => revision.Payload)
                .FirstOrDefault();
        if (!delete && payload is null)
        {
            throw new InvalidOperationException("Choose a group revision to keep.");
        }

        if (delete && connection.Table<SecretEntity>().Any(secret => secret.GroupId == request.RecordId))
        {
            throw new UserInputValidationException(
                "Move every secret out of this group before keeping the group deleted.");
        }

        var vector = SyncRevisionFactory.MergeAll(revisions).Increment(deviceId);
        var resolved = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            request.RecordId,
            vector,
            delete,
            delete ? DateTimeOffset.UtcNow : null,
            payload);
        foreach (var entity in entities)
        {
            connection.Delete<SyncRecordRevisionEntity>(entity.RevisionId);
        }

        connection.Insert(ToEntity(request.RecordId, SyncEntityKind.Group, resolved));
        if (delete)
        {
            connection.Delete<GroupEntity>(request.RecordId);
        }
        else
        {
            connection.InsertOrReplace(ToEntity(payload!));
        }
    }

    private void ResolveSecretConflict(
        SQLiteConnection connection,
        IReadOnlyList<SyncRecordRevisionEntity> entities,
        ConflictResolutionRequest request,
        string deviceId)
    {
        if (request.Resolution is ConflictResolutionKind.RestoreGroup or
            ConflictResolutionKind.MoveSecretsAndDeleteGroup)
        {
            throw new UserInputValidationException(
                "This group-only action cannot be used for a secret conflict.");
        }

        var revisions = entities.Select(ToSecretRevision).ToArray();
        var selected = string.IsNullOrWhiteSpace(request.SelectedRevisionId)
            ? null
            : revisions.FirstOrDefault(revision => revision.RevisionId == request.SelectedRevisionId);
        var delete = request.Resolution == ConflictResolutionKind.AcceptDeletion || selected?.IsDeleted == true;
        var payload = request.MergedSecret ?? selected?.Payload;
        if (request.MergedSecretDocument is not null)
        {
            if (payload is null || _encryption is null || _session is null)
            {
                throw new VaultLockedException();
            }

            if (request.MergedSecretDocument.Fields.Count > CloudResourceLimits.MaximumFieldsPerSecret)
            {
                throw new CloudResourceLimitException("The merged secret contains too many fields.");
            }

            var plaintext = JsonSerializer.SerializeToUtf8Bytes(request.MergedSecretDocument, JsonOptions);
            try
            {
                if (plaintext.Length > CloudResourceLimits.MaximumSecretPlaintextBytes)
                {
                    throw new CloudResourceLimitException("The merged secret exceeds the 1 MiB safety limit.");
                }

                payload = payload with
                {
                    Name = request.MergedSecretDocument.Name.Trim(),
                    SearchableFieldNames = string.Join(
                        ' ',
                        request.MergedSecretDocument.Fields
                            .Select(field => field.Key.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)),
                    EncryptedPayload = _session.UseKey(key => _encryption.Encrypt(
                        plaintext,
                        key.Span,
                        CreateSecretAssociatedData(
                            payload.Id,
                            payload.GroupId,
                            payload.EncryptionVersion))),
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        if (!delete && payload is null)
        {
            throw new InvalidOperationException("Choose a secret revision to keep.");
        }

        var vector = SyncRevisionFactory.MergeAll(revisions).Increment(deviceId);
        if (request.Resolution == ConflictResolutionKind.KeepBoth)
        {
            KeepAdditionalSecretRevisions(connection, revisions, selected, vector, deviceId);
        }

        var resolved = SyncRevisionFactory.Create(
            SyncEntityKind.Secret,
            request.RecordId,
            vector,
            delete,
            delete ? DateTimeOffset.UtcNow : null,
            delete ? null : payload);
        foreach (var entity in entities)
        {
            connection.Delete<SyncRecordRevisionEntity>(entity.RevisionId);
        }

        connection.Insert(ToEntity(request.RecordId, SyncEntityKind.Secret, resolved));
        if (delete)
        {
            connection.Delete<SecretEntity>(request.RecordId);
        }
        else
        {
            connection.InsertOrReplace(ToEntity(payload!));
        }
    }

    private void KeepAdditionalSecretRevisions(
        SQLiteConnection connection,
        IReadOnlyList<SyncRecordRevision<VaultSecret>> revisions,
        SyncRecordRevision<VaultSecret>? selected,
        VersionVector resolvedVector,
        string deviceId)
    {
        if (_encryption is null || _session is null)
        {
            throw new InvalidOperationException("Keep both is unavailable until the vault encryption session is initialized.");
        }

        foreach (var alternate in revisions.Where(revision =>
                     !revision.IsDeleted &&
                     revision.Payload is not null &&
                     revision.RevisionId != selected?.RevisionId))
        {
            var source = alternate.Payload!;
            var newId = Guid.NewGuid().ToString("N");
            var plaintext = _session.UseKey(key => _encryption.Decrypt(
                source.EncryptedPayload,
                key.Span,
                CreateSecretAssociatedData(source.Id, source.GroupId, source.EncryptionVersion)));
            try
            {
                var clone = source with
                {
                    Id = newId,
                    EncryptedPayload = _session.UseKey(key => _encryption.Encrypt(
                        plaintext,
                        key.Span,
                        CreateSecretAssociatedData(newId, source.GroupId, source.EncryptionVersion))),
                };
                connection.Insert(ToEntity(clone));
                var cloneRevision = SyncRevisionFactory.Create(
                    SyncEntityKind.Secret,
                    newId,
                    resolvedVector.Increment(deviceId),
                    false,
                    null,
                    clone);
                connection.Insert(ToEntity(newId, SyncEntityKind.Secret, cloneRevision));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] CreateSecretAssociatedData(string id, string groupId, int version) =>
        System.Text.Encoding.UTF8.GetBytes($"passcrate:secret:{version}:{id}:{groupId}");

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidDataException("Stored metadata is invalid.");

    private static SQLiteAsyncConnection CreateConnection(string databasePath) => new(
        databasePath,
        SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
}
