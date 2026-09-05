using System.Text.Json;
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

    [Fact]
    public async Task ReenableSync_PreservesHistoryAndContinuesGenerationSequence()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(OldPassphrase);
        var storage = new InMemoryCloudStorageProvider();
        var sync = CreateService(context, storage);
        await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var secondDevice = await TestContext.CreateAsync();
        var secondSync = CreateService(secondDevice, storage);
        var initial = Assert.Single(await secondSync.FindVaultsAsync(CloudProviderKind.GoogleDrive));
        await secondSync.RestoreAsync(new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = initial.VaultId,
            VaultPassphrase = OldPassphrase,
        });
        await sync.DisconnectAsync();
        await context.Vault.CreateGroupAsync("Local while disconnected", "L", "#4F46E5");
        await secondDevice.Vault.CreateGroupAsync("Remote from another phone", "R", "#10B981");
        await secondSync.SynchronizeAsync();

        await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        var descriptor = Assert.Single(await sync.FindVaultsAsync(CloudProviderKind.GoogleDrive));
        Assert.Equal(3, descriptor.SnapshotCount);
        Assert.Equal(3, descriptor.LatestGeneration);
        var groupNames = (await context.Vault.GetGroupsAsync()).Select(group => group.Name).ToArray();
        Assert.Contains("Local while disconnected", groupNames);
        Assert.Contains("Remote from another phone", groupNames);
    }

    [Fact]
    public async Task ChangePassphrase_WhenUploadVerificationFails_KeepsOldLocalPassphrase()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(OldPassphrase);
        var storage = new InMemoryCloudStorageProvider();
        var sync = CreateService(context, storage);
        await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);
        storage.CorruptUploadedDownloadAfter = 1;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            sync.ChangePassphraseAsync(OldPassphrase, NewPassphrase));

        await context.Keys.VerifyPassphraseAsync(OldPassphrase);
        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            context.Keys.VerifyPassphraseAsync(NewPassphrase));
        var descriptor = Assert.Single(await sync.FindVaultsAsync(CloudProviderKind.GoogleDrive));
        Assert.Equal(1, descriptor.SnapshotCount);
        Assert.Equal(1, descriptor.LatestGeneration);
    }

    [Fact]
    public async Task ChangePassphrase_WhenFinalCloudPublishFails_RetriesWithNewRecoveryOnNextSync()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(OldPassphrase);
        var storage = new InMemoryCloudStorageProvider();
        var sync = CreateService(context, storage);
        await sync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);
        storage.CorruptUploadedDownloadAfter = 2;

        await sync.ChangePassphraseAsync(OldPassphrase, NewPassphrase);

        await context.Keys.VerifyPassphraseAsync(NewPassphrase);
        await Assert.ThrowsAsync<VaultUnlockException>(() =>
            context.Keys.VerifyPassphraseAsync(OldPassphrase));
        Assert.True((await context.Repository.GetConfigurationAsync())!.IsRecoveryUploadPending);
        Assert.Equal(CloudSyncState.Error, sync.Status.State);

        await sync.SynchronizeAsync();

        Assert.False((await context.Repository.GetConfigurationAsync())!.IsRecoveryUploadPending);
        await using var restored = await TestContext.CreateAsync();
        var restore = CreateService(restored, storage);
        var backup = Assert.Single(await restore.FindVaultsAsync(CloudProviderKind.GoogleDrive));
        await Assert.ThrowsAsync<CloudRestoreFailedException>(() => restore.RestoreAsync(new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = backup.VaultId,
            VaultPassphrase = OldPassphrase,
        }));
        await restore.RestoreAsync(new CloudRestoreRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            VaultId = backup.VaultId,
            VaultPassphrase = NewPassphrase,
        });
    }

    [Fact]
    public async Task ReplaceVaultState_WhenTransactionFails_PreservesExistingVault()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(OldPassphrase);
        var original = await context.Vault.CreateGroupAsync("Original", "O", "#4F46E5");
        var recoveredKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(SecurityDefaults.KeySize);
        try
        {
            var metadata = await context.Keys.PrepareRestoredVaultMetadataAsync(NewPassphrase, recoveredKey);
            var imported = new VaultGroup
            {
                Id = "imported-group",
                Name = "Imported",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            var revision = SyncRevisionFactory.Create(
                SyncEntityKind.Group,
                imported.Id,
                new VersionVector().Increment("cloud-device"),
                false,
                null,
                imported);
            var duplicateEnvelope = new SyncRecordEnvelope<VaultGroup>
            {
                EntityKind = SyncEntityKind.Group,
                RecordId = imported.Id,
                Revisions = [revision],
            };
            var invalidState = new CloudVaultStateV2
            {
                Groups = [duplicateEnvelope, duplicateEnvelope],
            };

            await Assert.ThrowsAnyAsync<Exception>(() => context.Repository.ReplaceVaultStateV2Async(
                metadata,
                invalidState,
                TestConfiguration("replacement-vault")));

            Assert.Equal(original.Id, Assert.Single(await context.Repository.GetGroupsAsync()).Id);
            await context.Keys.VerifyPassphraseAsync(OldPassphrase);
            await Assert.ThrowsAsync<VaultUnlockException>(() =>
                context.Keys.VerifyPassphraseAsync(NewPassphrase));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(recoveredKey);
        }
    }

    [Theory]
    [InlineData("same notes", "same notes", "value", "value", 1, 0, false, true)]
    [InlineData("local notes", "cloud notes", "value", "value", 0, 1, false, true)]
    [InlineData("same notes", "same notes", "Value", "value", 0, 1, true, true)]
    [InlineData("local notes", "cloud notes", "value", "value", 0, 1, false, false)]
    [InlineData("local notes", "cloud notes", "value", "value", 0, 1, true, false)]
    public async Task MergeDifferentVault_MatchesNamesWithoutLosingDifferentContents(
        string localNotes, string cloudNotes, string localValue, string cloudValue, int duplicateCount, int conflictCount,
        bool chooseCloud, bool keepBoth)
    {
        var storage = new InMemoryCloudStorageProvider();
        await using var cloud = await TestContext.CreateAsync();
        await cloud.Keys.InitializeVaultAsync(OldPassphrase);
        var cloudGroup = await cloud.Vault.CreateGroupAsync("Accounts", "C", "#4F46E5");
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument
        {
            Name = "Email", Notes = cloudNotes,
            Fields = [new SecretField { Key = "Password", Value = cloudValue, IsSensitive = true }],
        });
        var cloudSync = CreateService(cloud, storage);
        await cloudSync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        var localGroup = await local.Vault.CreateGroupAsync(" accounts ", "L", "#10B981");
        await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument
        {
            Name = "email", Notes = localNotes,
            Fields = [new SecretField { Key = "Password", Value = localValue, IsSensitive = true }],
        });
        var sync = CreateService(local, storage);
        var request = new CloudVaultMergeRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            CloudVaultId = (await cloud.Repository.GetConfigurationAsync())!.VaultId,
            LocalVaultPassphrase = NewPassphrase, CloudVaultPassphrase = OldPassphrase,
        };
        var preview = await sync.PreviewMergeAsync(request);
        Assert.Equal(1, preview.CombinedGroups);
        Assert.Equal(duplicateCount, preview.DuplicateSecrets);
        Assert.Equal(conflictCount, preview.SecretsNeedingReview);
        var result = await sync.MergeDifferentVaultAsync(request);
        Assert.Equal(preview, result.Preview);
        await sync.SynchronizeAsync();
        Assert.Equal(localGroup.Id, Assert.Single(await local.Vault.GetGroupsAsync()).Id);
        var secret = Assert.Single(await local.Vault.GetSecretsAsync());
        Assert.Equal(localGroup.Id, secret.GroupId);
        var conflicts = await local.Repository.ListConflictsAsync();
        Assert.Equal(conflictCount, conflicts.Count);
        if (conflictCount > 0)
        {
            var details = await local.Repository.GetConflictDetailsAsync(SyncEntityKind.Secret, secret.Id);
            Assert.Equal(2, details.Revisions.Count);
            Assert.Contains(details.Revisions, r => r.Secret!.Notes == localNotes && r.Secret.Fields[0].Value == localValue);
            Assert.Contains(details.Revisions, r => r.Secret!.Notes == cloudNotes && r.Secret.Fields[0].Value == cloudValue);
            await local.Repository.ResolveAsync(new ConflictResolutionRequest
            {
                EntityKind = SyncEntityKind.Secret, RecordId = secret.Id,
                Resolution = keepBoth ? ConflictResolutionKind.KeepBoth : ConflictResolutionKind.KeepRevision,
                SelectedRevisionId = details.Revisions.Single(r => r.DisplayName.EndsWith(chooseCloud ? "Cloud version" : "Phone version", StringComparison.Ordinal)).RevisionId,
            }, "review-device");
            Assert.Equal(keepBoth ? 2 : 1, (await local.Vault.GetSecretsAsync()).Count);
            if (keepBoth)
            {
                var duplicate = Assert.Single(await local.Vault.GetSecretsAsync(), s => s.Name.EndsWith(" (duplicate)", StringComparison.Ordinal));
                var doc = await local.Vault.ReadSecretAsync(duplicate.Id);
                Assert.Equal(cloudNotes, doc.Notes);
                Assert.Equal(cloudValue, doc.Fields[0].Value);
                Assert.Equal(duplicate.Name, doc.Name);
            }
            else
            {
                var only = Assert.Single(await local.Vault.GetSecretsAsync());
                Assert.DoesNotContain("(duplicate)", only.Name);
                Assert.Equal(chooseCloud ? cloudNotes : localNotes, (await local.Vault.ReadSecretAsync(only.Id)).Notes);
            }
            foreach (var saved in await local.Vault.GetSecretsAsync())
                Assert.NotNull(await local.Vault.ReadSecretAsync(saved.Id));
        }
        else
            Assert.Equal(localNotes, (await local.Vault.ReadSecretAsync(secret.Id)).Notes);
    }

    [Fact]
    public async Task MergeDifferentVault_MultipleCloudMatchesAreLabeledAndKeptWithUniqueNames()
    {
        var storage = new InMemoryCloudStorageProvider();
        await using var cloud = await TestContext.CreateAsync();
        await cloud.Keys.InitializeVaultAsync(OldPassphrase);
        var cloudGroup = await cloud.Vault.CreateGroupAsync("Accounts", "C", "#4F46E5");
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument
        {
            Name = "Email", Notes = "cloud one",
            Fields = [new SecretField { Key = "Password", Value = "one", IsSensitive = true }],
        });
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument
        {
            Name = "email", Notes = "cloud two",
            Fields = [new SecretField { Key = "Password", Value = "two", IsSensitive = true }],
        });
        var cloudSync = CreateService(cloud, storage);
        await cloudSync.EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        var localGroup = await local.Vault.CreateGroupAsync("accounts", "L", "#10B981");
        var localSecretId = await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument
        {
            Name = "EMAIL", Notes = "phone",
            Fields = [new SecretField { Key = "Password", Value = "phone", IsSensitive = true }],
        });
        await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument
        {
            Name = "Email (duplicate)", Notes = "existing duplicate",
            Fields = [],
        });

        var sync = CreateService(local, storage);
        var request = new CloudVaultMergeRequest
        {
            Provider = CloudProviderKind.GoogleDrive,
            CloudVaultId = (await cloud.Repository.GetConfigurationAsync())!.VaultId,
            LocalVaultPassphrase = NewPassphrase,
            CloudVaultPassphrase = OldPassphrase,
        };
        var result = await sync.MergeDifferentVaultAsync(request);

        Assert.Equal(1, result.Preview.CombinedGroups);
        Assert.Equal(1, result.Preview.SecretsNeedingReview);
        var details = await local.Repository.GetConflictDetailsAsync(SyncEntityKind.Secret, localSecretId);
        Assert.Equal(3, details.Revisions.Count);
        Assert.Single(details.Revisions, item => item.DisplayName.EndsWith("Phone version", StringComparison.Ordinal));
        Assert.Equal(2, details.Revisions.Count(item => item.DisplayName.EndsWith("Cloud version", StringComparison.Ordinal)));

        await local.Repository.ResolveAsync(new ConflictResolutionRequest
        {
            EntityKind = SyncEntityKind.Secret,
            RecordId = localSecretId,
            Resolution = ConflictResolutionKind.KeepBoth,
            SelectedRevisionId = details.Revisions.Single(item =>
                item.DisplayName.EndsWith("Phone version", StringComparison.Ordinal)).RevisionId,
        }, "review-device");

        var names = (await local.Vault.GetSecretsAsync()).Select(item => item.Name).ToArray();
        Assert.Contains("Email (duplicate)", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Email (duplicate 2)", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("email (duplicate 3)", names, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var exported = await local.Repository.ExportStateV2Async("compatibility-device");
        var serialized = JsonSerializer.Serialize(exported);
        Assert.DoesNotContain("isCloudMergeVersion", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeDifferentVault_PreservesCloudGroupWhenPhoneGroupNameIsAmbiguous()
    {
        var storage = new InMemoryCloudStorageProvider();
        await using var cloud = await TestContext.CreateAsync();
        await cloud.Keys.InitializeVaultAsync(OldPassphrase);
        var cloudGroup = await cloud.Vault.CreateGroupAsync("Accounts", "C", "#4F46E5");
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument { Name = "Cloud only", Fields = [] });
        await CreateService(cloud, storage).EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        await local.Vault.CreateGroupAsync("accounts", "L", "#10B981");
        await local.Vault.CreateGroupAsync(" Accounts ", "L", "#10B981");
        var sync = CreateService(local, storage);
        var request = await CreateMergeRequestAsync(cloud, CloudProviderKind.GoogleDrive);

        var preview = await sync.PreviewMergeAsync(request);
        Assert.Equal(1, preview.AmbiguousGroupsPreserved);
        await sync.MergeDifferentVaultAsync(request);

        Assert.Equal(3, (await local.Vault.GetGroupsAsync()).Count);
        var secret = Assert.Single(await local.Vault.GetSecretsAsync());
        Assert.Equal(cloudGroup.Id, secret.GroupId);
    }

    [Fact]
    public async Task MergeDifferentVault_PreservesCloudSecretWhenPhoneSecretNameIsAmbiguous()
    {
        var storage = new InMemoryCloudStorageProvider();
        await using var cloud = await TestContext.CreateAsync();
        await cloud.Keys.InitializeVaultAsync(OldPassphrase);
        var cloudGroup = await cloud.Vault.CreateGroupAsync("Accounts", "C", "#4F46E5");
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument { Name = "Email", Notes = "cloud", Fields = [] });
        await CreateService(cloud, storage).EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        var localGroup = await local.Vault.CreateGroupAsync("accounts", "L", "#10B981");
        await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument { Name = "email", Notes = "phone one", Fields = [] });
        await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument { Name = " EMAIL ", Notes = "phone two", Fields = [] });
        var sync = CreateService(local, storage);
        var request = await CreateMergeRequestAsync(cloud, CloudProviderKind.GoogleDrive);

        var preview = await sync.PreviewMergeAsync(request);
        Assert.Equal(1, preview.CombinedGroups);
        Assert.Equal(1, preview.AmbiguousSecretsPreserved);
        Assert.Equal(0, preview.SecretsNeedingReview);
        await sync.MergeDifferentVaultAsync(request);

        Assert.Equal(3, (await local.Vault.GetSecretsAsync()).Count);
        Assert.Empty(await local.Repository.ListConflictsAsync());
    }

    [Fact]
    public async Task MergeDifferentVault_RetrySkipsAnAlreadyImportedConflictRevision()
    {
        var storage = new InMemoryCloudStorageProvider { FailDeletes = true };
        await using var cloud = await TestContext.CreateAsync();
        await cloud.Keys.InitializeVaultAsync(OldPassphrase);
        var cloudGroup = await cloud.Vault.CreateGroupAsync("Accounts", "C", "#4F46E5");
        await cloud.Vault.AddSecretAsync(cloudGroup.Id, new SecretDocument { Name = "Email", Notes = "cloud", Fields = [] });
        await CreateService(cloud, storage).EnableAsync(CloudProviderKind.GoogleDrive, OldPassphrase);

        await using var local = await TestContext.CreateAsync();
        await local.Keys.InitializeVaultAsync(NewPassphrase);
        var localGroup = await local.Vault.CreateGroupAsync("accounts", "L", "#10B981");
        var localSecret = await local.Vault.AddSecretAsync(localGroup.Id, new SecretDocument { Name = "Email", Notes = "phone", Fields = [] });
        var sync = CreateService(local, storage);
        var request = await CreateMergeRequestAsync(cloud, CloudProviderKind.GoogleDrive);

        await sync.MergeDifferentVaultAsync(request);
        var first = await local.Repository.GetConflictDetailsAsync(SyncEntityKind.Secret, localSecret);
        Assert.Equal(2, first.Revisions.Count);
        await Assert.ThrowsAsync<UserInputValidationException>(() => local.Vault.UpdateSecretAsync(
            localSecret,
            localGroup.Id,
            new SecretDocument { Name = "Email", Notes = "edited", Fields = [] }));
        await Assert.ThrowsAsync<UserInputValidationException>(() => local.Vault.DeleteSecretAsync(localSecret));

        var retryPreview = await sync.PreviewMergeAsync(request);
        Assert.Equal(1, retryPreview.DuplicateSecrets);
        await sync.MergeDifferentVaultAsync(request);
        var retry = await local.Repository.GetConflictDetailsAsync(SyncEntityKind.Secret, localSecret);
        Assert.Equal(2, retry.Revisions.Count);
        await local.Repository.ResolveAsync(new ConflictResolutionRequest
        {
            EntityKind = SyncEntityKind.Secret,
            RecordId = localSecret,
            Resolution = ConflictResolutionKind.KeepRevision,
            SelectedRevisionId = retry.Revisions.Single(item =>
                item.DisplayName.EndsWith("Phone version", StringComparison.Ordinal)).RevisionId,
        }, "review-device");
        Assert.False(await local.Repository.HasConflictAsync(SyncEntityKind.Secret, localSecret));
        await local.Vault.UpdateSecretAsync(
            localSecret,
            localGroup.Id,
            new SecretDocument { Name = "Email", Notes = "resolved", Fields = [] });
    }

    private static async Task<CloudVaultMergeRequest> CreateMergeRequestAsync(
        TestContext cloud,
        CloudProviderKind provider) => new()
        {
            Provider = provider,
            CloudVaultId = (await cloud.Repository.GetConfigurationAsync())!.VaultId,
            LocalVaultPassphrase = NewPassphrase,
            CloudVaultPassphrase = OldPassphrase,
        };

    private static CloudSyncConfiguration TestConfiguration(string vaultId) => new()
    {
        IsEnabled = true,
        Provider = CloudProviderKind.GoogleDrive,
        VaultId = vaultId,
        DeviceId = "replacement-device",
        Recovery = new RecoveryKeyEnvelope
        {
            RecoveryKdf = SecurityDefaults.CreatePbkdf2Parameters(),
            WrappedDataEncryptionKey = new EncryptedPayload
            {
                Nonce = new byte[SecurityDefaults.GcmNonceSize],
                Ciphertext = new byte[SecurityDefaults.KeySize],
                AuthenticationTag = new byte[SecurityDefaults.GcmTagSize],
            },
        },
    };

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
        private string? _corruptDownloadFileId;

        public int CorruptUploadedDownloadAfter { get; set; }
        public bool FailDeletes { get; set; }

        public CloudProviderKind Kind => CloudProviderKind.GoogleDrive;

        public Task<IReadOnlyList<CloudFileInfo>> ListAsync(
            int maximumFiles = CloudResourceLimits.MaximumFilesPerOperation,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudFileInfo>>(
            _files.Values.Select(item => item.Info).Take(maximumFiles).ToArray());

        public Task<Stream> DownloadAsync(
            string providerFileId,
            long maximumBytes = CloudResourceLimits.MaximumSnapshotBytes,
            CancellationToken cancellationToken = default)
        {
            if (StringComparer.Ordinal.Equals(providerFileId, _corruptDownloadFileId))
            {
                _corruptDownloadFileId = null;
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3], writable: false));
            }

            return Task.FromResult<Stream>(
                new MemoryStream(_files[providerFileId].Content.ToArray(), writable: false));
        }

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
            if (CorruptUploadedDownloadAfter > 0 && --CorruptUploadedDownloadAfter == 0)
            {
                _corruptDownloadFileId = id;
            }
            return Task.FromResult(info);
        }

        public Task DeleteAsync(string providerFileId, CancellationToken cancellationToken = default)
        {
            if (FailDeletes) throw new IOException("Delete failed for test.");
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
