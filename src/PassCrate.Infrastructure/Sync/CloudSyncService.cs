using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Sync;

public sealed class CloudSyncService(
    ISyncRepository syncRepository,
    IVaultRepository vaultRepository,
    IVaultSession session,
    IKeyManagementService keyManagement,
    ICloudSnapshotService snapshots,
    ISyncMergeService merger,
    ICloudStorageProviderFactory providerFactory,
    ICloudAuthorizationService authorization,
    ICloudNetworkPolicy networkPolicy) : ICloudSyncService
{
    private const int RetainedMergedSnapshots = 10;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private CancellationTokenSource? _activeSync;
    private CloudSyncStatus _status = new();

    public event EventHandler<CloudSyncStatus>? StatusChanged;
    public CloudSyncStatus Status => _status;

    public async Task<IReadOnlyList<CloudVaultDescriptor>> FindVaultsAsync(
        CloudProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        EnsureNetworkAvailable();
        SetStatus(CloudSyncState.Connecting, provider, "Connecting to your cloud account…");
        await authorization.AuthorizeAsync(provider, cancellationToken).ConfigureAwait(false);
        var remote = await DownloadSnapshotHeadersAsync(
            providerFactory.Get(provider),
            cancellationToken).ConfigureAwait(false);
        var descriptors = remote
            .GroupBy(item => item.Snapshot.Header.VaultId, StringComparer.Ordinal)
            .Select(group => new CloudVaultDescriptor(
                group.Key,
                group.Max(item => item.Snapshot.Header.Generation),
                group.Count(),
                provider))
            .OrderByDescending(item => item.LatestGeneration)
            .ThenBy(item => item.VaultId, StringComparer.Ordinal)
            .ToArray();
        SetStatus(
            CloudSyncState.Ready,
            provider,
            descriptors.Length == 0 ? "No PassCrate backups found." : "PassCrate backups found.");
        return descriptors;
    }

    public async Task EnableAsync(
        CloudProviderKind provider,
        string vaultPassphrase,
        CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(vaultPassphrase);
        EnsureUnlocked();
        await keyManagement.VerifyPassphraseAsync(vaultPassphrase, cancellationToken).ConfigureAwait(false);
        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Connecting, provider, "Connecting to your cloud account…");
            var current = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (current is { IsEnabled: true } && current.Provider != provider)
            {
                await authorization.DisconnectAsync(current.Provider, cancellationToken).ConfigureAwait(false);
            }

            await authorization.AuthorizeAsync(provider, cancellationToken).ConfigureAwait(false);
            var storage = providerFactory.Get(provider);
            var remote = await DownloadSnapshotHeadersAsync(storage, cancellationToken).ConfigureAwait(false);
            var vaultId = current?.VaultId ?? Guid.NewGuid().ToString("N");
            var isReenablingExistingVault = current is { IsEnabled: false };
            if (remote.Any(item => !StringComparer.Ordinal.Equals(item.Snapshot.Header.VaultId, vaultId)))
            {
                throw new CloudVaultMismatchException();
            }

            var dataEncryptionKey = session.UseKey(key => key.ToArray());
            try
            {
                var recovery = await snapshots.CreateRecoveryEnvelopeAsync(
                    vaultId,
                    vaultPassphrase,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                var configuration = new CloudSyncConfiguration
                {
                    Version = 2,
                    IsEnabled = true,
                    Provider = provider,
                    VaultId = vaultId,
                    DeviceId = current?.DeviceId ?? Guid.NewGuid().ToString("N"),
                    Recovery = recovery,
                    WifiOnly = current?.WifiOnly ?? false,
                };
                await syncRepository.InitializeRecordVectorsAsync(
                    configuration.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                if (!isReenablingExistingVault)
                {
                    await UploadCurrentStateAsync(
                        storage,
                        configuration,
                        [],
                        generation: 1,
                        dataEncryptionKey,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The prior snapshots were protected by the old passphrase.
                    // Keep the vault locally disconnected until the replacement has
                    // uploaded and every old recovery envelope has been removed.
                    var state = await syncRepository.ExportStateV2Async(cancellationToken).ConfigureAwait(false);
                    var upload = await UploadSnapshotOnlyAsync(
                        storage,
                        configuration,
                        state,
                        [],
                        generation: 1,
                        dataEncryptionKey,
                        cancellationToken).ConfigureAwait(false);
                    var obsoleteSnapshots = remote
                        .Where(item => item.Snapshot.Header.VaultId == vaultId)
                        .ToArray();
                    await DeleteSnapshotsAsync(
                        storage,
                        obsoleteSnapshots,
                        upload.File.ProviderFileId,
                        cancellationToken).ConfigureAwait(false);
                    await syncRepository.DeleteSnapshotCatalogEntriesAsync(
                        obsoleteSnapshots
                            .Select(item => item.Snapshot.Header.SnapshotId)
                            .ToArray(),
                        cancellationToken).ConfigureAwait(false);
                    await CommitUploadedStateAsync(
                        configuration,
                        state,
                        [],
                        generation: 1,
                        dataEncryptionKey,
                        upload,
                        cancellationToken).ConfigureAwait(false);
                    SetStatus(
                        CloudSyncState.Ready,
                        configuration.Provider,
                        "Cloud sync is ready with your new passphrase.",
                        upload.CompletedAt,
                        CountConflicts(state));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailureStatus(exception, provider, null);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CloudSyncResult> SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        EnsureUnlocked();
        var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is not { IsEnabled: true } || configuration.Version != 2)
        {
            throw new InvalidOperationException("Cloud sync is not enabled.");
        }

        EnsureNetworkAvailable(configuration);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _activeSync = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationToken = _activeSync.Token;
        try
        {
            SetStatus(
                CloudSyncState.Syncing,
                configuration.Provider,
                "Syncing your vault…",
                configuration.LastSuccessfulSyncAt);
            var storage = providerFactory.Get(configuration.Provider);
            var dataEncryptionKey = session.UseKey(key => key.ToArray());
            try
            {
                var valid = await DownloadValidSnapshotsAsync(
                    storage,
                    configuration.VaultId,
                    dataEncryptionKey,
                    operationToken).ConfigureAwait(false);
                var heads = FindHeads(valid);
                var merged = await syncRepository.ExportStateV2Async(operationToken).ConfigureAwait(false);
                var conflicts = Array.Empty<SyncConflictSet>();
                foreach (var head in heads)
                {
                    var result = merger.Merge(merged, head.State!);
                    merged = result.State;
                    conflicts = result.Conflicts.ToArray();
                }

                var parents = heads
                    .Select(item => item.Snapshot.Header.SnapshotId)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var generation = Math.Max(
                    configuration.Generation,
                    valid.Count == 0 ? 0 : valid.Max(item => item.Snapshot.Header.Generation)) + 1;
                var upload = await UploadStateAsync(
                    storage,
                    configuration,
                    merged,
                    parents,
                    generation,
                    dataEncryptionKey,
                    operationToken).ConfigureAwait(false);
                await PruneAsync(storage, valid, upload, operationToken).ConfigureAwait(false);
                SetStatus(
                    CloudSyncState.Ready,
                    configuration.Provider,
                    "Sync complete.",
                    upload.CompletedAt,
                    conflicts.Length);
                return new CloudSyncResult(
                    upload.Snapshot.Header.SnapshotId,
                    conflicts.Length,
                    upload.CompletedAt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(
                CloudSyncState.Ready,
                configuration.Provider,
                "Sync paused until the vault is unlocked and foregrounded.",
                configuration.LastSuccessfulSyncAt);
            throw;
        }
        catch (Exception exception)
        {
            SetFailureStatus(exception, configuration.Provider, configuration.LastSuccessfulSyncAt);
            throw;
        }
        finally
        {
            _activeSync.Dispose();
            _activeSync = null;
            _operationLock.Release();
        }
    }

    public async Task ChangePassphraseAsync(
        string currentPassphrase,
        string newPassphrase,
        CancellationToken cancellationToken = default)
    {
        CredentialPolicy.ValidateVaultPassphrase(newPassphrase);
        await keyManagement.VerifyPassphraseAsync(currentPassphrase, cancellationToken).ConfigureAwait(false);

        var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (configuration is not { IsEnabled: true } || configuration.Version != 2)
        {
            await keyManagement.ChangePassphraseAsync(
                currentPassphrase,
                newPassphrase,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        EnsureUnlocked();
        EnsureNetworkAvailable(configuration);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(
                CloudSyncState.Syncing,
                configuration.Provider,
                "Updating encrypted cloud recovery…",
                configuration.LastSuccessfulSyncAt);
            await authorization.AuthorizeAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
            var storage = providerFactory.Get(configuration.Provider);
            var dataEncryptionKey = session.UseKey(key => key.ToArray());
            try
            {
                var valid = await DownloadValidSnapshotsAsync(
                    storage,
                    configuration.VaultId,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                var heads = FindHeads(valid);
                var parents = heads
                    .Select(item => item.Snapshot.Header.SnapshotId)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var generation = Math.Max(
                    configuration.Generation,
                    valid.Count == 0 ? 0 : valid.Max(item => item.Snapshot.Header.Generation)) + 1;
                var recovery = await snapshots.CreateRecoveryEnvelopeAsync(
                    configuration.VaultId,
                    newPassphrase,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                var replacement = configuration with { Recovery = recovery };
                var state = await syncRepository.ExportStateV2Async(cancellationToken).ConfigureAwait(false);

                // Upload the replacement first.  Until this succeeds, the old local
                // passphrase and cloud recovery envelope remain authoritative.
                var upload = await UploadSnapshotOnlyAsync(
                    storage,
                    replacement,
                    state,
                    parents,
                    generation,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);

                await keyManagement.ChangePassphraseAsync(
                    currentPassphrase,
                    newPassphrase,
                    cancellationToken).ConfigureAwait(false);

                var completedConfiguration = replacement with
                {
                    Version = 2,
                    IsEnabled = true,
                    HeadSnapshotIds = [upload.Snapshot.Header.SnapshotId],
                    Generation = generation,
                    LastSuccessfulSyncAt = upload.CompletedAt,
                };
                await syncRepository.SaveConfigurationAsync(
                    completedConfiguration,
                    cancellationToken).ConfigureAwait(false);
                await syncRepository.SaveSnapshotCatalogEntryAsync(new CloudSnapshotCatalogEntry
                {
                    SnapshotId = upload.Snapshot.Header.SnapshotId,
                    ProviderFileId = upload.File.ProviderFileId,
                    OpaqueName = upload.File.OpaqueName,
                    NamespaceId = SyncRevisionFactory.CreateNamespaceId(
                        replacement.VaultId,
                        dataEncryptionKey),
                    ParentSnapshotIds = parents,
                    Generation = generation,
                    UploadComplete = true,
                }, cancellationToken).ConfigureAwait(false);
                SetStatus(
                    CloudSyncState.Ready,
                    configuration.Provider,
                    "Vault passphrase changed and cloud recovery updated.",
                    upload.CompletedAt,
                    CountConflicts(state));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailureStatus(exception, configuration.Provider, configuration.LastSuccessfulSyncAt);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestoreAsync(
        CloudRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialPolicy.ValidateVaultPassphrase(request.VaultPassphrase);
        if (await vaultRepository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new DuplicateAccountException();
        }

        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? dataEncryptionKey = null;
        var localMutationStarted = false;
        try
        {
            SetStatus(CloudSyncState.Connecting, request.Provider, "Downloading your cloud backup…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            var storage = providerFactory.Get(request.Provider);
            var candidates = (await DownloadSnapshotHeadersAsync(storage, cancellationToken).ConfigureAwait(false))
                .Where(item => item.Snapshot.Header.VaultId == request.VaultId)
                .OrderByDescending(item => item.Snapshot.Header.Generation)
                .ThenBy(item => item.Snapshot.Header.SnapshotId, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new CloudSnapshotUnavailableException();
            }

            DownloadedSnapshot? recoverySource = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    dataEncryptionKey = await snapshots.RecoverDataEncryptionKeyAsync(
                        candidate.Snapshot,
                        request.VaultPassphrase,
                        cancellationToken).ConfigureAwait(false);
                    recoverySource = candidate;
                    break;
                }
                catch (Exception exception) when (
                    exception is CryptographicException or InvalidDataException or ArgumentException)
                {
                }
            }

            if (dataEncryptionKey is null || recoverySource is null)
            {
                throw new CloudRestoreFailedException();
            }

            var valid = new List<DownloadedSnapshot>();
            foreach (var candidate in candidates)
            {
                try
                {
                    valid.Add(candidate with
                    {
                        State = snapshots.DecryptState(candidate.Snapshot, dataEncryptionKey),
                    });
                }
                catch (Exception exception) when (
                    exception is CryptographicException or InvalidDataException or CloudResourceLimitException)
                {
                }
            }

            var heads = FindHeads(valid);
            if (heads.Count == 0)
            {
                throw new CloudSnapshotUnavailableException();
            }

            var merged = heads[0].State!;
            foreach (var head in heads.Skip(1))
            {
                merged = merger.Merge(merged, head.State!).State;
            }

            await syncRepository.BeginRestoreAsync(cancellationToken).ConfigureAwait(false);
            localMutationStarted = true;
            await keyManagement.InitializeRestoredVaultAsync(
                request.VaultPassphrase,
                dataEncryptionKey,
                cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var configuration = new CloudSyncConfiguration
            {
                Version = 2,
                IsEnabled = true,
                Provider = request.Provider,
                VaultId = request.VaultId,
                DeviceId = Guid.NewGuid().ToString("N"),
                Recovery = recoverySource.Snapshot.Header.Recovery,
                HeadSnapshotIds = heads
                    .Select(item => item.Snapshot.Header.SnapshotId)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                Generation = valid.Max(item => item.Snapshot.Header.Generation),
                LastSuccessfulSyncAt = now,
            };
            await syncRepository.ApplyMergedStateV2Async(
                merged,
                configuration,
                cancellationToken).ConfigureAwait(false);
            await syncRepository.CompleteRestoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(
                CloudSyncState.Ready,
                request.Provider,
                "Your vault was restored from the cloud backup.",
                now,
                CountConflicts(merged));
        }
        catch
        {
            if (localMutationStarted)
            {
                keyManagement.LockVault();
                await vaultRepository.DeleteAllAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (dataEncryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }

            _operationLock.Release();
        }
    }

    public Task SetWifiOnlyAsync(bool wifiOnly, CancellationToken cancellationToken = default) =>
        syncRepository.SetWifiOnlyAsync(wifiOnly, cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancelActiveSync();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (configuration is not null)
            {
                try
                {
                    await authorization.DisconnectAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await syncRepository.DisableAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            SetStatus(CloudSyncState.Disabled, null, "Cloud sync is off. Your cloud backups were kept.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Cloud sync is not configured.");
        EnsureUnlocked();
        EnsureNetworkAvailable(configuration);
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Connecting, configuration.Provider, "Reconnecting your cloud account…");
            await authorization.AuthorizeAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
            var key = session.UseKey(value => value.ToArray());
            try
            {
                var valid = await DownloadValidSnapshotsAsync(
                    providerFactory.Get(configuration.Provider),
                    configuration.VaultId,
                    key,
                    cancellationToken).ConfigureAwait(false);
                if (valid.Count == 0)
                {
                    throw new CloudVaultMismatchException();
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (Exception exception)
        {
            SetFailureStatus(exception, configuration.Provider, configuration.LastSuccessfulSyncAt);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }

        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCloudVaultAsync(
        string vaultPassphrase,
        string typedConfirmation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(typedConfirmation.Trim(), "DELETE CLOUD VAULT", StringComparison.Ordinal))
        {
            throw new UserInputValidationException("Type DELETE CLOUD VAULT exactly to confirm deletion.");
        }

        await keyManagement.VerifyPassphraseAsync(vaultPassphrase, cancellationToken).ConfigureAwait(false);
        EnsureUnlocked();
        CancelActiveSync();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cloud sync is not configured.");
            EnsureNetworkAvailable(configuration);
            var storage = providerFactory.Get(configuration.Provider);
            var key = session.UseKey(value => value.ToArray());
            try
            {
                var namespaceId = SyncRevisionFactory.CreateNamespaceId(configuration.VaultId, key);
                var prefix = $"pv2-{namespaceId}-";
                var catalog = await syncRepository.GetSnapshotCatalogAsync(cancellationToken).ConfigureAwait(false);
                var files = await storage.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var targets = files
                    .Where(file => file.OpaqueName.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(file => file.ProviderFileId)
                    .Concat(catalog
                        .Where(item => item.NamespaceId == namespaceId)
                        .Select(item => item.ProviderFileId))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                foreach (var target in targets)
                {
                    await storage.DeleteAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            await authorization.DisconnectAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
            await syncRepository.DisableAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(CloudSyncState.Disabled, null, "The cloud backup was deleted. Your vault on this device was kept.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DeleteAllProviderDataAsync(
        string vaultPassphrase,
        string typedConfirmation,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(typedConfirmation.Trim(), "DELETE ALL CLOUD VAULTS", StringComparison.Ordinal))
        {
            throw new UserInputValidationException("Type DELETE ALL CLOUD VAULTS exactly to confirm deletion.");
        }

        await keyManagement.VerifyPassphraseAsync(vaultPassphrase, cancellationToken).ConfigureAwait(false);
        CancelActiveSync();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Cloud sync is not configured.");
            EnsureNetworkAvailable(configuration);
            await authorization.AuthorizeAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
            var storage = providerFactory.Get(configuration.Provider);
            var files = await storage.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var file in files.Where(file => file.OpaqueName.EndsWith(".pvs", StringComparison.Ordinal)))
            {
                await storage.DeleteAsync(file.ProviderFileId, cancellationToken).ConfigureAwait(false);
            }

            await authorization.DisconnectAsync(configuration.Provider, cancellationToken).ConfigureAwait(false);
            await syncRepository.DisableAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(CloudSyncState.Disabled, null, "All PassCrate cloud backups were deleted. Your vault on this device was kept.");
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void CancelActiveSync() => _activeSync?.Cancel();

    public void ResetLocalState()
    {
        CancelActiveSync();
        SetStatus(CloudSyncState.Disabled, null, "Cloud sync is off.");
    }

    private async Task<UploadResult> UploadCurrentStateAsync(
        ICloudStorageProvider storage,
        CloudSyncConfiguration configuration,
        IReadOnlyList<string> parents,
        long generation,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        var state = await syncRepository.ExportStateV2Async(cancellationToken).ConfigureAwait(false);
        var result = await UploadStateAsync(
            storage,
            configuration,
            state,
            parents,
            generation,
            dataEncryptionKey,
            cancellationToken).ConfigureAwait(false);
        SetStatus(
            CloudSyncState.Ready,
            configuration.Provider,
            "Encrypted cloud sync enabled.",
            result.CompletedAt,
            CountConflicts(state));
        return result;
    }

    private static async Task DeleteSnapshotsAsync(
        ICloudStorageProvider storage,
        IEnumerable<DownloadedSnapshot> snapshotsToDelete,
        string retainedProviderFileId,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshotsToDelete
                     .Where(item => !StringComparer.Ordinal.Equals(item.File.ProviderFileId, retainedProviderFileId))
                     .GroupBy(item => item.File.ProviderFileId, StringComparer.Ordinal)
                     .Select(group => group.Key))
        {
            await storage.DeleteAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UploadResult> UploadStateAsync(
        ICloudStorageProvider storage,
        CloudSyncConfiguration configuration,
        CloudVaultStateV2 state,
        IReadOnlyList<string> parents,
        long generation,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        var result = await UploadSnapshotOnlyAsync(
            storage,
            configuration,
            state,
            parents,
            generation,
            dataEncryptionKey,
            cancellationToken).ConfigureAwait(false);
        await CommitUploadedStateAsync(
            configuration,
            state,
            parents,
            generation,
            dataEncryptionKey,
            result,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task CommitUploadedStateAsync(
        CloudSyncConfiguration configuration,
        CloudVaultStateV2 state,
        IReadOnlyList<string> parents,
        long generation,
        ReadOnlyMemory<byte> dataEncryptionKey,
        UploadResult result,
        CancellationToken cancellationToken)
    {
        var snapshotId = result.Snapshot.Header.SnapshotId;
        var namespaceId = SyncRevisionFactory.CreateNamespaceId(
            configuration.VaultId,
            dataEncryptionKey.Span);
        var completedAt = result.CompletedAt;
        var updated = configuration with
        {
            Version = 2,
            IsEnabled = true,
            HeadSnapshotIds = [snapshotId],
            Generation = generation,
            LastSuccessfulSyncAt = completedAt,
        };
        await syncRepository.ApplyMergedStateV2Async(state, updated, cancellationToken).ConfigureAwait(false);
        await syncRepository.SaveSnapshotCatalogEntryAsync(new CloudSnapshotCatalogEntry
        {
            SnapshotId = snapshotId,
            ProviderFileId = result.File.ProviderFileId,
            OpaqueName = result.File.OpaqueName,
            NamespaceId = namespaceId,
            ParentSnapshotIds = parents,
            Generation = generation,
            UploadComplete = true,
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<UploadResult> UploadSnapshotOnlyAsync(
        ICloudStorageProvider storage,
        CloudSyncConfiguration configuration,
        CloudVaultStateV2 state,
        IReadOnlyList<string> parents,
        long generation,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        var snapshotId = Guid.NewGuid().ToString("N");
        var namespaceId = SyncRevisionFactory.CreateNamespaceId(configuration.VaultId, dataEncryptionKey.Span);
        var header = new CloudVaultSnapshotHeaderV2
        {
            VaultId = configuration.VaultId,
            SnapshotId = snapshotId,
            ParentSnapshotIds = parents,
            Generation = generation,
            Recovery = configuration.Recovery,
        };
        var content = snapshots.SerializeEncryptedSnapshot(header, state, dataEncryptionKey);
        try
        {
            var opaqueName = $"pv2-{namespaceId}-{snapshotId}.pvs";
            var file = await storage.UploadImmutableAsync(opaqueName, content, cancellationToken).ConfigureAwait(false);
            return new UploadResult(
                new CloudVaultSnapshotV2
                {
                    Header = header,
                    EncryptedVaultState = new EncryptedPayload
                    {
                        Nonce = [],
                        Ciphertext = [],
                        AuthenticationTag = [],
                    },
                },
                file,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private async Task<IReadOnlyList<DownloadedSnapshot>> DownloadSnapshotHeadersAsync(
        ICloudStorageProvider storage,
        CancellationToken cancellationToken)
    {
        var files = await storage.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new List<DownloadedSnapshot>();
        long cumulativeBytes = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Size <= 0 || file.Size > CloudResourceLimits.MaximumSnapshotBytes)
            {
                throw new CloudResourceLimitException("Cloud cleanup is required before PassCrate can sync.");
            }

            await using var stream = await storage.DownloadAsync(
                file.ProviderFileId,
                CloudResourceLimits.MaximumSnapshotBytes,
                cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            cumulativeBytes = checked(cumulativeBytes + buffer.Length);
            if (cumulativeBytes > CloudResourceLimits.MaximumDownloadedBytesPerOperation)
            {
                throw new CloudResourceLimitException("Cloud cleanup is required before PassCrate can sync.");
            }

            var content = buffer.ToArray();
            try
            {
                result.Add(new DownloadedSnapshot(
                    file,
                    snapshots.DeserializeSnapshotV2(content),
                    null));
            }
            catch (Exception exception) when (
                exception is InvalidDataException or JsonException or NotSupportedException)
            {
                // Non-PassCrate or corrupt files are ignored during discovery.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(content);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<DownloadedSnapshot>> DownloadValidSnapshotsAsync(
        ICloudStorageProvider storage,
        string vaultId,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        var headers = await DownloadSnapshotHeadersAsync(storage, cancellationToken).ConfigureAwait(false);
        var valid = new List<DownloadedSnapshot>();
        foreach (var item in headers.Where(item => item.Snapshot.Header.VaultId == vaultId))
        {
            try
            {
                valid.Add(item with
                {
                    State = snapshots.DecryptState(item.Snapshot, dataEncryptionKey),
                });
            }
            catch (Exception exception) when (
                exception is CryptographicException or InvalidDataException or CloudResourceLimitException)
            {
            }
        }

        return valid;
    }

    private static IReadOnlyList<DownloadedSnapshot> FindHeads(
        IReadOnlyList<DownloadedSnapshot> snapshotSet)
    {
        var parentIds = snapshotSet
            .SelectMany(item => item.Snapshot.Header.ParentSnapshotIds)
            .ToHashSet(StringComparer.Ordinal);
        return snapshotSet
            .Where(item => !parentIds.Contains(item.Snapshot.Header.SnapshotId))
            .OrderBy(item => item.Snapshot.Header.SnapshotId, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task PruneAsync(
        ICloudStorageProvider storage,
        IReadOnlyList<DownloadedSnapshot> existing,
        UploadResult uploaded,
        CancellationToken cancellationToken)
    {
        var all = existing.Append(new DownloadedSnapshot(
            uploaded.File,
            uploaded.Snapshot,
            null)).ToArray();
        var byId = all.ToDictionary(
            item => item.Snapshot.Header.SnapshotId,
            StringComparer.Ordinal);
        var retained = all
            .OrderByDescending(item => item.Snapshot.Header.Generation)
            .ThenBy(item => item.Snapshot.Header.SnapshotId, StringComparer.Ordinal)
            .Take(RetainedMergedSnapshots)
            .Select(item => item.Snapshot.Header.SnapshotId)
            .ToHashSet(StringComparer.Ordinal);
        var dominated = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(uploaded.Snapshot.Header.ParentSnapshotIds);
        while (pending.TryPop(out var snapshotId))
        {
            if (!dominated.Add(snapshotId) || !byId.TryGetValue(snapshotId, out var item))
            {
                continue;
            }

            foreach (var parent in item.Snapshot.Header.ParentSnapshotIds)
            {
                pending.Push(parent);
            }
        }

        foreach (var item in all.Where(item =>
                     dominated.Contains(item.Snapshot.Header.SnapshotId) &&
                     !retained.Contains(item.Snapshot.Header.SnapshotId)))
        {
            await storage.DeleteAsync(item.File.ProviderFileId, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureNetworkAvailable(CloudSyncConfiguration? configuration = null)
    {
        if (!networkPolicy.IsInternetAvailable)
        {
            SetStatus(
                CloudSyncState.Offline,
                configuration?.Provider,
                "Offline. Local changes are safe and will sync later.",
                configuration?.LastSuccessfulSyncAt);
            throw new HttpRequestException("No internet connection is available.");
        }

        if (configuration is { WifiOnly: true } && !networkPolicy.IsUsingWifi)
        {
            SetStatus(
                CloudSyncState.Offline,
                configuration.Provider,
                "Waiting for Wi-Fi. Local changes are safe.",
                configuration.LastSuccessfulSyncAt);
            throw new HttpRequestException("Cloud sync is waiting for Wi-Fi.");
        }
    }

    private void EnsureUnlocked()
    {
        if (!session.IsUnlocked)
        {
            throw new VaultLockedException();
        }
    }

    private void SetFailureStatus(
        Exception exception,
        CloudProviderKind provider,
        DateTimeOffset? lastSuccessfulSyncAt)
    {
        var (state, message) = exception switch
        {
            CloudProviderConfigurationException configuration =>
                (CloudSyncState.ReconnectRequired, configuration.Message),
            CloudAuthorizationRequiredException =>
                (CloudSyncState.ReconnectRequired, "Your cloud connection expired. Reconnect to continue."),
            CloudQuotaException =>
                (CloudSyncState.QuotaLimited, "Cloud storage is full or temporarily busy. Your changes on this device are safe."),
            CloudResourceLimitException =>
                (CloudSyncState.CloudCleanupRequired, "Remove old PassCrate backups before syncing again."),
            HttpRequestException when !networkPolicy.IsInternetAvailable =>
                (CloudSyncState.Offline, "Offline. Local changes are safe and will sync later."),
            HttpRequestException =>
                (CloudSyncState.Error, "PassCrate could not reach the cloud service. Your changes on this device are safe."),
            TimeoutException =>
                (CloudSyncState.Error, "The cloud service took too long to respond. Your changes on this device are safe."),
            _ => (CloudSyncState.Error, "PassCrate could not complete cloud sync. Local changes are safe."),
        };
        SetStatus(state, provider, message, lastSuccessfulSyncAt);
    }

    private void SetStatus(
        CloudSyncState state,
        CloudProviderKind? provider,
        string message,
        DateTimeOffset? lastSuccessfulSyncAt = null,
        int conflictCount = 0)
    {
        _status = new CloudSyncStatus
        {
            State = state,
            Provider = provider,
            Message = message,
            LastSuccessfulSyncAt = lastSuccessfulSyncAt,
            ConflictCount = conflictCount,
        };
        StatusChanged?.Invoke(this, _status);
    }

    private static int CountConflicts(CloudVaultStateV2 state) =>
        state.Groups.Count(envelope =>
            envelope.Revisions.Count > 1 || envelope.Revisions.Any(revision => revision.IsConflict)) +
        state.Secrets.Count(envelope =>
            envelope.Revisions.Count > 1 || envelope.Revisions.Any(revision => revision.IsConflict));

    private sealed record DownloadedSnapshot(
        CloudFileInfo File,
        CloudVaultSnapshotV2 Snapshot,
        CloudVaultStateV2? State);

    private sealed record UploadResult(
        CloudVaultSnapshotV2 Snapshot,
        CloudFileInfo File,
        DateTimeOffset CompletedAt);
}
