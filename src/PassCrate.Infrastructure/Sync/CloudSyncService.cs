using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Sync;

public sealed partial class CloudSyncService(
    ISyncRepository syncRepository,
    IVaultRepository vaultRepository,
    IVaultSession session,
    IKeyManagementService keyManagement,
    IEncryptionService encryption,
    ICloudSnapshotService snapshots,
    ISyncMergeService merger,
    ICloudStorageProviderFactory providerFactory,
    ICloudAuthorizationService authorization,
    ICloudNetworkPolicy networkPolicy) : ICloudSyncService
{
    private const int RetainedMergedSnapshots = 10;
    private static readonly JsonSerializerOptions SecretJsonOptions = new(JsonSerializerDefaults.Web);
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
                provider,
                group.Select(item => item.File.ModifiedAt).Max()))
            .OrderByDescending(item => item.LastModifiedAt)
            .ThenByDescending(item => item.LatestGeneration)
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
            if (!session.IsUnlocked)
            {
                // Interactive OAuth can deactivate the app. PassCrate deliberately
                // locks at that boundary, so setup must continue only after the user
                // unlocks again instead of trying to use a cleared vault key.
                throw new CloudSetupRequiresUnlockException();
            }

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
                    var valid = await DownloadValidSnapshotsAsync(
                        storage,
                        vaultId,
                        dataEncryptionKey,
                        cancellationToken).ConfigureAwait(false);
                    if (remote.Any(item => item.Snapshot.Header.VaultId == vaultId) && valid.Count == 0)
                    {
                        throw new CloudSnapshotUnavailableException();
                    }

                    var heads = FindHeads(valid);
                    var parents = heads
                        .Select(item => item.Snapshot.Header.SnapshotId)
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    var generation = Math.Max(
                        current?.Generation ?? 0,
                        valid.Count == 0 ? 0 : valid.Max(item => item.Snapshot.Header.Generation)) + 1;
                    var state = await syncRepository.ExportStateV2Async(
                        configuration.DeviceId,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var head in heads)
                    {
                        state = merger.Merge(state, head.State!).State;
                    }
                    var upload = await UploadStateAsync(
                        storage,
                        configuration,
                        state,
                        parents,
                        generation,
                        dataEncryptionKey,
                        cancellationToken).ConfigureAwait(false);
                    await PruneAsync(storage, valid, upload, cancellationToken).ConfigureAwait(false);
                    SetStatus(
                        CloudSyncState.Ready,
                        configuration.Provider,
                        "Cloud sync is ready.",
                        upload.CompletedAt,
                        CountConflicts(state));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }
        }
        catch (CloudSetupRequiresUnlockException)
        {
            SetStatus(
                CloudSyncState.Connecting,
                provider,
                "Cloud access was approved. Unlock your vault to finish connecting.");
            throw;
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
                var uploadConfiguration = configuration.IsRecoveryUploadPending
                    ? configuration with { IsRecoveryUploadPending = false }
                    : valid.Count == 0
                        ? configuration
                    : configuration with
                    {
                        // A second device may still use its former local unlock
                        // passphrase. Preserve the newest cloud recovery envelope so
                        // that device cannot make the former cloud passphrase valid.
                        Recovery = SelectAuthoritativeSnapshot(valid).Snapshot.Header.Recovery,
                    };
                var heads = FindHeads(valid);
                var merged = await syncRepository.ExportStateV2Async(
                    configuration.DeviceId,
                    operationToken).ConfigureAwait(false);
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
                    uploadConfiguration,
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
                var state = await syncRepository.ExportStateV2Async(
                    configuration.DeviceId,
                    cancellationToken).ConfigureAwait(false);
                foreach (var head in heads)
                {
                    state = merger.Merge(state, head.State!).State;
                }

                // First upload and verify a checkpoint that still uses the old
                // recovery envelope. A crash before the local transaction therefore
                // leaves both local and cloud recovery on the old passphrase.
                var checkpoint = await UploadSnapshotOnlyAsync(
                    storage,
                    configuration,
                    state,
                    parents,
                    generation,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                await VerifyOrDeleteUploadedSnapshotAsync(
                    storage,
                    checkpoint,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);

                var pendingConfiguration = configuration with
                {
                    Version = 2,
                    IsEnabled = true,
                    Recovery = recovery,
                    HeadSnapshotIds = [checkpoint.Snapshot.Header.SnapshotId],
                    Generation = generation,
                    LastSuccessfulSyncAt = checkpoint.CompletedAt,
                    IsRecoveryUploadPending = true,
                };
                var metadata = await keyManagement.PreparePassphraseChangeMetadataAsync(
                    newPassphrase,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                try
                {
                    await syncRepository.CommitPassphraseRotationAsync(
                        metadata,
                        pendingConfiguration,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    try
                    {
                        await storage.DeleteAsync(checkpoint.File.ProviderFileId, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // This checkpoint still uses the old recovery envelope and
                        // is safe to retain if cleanup is temporarily unavailable.
                    }

                    throw;
                }

                try
                {
                    var completedConfiguration = pendingConfiguration with
                    {
                        IsRecoveryUploadPending = false,
                    };
                    var upload = await UploadStateAsync(
                        storage,
                        completedConfiguration,
                        state,
                        [checkpoint.Snapshot.Header.SnapshotId],
                        generation + 1,
                        dataEncryptionKey,
                        cancellationToken).ConfigureAwait(false);
                    SetStatus(
                        CloudSyncState.Ready,
                        configuration.Provider,
                        "Vault passphrase changed and cloud recovery updated.",
                        upload.CompletedAt,
                        CountConflicts(state));
                }
                catch (Exception)
                {
                    // The local metadata and sync configuration were committed in
                    // one transaction. Keep the new local passphrase and let the
                    // next normal sync publish its pending recovery envelope.
                    SetStatus(
                        CloudSyncState.Error,
                        configuration.Provider,
                        "Passphrase changed on this phone. Cloud recovery will retry automatically.",
                        checkpoint.CompletedAt,
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

            // Passphrase rotation creates the authoritative newest snapshot.
            // Falling back to an older recovery envelope would incorrectly accept
            // a former passphrase after the user has changed it.
            var recoverySource = SelectAuthoritativeSnapshot(candidates);
            try
            {
                dataEncryptionKey = await snapshots.RecoverDataEncryptionKeyAsync(
                    recoverySource.Snapshot,
                    request.VaultPassphrase,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is CryptographicException or InvalidDataException or ArgumentException)
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

    public async Task<CloudVaultContentsSummary> ValidateCloudVaultAsync(
        CloudRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialPolicy.ValidateVaultPassphrase(request.VaultPassphrase);
        EnsureUnlocked();
        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Connecting, request.Provider, "Checking the selected cloud backup…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            EnsureUnlockedAfterAuthorization();
            using var prepared = await PrepareCloudVaultAsync(
                providerFactory.Get(request.Provider),
                request.VaultId,
                request.VaultPassphrase,
                cancellationToken).ConfigureAwait(false);
            var summary = Summarize(prepared.State);
            SetStatus(
                CloudSyncState.VaultMismatch,
                request.Provider,
                $"Cloud backup verified: {summary.GroupCount} groups and {summary.SecretCount} secrets.");
            return summary;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailureStatus(exception, request.Provider, null);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task RestoreReplacingLocalAsync(
        CloudRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialPolicy.ValidateVaultPassphrase(request.VaultPassphrase);
        EnsureUnlocked();
        if (await vaultRepository.GetVaultMetadataAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException("No local vault exists to replace.");
        }

        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        byte[]? originalDataEncryptionKey = null;
        var replacementCommitted = false;
        try
        {
            SetStatus(CloudSyncState.Connecting, request.Provider, "Verifying the cloud backup before replacing this vault…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            EnsureUnlockedAfterAuthorization();
            using var prepared = await PrepareCloudVaultAsync(
                providerFactory.Get(request.Provider),
                request.VaultId,
                request.VaultPassphrase,
                cancellationToken).ConfigureAwait(false);
            var metadata = await keyManagement.PrepareRestoredVaultMetadataAsync(
                request.VaultPassphrase,
                prepared.DataEncryptionKey,
                cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var configuration = new CloudSyncConfiguration
            {
                Version = 2,
                IsEnabled = true,
                Provider = request.Provider,
                VaultId = request.VaultId,
                DeviceId = Guid.NewGuid().ToString("N"),
                Recovery = prepared.RecoverySource.Snapshot.Header.Recovery,
                HeadSnapshotIds = prepared.Heads
                    .Select(item => item.Snapshot.Header.SnapshotId)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                Generation = prepared.ValidSnapshots.Max(item => item.Snapshot.Header.Generation),
                LastSuccessfulSyncAt = now,
            };
            originalDataEncryptionKey = session.UseKey(key => key.ToArray());
            keyManagement.LockVault();
            await syncRepository.ReplaceVaultStateV2Async(
                metadata,
                prepared.State,
                configuration,
                cancellationToken).ConfigureAwait(false);
            replacementCommitted = true;
            await keyManagement.ActivateRestoredVaultAsync(
                prepared.DataEncryptionKey,
                cancellationToken).ConfigureAwait(false);
            SetStatus(
                CloudSyncState.Ready,
                request.Provider,
                "The existing cloud vault is now restored on this device.",
                now,
                CountConflicts(prepared.State));
        }
        catch (Exception exception)
        {
            if (!replacementCommitted && originalDataEncryptionKey is not null)
            {
                session.Unlock(originalDataEncryptionKey);
            }

            if (exception is not OperationCanceledException)
            {
                SetFailureStatus(exception, request.Provider, null);
            }
            throw;
        }
        finally
        {
            if (originalDataEncryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(originalDataEncryptionKey);
            }

            _operationLock.Release();
        }
    }

    public async Task ReplaceCloudVaultAsync(
        CloudVaultReplacementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.TypedConfirmation.Trim(), "REPLACE CLOUD BACKUP", StringComparison.Ordinal))
        {
            throw new UserInputValidationException("Type REPLACE CLOUD BACKUP exactly to confirm replacement.");
        }

        CredentialPolicy.ValidateVaultPassphrase(request.LocalVaultPassphrase);
        EnsureUnlocked();
        await keyManagement.VerifyPassphraseAsync(request.LocalVaultPassphrase, cancellationToken).ConfigureAwait(false);
        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Syncing, request.Provider, "Creating and verifying the replacement backup…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            EnsureUnlockedAfterAuthorization();
            var storage = providerFactory.Get(request.Provider);
            var remote = await DownloadSnapshotHeadersAsync(storage, cancellationToken).ConfigureAwait(false);
            var obsolete = remote
                .Where(item => item.Snapshot.Header.VaultId == request.CloudVaultId)
                .ToArray();
            if (obsolete.Length == 0)
            {
                throw new CloudSnapshotUnavailableException();
            }

            var dataEncryptionKey = session.UseKey(key => key.ToArray());
            try
            {
                var existingConfiguration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                var vaultId = existingConfiguration?.VaultId ?? Guid.NewGuid().ToString("N");
                if (StringComparer.Ordinal.Equals(vaultId, request.CloudVaultId))
                {
                    throw new InvalidOperationException("The selected backup already belongs to this vault.");
                }

                var deviceId = existingConfiguration?.DeviceId ?? Guid.NewGuid().ToString("N");
                await syncRepository.InitializeRecordVectorsAsync(deviceId, cancellationToken).ConfigureAwait(false);
                var recovery = await snapshots.CreateRecoveryEnvelopeAsync(
                    vaultId,
                    request.LocalVaultPassphrase,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                var configuration = new CloudSyncConfiguration
                {
                    Version = 2,
                    IsEnabled = true,
                    Provider = request.Provider,
                    VaultId = vaultId,
                    DeviceId = deviceId,
                    Recovery = recovery,
                    WifiOnly = existingConfiguration?.WifiOnly ?? false,
                };
                var state = await syncRepository.ExportStateV2Async(deviceId, cancellationToken).ConfigureAwait(false);
                var upload = await UploadSnapshotOnlyAsync(
                    storage,
                    configuration,
                    state,
                    [],
                    generation: 1,
                    dataEncryptionKey,
                    cancellationToken).ConfigureAwait(false);
                await VerifyUploadedSnapshotAsync(storage, upload, dataEncryptionKey, cancellationToken).ConfigureAwait(false);
                await CommitUploadedStateAsync(
                    configuration,
                    state,
                    [],
                    generation: 1,
                    dataEncryptionKey,
                    upload,
                    cancellationToken).ConfigureAwait(false);
                var cleanupComplete = await TryDeleteSnapshotsAsync(
                    storage,
                    obsolete,
                    cancellationToken).ConfigureAwait(false);
                SetStatus(
                    cleanupComplete ? CloudSyncState.Ready : CloudSyncState.CloudCleanupRequired,
                    request.Provider,
                    cleanupComplete
                        ? "The new vault replaced the selected cloud backup."
                        : "The new backup is safe, but the older backup could not be removed. Try cleanup again later.",
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
            SetFailureStatus(exception, request.Provider, null);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CloudVaultMergePreview> PreviewMergeAsync(
        CloudVaultMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateMergeRequest(request);
        EnsureUnlocked();
        await keyManagement.VerifyPassphraseAsync(request.LocalVaultPassphrase, cancellationToken).ConfigureAwait(false);
        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Connecting, request.Provider, "Checking both vaults before merging…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            EnsureUnlockedAfterAuthorization();
            using var prepared = await PrepareCloudVaultAsync(
                providerFactory.Get(request.Provider),
                request.CloudVaultId,
                request.CloudVaultPassphrase,
                cancellationToken).ConfigureAwait(false);
            var previewKey = session.UseKey(key => key.ToArray());
            CloudVaultMergePreview preview;
            try
            {
                var config = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                var local = await syncRepository.ExportStateV2Async(config?.DeviceId ?? "merge-preview", cancellationToken).ConfigureAwait(false);
                preview = CombineByIdentityAndName(local, ReencryptCloudSecrets(prepared.State, prepared.DataEncryptionKey, previewKey), previewKey).Preview;
            }
            finally { CryptographicOperations.ZeroMemory(previewKey); }
            SetStatus(
                CloudSyncState.VaultMismatch,
                request.Provider,
                $"Merge ready: {preview.ImportedItems} cloud items will be added, {preview.DuplicateSecrets} identical secrets will be skipped, and {preview.SecretsNeedingReview} items need review.");
            return preview;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailureStatus(exception, request.Provider, null);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<CloudVaultMergeResult> MergeDifferentVaultAsync(
        CloudVaultMergeRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateMergeRequest(request);
        EnsureUnlocked();
        await keyManagement.VerifyPassphraseAsync(request.LocalVaultPassphrase, cancellationToken).ConfigureAwait(false);
        EnsureNetworkAvailable();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(CloudSyncState.Syncing, request.Provider, "Merging and protecting both vaults…");
            await authorization.AuthorizeAsync(request.Provider, cancellationToken).ConfigureAwait(false);
            EnsureUnlockedAfterAuthorization();
            var storage = providerFactory.Get(request.Provider);
            using var prepared = await PrepareCloudVaultAsync(
                storage,
                request.CloudVaultId,
                request.CloudVaultPassphrase,
                cancellationToken).ConfigureAwait(false);
            var localKey = session.UseKey(key => key.ToArray());
            try
            {
                var existingConfiguration = await syncRepository.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                var vaultId = existingConfiguration?.VaultId ?? Guid.NewGuid().ToString("N");
                var deviceId = existingConfiguration?.DeviceId ?? Guid.NewGuid().ToString("N");
                await syncRepository.InitializeRecordVectorsAsync(deviceId, cancellationToken).ConfigureAwait(false);
                var localState = await syncRepository.ExportStateV2Async(deviceId, cancellationToken).ConfigureAwait(false);
                var importedCloudState = ReencryptCloudSecrets(
                    prepared.State,
                    prepared.DataEncryptionKey,
                    localKey);
                var combination = CombineByIdentityAndName(localState, importedCloudState, localKey);
                var mergedState = combination.State;
                var preview = combination.Preview;
                var recovery = await snapshots.CreateRecoveryEnvelopeAsync(
                    vaultId,
                    request.LocalVaultPassphrase,
                    localKey,
                    cancellationToken).ConfigureAwait(false);
                var configuration = new CloudSyncConfiguration
                {
                    Version = 2,
                    IsEnabled = true,
                    Provider = request.Provider,
                    VaultId = vaultId,
                    DeviceId = deviceId,
                    Recovery = recovery,
                    WifiOnly = existingConfiguration?.WifiOnly ?? false,
                };
                var upload = await UploadSnapshotOnlyAsync(
                    storage,
                    configuration,
                    mergedState,
                    [],
                    generation: 1,
                    localKey,
                    cancellationToken).ConfigureAwait(false);
                await VerifyUploadedSnapshotAsync(storage, upload, localKey, cancellationToken).ConfigureAwait(false);
                await CommitUploadedStateAsync(
                    configuration,
                    mergedState,
                    [],
                    generation: 1,
                    localKey,
                    upload,
                    cancellationToken).ConfigureAwait(false);
                var cleanupComplete = await TryDeleteSnapshotsAsync(
                    storage,
                    prepared.Candidates,
                    cancellationToken).ConfigureAwait(false);
                SetStatus(
                    cleanupComplete ? CloudSyncState.Ready : CloudSyncState.CloudCleanupRequired,
                    request.Provider,
                    cleanupComplete
                        ? "The cloud data was merged into this vault. Any different same-name secrets are ready for Conflict Review."
                        : "The merged backup is safe, but the older backup could not be removed. Try cleanup again later.",
                    upload.CompletedAt,
                    CountConflicts(mergedState));
                return new CloudVaultMergeResult(
                    preview,
                    new CloudSyncResult(
                        upload.Snapshot.Header.SnapshotId,
                        CountConflicts(mergedState),
                        upload.CompletedAt));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(localKey);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetFailureStatus(exception, request.Provider, null);
            throw;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task SwitchCloudAccountAsync(
        CloudProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        CancelActiveSync();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await authorization.DisconnectAsync(provider, cancellationToken).ConfigureAwait(false);
            SetStatus(CloudSyncState.Disabled, null, "Cloud account disconnected. Choose a provider to connect another account.");
        }
        finally
        {
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

    private async Task<PreparedCloudVault> PrepareCloudVaultAsync(
        ICloudStorageProvider storage,
        string vaultId,
        string vaultPassphrase,
        CancellationToken cancellationToken)
    {
        var candidates = (await DownloadSnapshotHeadersAsync(storage, cancellationToken).ConfigureAwait(false))
            .Where(item => item.Snapshot.Header.VaultId == vaultId)
            .OrderByDescending(item => item.Snapshot.Header.Generation)
            .ThenByDescending(item => item.File.ModifiedAt)
            .ThenBy(item => item.Snapshot.Header.SnapshotId, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new CloudSnapshotUnavailableException();
        }

        var recoverySource = SelectAuthoritativeSnapshot(candidates);
        byte[] dataEncryptionKey;
        try
        {
            dataEncryptionKey = await snapshots.RecoverDataEncryptionKeyAsync(
                recoverySource.Snapshot,
                vaultPassphrase,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is CryptographicException or InvalidDataException or ArgumentException)
        {
            throw new CloudRestoreFailedException();
        }

        try
        {
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

            var state = heads[0].State!;
            foreach (var head in heads.Skip(1))
            {
                state = merger.Merge(state, head.State!).State;
            }

            return new PreparedCloudVault(
                dataEncryptionKey,
                recoverySource,
                candidates,
                valid,
                heads,
                state);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
            throw;
        }
    }

    private async Task<CloudVaultMergePreview> CreateMergePreviewAsync(
        CloudVaultStateV2 cloudState,
        CancellationToken cancellationToken)
    {
        var localGroupIds = (await vaultRepository.GetGroupsAsync(cancellationToken).ConfigureAwait(false))
            .Select(group => group.Id)
            .ToHashSet(StringComparer.Ordinal);
        var localSecretIds = (await vaultRepository.GetAllSecretsAsync(cancellationToken).ConfigureAwait(false))
            .Select(secret => secret.Id)
            .ToHashSet(StringComparer.Ordinal);
        var cloudGroupIds = ActiveRecordIds(cloudState.Groups);
        var cloudSecretIds = ActiveRecordIds(cloudState.Secrets);
        return new CloudVaultMergePreview(
            localGroupIds.Except(cloudGroupIds, StringComparer.Ordinal).Count(),
            cloudGroupIds.Except(localGroupIds, StringComparer.Ordinal).Count(),
            localSecretIds.Except(cloudSecretIds, StringComparer.Ordinal).Count(),
            cloudSecretIds.Except(localSecretIds, StringComparer.Ordinal).Count(),
            localGroupIds.Intersect(cloudGroupIds, StringComparer.Ordinal).Count() +
            localSecretIds.Intersect(cloudSecretIds, StringComparer.Ordinal).Count());
    }

    private CloudVaultStateV2 ReencryptCloudSecrets(
        CloudVaultStateV2 cloudState,
        ReadOnlyMemory<byte> cloudKey,
        ReadOnlyMemory<byte> localKey) => new()
    {
        Groups = cloudState.Groups,
        Secrets = cloudState.Secrets.Select(envelope => new SyncRecordEnvelope<VaultSecret>
        {
            EntityKind = envelope.EntityKind,
            RecordId = envelope.RecordId,
            Revisions = envelope.Revisions.Select(revision =>
            {
                if (revision.Payload is null)
                {
                    return revision;
                }

                var source = revision.Payload;
                var associatedData = CreateSecretAssociatedData(
                    source.Id,
                    source.GroupId,
                    source.EncryptionVersion);
                byte[]? plaintext = null;
                try
                {
                    plaintext = encryption.Decrypt(
                        source.EncryptedPayload,
                        cloudKey.Span,
                        associatedData);
                    var document = JsonSerializer.Deserialize<SecretDocument>(plaintext, SecretJsonOptions)
                        ?? throw new InvalidDataException("A cloud secret is invalid.");
                    if (document.Fields.Count > CloudResourceLimits.MaximumFieldsPerSecret ||
                        plaintext.Length > CloudResourceLimits.MaximumSecretPlaintextBytes)
                    {
                        throw new CloudResourceLimitException("A cloud secret exceeds PassCrate's safety limits.");
                    }

                    var reencrypted = source with
                    {
                        EncryptedPayload = encryption.Encrypt(
                            plaintext,
                            localKey.Span,
                            associatedData),
                    };
                    return SyncRevisionFactory.Create(
                        SyncEntityKind.Secret,
                        envelope.RecordId,
                        revision.Vector,
                        revision.IsDeleted,
                        revision.DeletedAt,
                        reencrypted,
                        revision.IsConflict);
                }
                catch (CryptographicException)
                {
                    throw new CloudRestoreFailedException();
                }
                catch (JsonException)
                {
                    throw new CloudRestoreFailedException();
                }
                finally
                {
                    if (plaintext is not null)
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }
                }
            }).ToArray(),
        }).ToArray(),
    };

    private static CloudVaultStateV2 MergePreferLocal(
        CloudVaultStateV2 localState,
        CloudVaultStateV2 importedCloudState)
    {
        var localGroupIds = localState.Groups.Select(item => item.RecordId).ToHashSet(StringComparer.Ordinal);
        var localSecretIds = localState.Secrets.Select(item => item.RecordId).ToHashSet(StringComparer.Ordinal);
        return new CloudVaultStateV2
        {
            Groups = localState.Groups
                .Concat(importedCloudState.Groups.Where(item => !localGroupIds.Contains(item.RecordId)))
                .OrderBy(item => item.RecordId, StringComparer.Ordinal)
                .ToArray(),
            Secrets = localState.Secrets
                .Concat(importedCloudState.Secrets.Where(item => !localSecretIds.Contains(item.RecordId)))
                .OrderBy(item => item.RecordId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private async Task VerifyUploadedSnapshotAsync(
        ICloudStorageProvider storage,
        UploadResult upload,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        await using var stream = await storage.DownloadAsync(
            upload.File.ProviderFileId,
            CloudResourceLimits.MaximumSnapshotBytes,
            cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (buffer.Length <= 0 || buffer.Length > CloudResourceLimits.MaximumSnapshotBytes)
        {
            throw new InvalidDataException("The uploaded cloud backup could not be verified.");
        }

        var content = buffer.ToArray();
        try
        {
            var downloaded = snapshots.DeserializeSnapshotV2(content);
            if (!StringComparer.Ordinal.Equals(
                    downloaded.Header.SnapshotId,
                    upload.Snapshot.Header.SnapshotId) ||
                !StringComparer.Ordinal.Equals(
                    downloaded.Header.VaultId,
                    upload.Snapshot.Header.VaultId))
            {
                throw new InvalidDataException("The uploaded cloud backup did not match the expected vault.");
            }

            _ = snapshots.DecryptState(downloaded, dataEncryptionKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private async Task VerifyOrDeleteUploadedSnapshotAsync(
        ICloudStorageProvider storage,
        UploadResult upload,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            await VerifyUploadedSnapshotAsync(
                storage,
                upload,
                dataEncryptionKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await storage.DeleteAsync(upload.File.ProviderFileId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Verification failure remains authoritative; cleanup is best effort.
            }

            throw;
        }
    }

    private static async Task<bool> TryDeleteSnapshotsAsync(
        ICloudStorageProvider storage,
        IEnumerable<DownloadedSnapshot> snapshotsToDelete,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var providerFileId in snapshotsToDelete
                         .Select(item => item.File.ProviderFileId)
                         .Distinct(StringComparer.Ordinal))
            {
                await storage.DeleteAsync(providerFileId, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static void ValidateMergeRequest(CloudVaultMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CredentialPolicy.ValidateVaultPassphrase(request.LocalVaultPassphrase);
        CredentialPolicy.ValidateVaultPassphrase(request.CloudVaultPassphrase);
        if (string.IsNullOrWhiteSpace(request.CloudVaultId))
        {
            throw new UserInputValidationException("Select the cloud backup to merge.");
        }
    }

    private void EnsureUnlockedAfterAuthorization()
    {
        if (!session.IsUnlocked)
        {
            throw new CloudSetupRequiresUnlockException();
        }
    }

    private static CloudVaultContentsSummary Summarize(CloudVaultStateV2 state) => new(
        ActiveRecordIds(state.Groups).Count,
        ActiveRecordIds(state.Secrets).Count);

    private static HashSet<string> ActiveRecordIds<T>(
        IReadOnlyList<SyncRecordEnvelope<T>> envelopes) => envelopes
        .Where(envelope => SelectPrimary(envelope.Revisions) is { IsDeleted: false, Payload: not null })
        .Select(envelope => envelope.RecordId)
        .ToHashSet(StringComparer.Ordinal);

    private static SyncRecordRevision<T>? SelectPrimary<T>(
        IReadOnlyList<SyncRecordRevision<T>> revisions) => revisions
        .OrderBy(revision => revision.IsDeleted)
        .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
        .FirstOrDefault();

    private static byte[] CreateSecretAssociatedData(string id, string groupId, int version) =>
        Encoding.UTF8.GetBytes($"passcrate:secret:{version}:{id}:{groupId}");

    private async Task<UploadResult> UploadCurrentStateAsync(
        ICloudStorageProvider storage,
        CloudSyncConfiguration configuration,
        IReadOnlyList<string> parents,
        long generation,
        ReadOnlyMemory<byte> dataEncryptionKey,
        CancellationToken cancellationToken)
    {
        var state = await syncRepository.ExportStateV2Async(
            configuration.DeviceId,
            cancellationToken).ConfigureAwait(false);
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
        await VerifyOrDeleteUploadedSnapshotAsync(
            storage,
            result,
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

    private static DownloadedSnapshot SelectAuthoritativeSnapshot(
        IReadOnlyList<DownloadedSnapshot> snapshotSet) => snapshotSet
        .OrderByDescending(item => item.Snapshot.Header.Generation)
        .ThenByDescending(item => item.File.ModifiedAt)
        .ThenByDescending(item => item.Snapshot.Header.SnapshotId, StringComparer.Ordinal)
        .First();

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
            CloudVaultMismatchException =>
                (CloudSyncState.VaultMismatch, "Another PassCrate vault already has a backup in this cloud account."),
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

    private sealed class PreparedCloudVault(
        byte[] dataEncryptionKey,
        DownloadedSnapshot recoverySource,
        IReadOnlyList<DownloadedSnapshot> candidates,
        IReadOnlyList<DownloadedSnapshot> validSnapshots,
        IReadOnlyList<DownloadedSnapshot> heads,
        CloudVaultStateV2 state) : IDisposable
    {
        public byte[] DataEncryptionKey { get; } = dataEncryptionKey;
        public DownloadedSnapshot RecoverySource { get; } = recoverySource;
        public IReadOnlyList<DownloadedSnapshot> Candidates { get; } = candidates;
        public IReadOnlyList<DownloadedSnapshot> ValidSnapshots { get; } = validSnapshots;
        public IReadOnlyList<DownloadedSnapshot> Heads { get; } = heads;
        public CloudVaultStateV2 State { get; } = state;

        public void Dispose() => CryptographicOperations.ZeroMemory(DataEncryptionKey);
    }

    private sealed record DownloadedSnapshot(
        CloudFileInfo File,
        CloudVaultSnapshotV2 Snapshot,
        CloudVaultStateV2? State);

    private sealed record UploadResult(
        CloudVaultSnapshotV2 Snapshot,
        CloudFileInfo File,
        DateTimeOffset CompletedAt);
}
