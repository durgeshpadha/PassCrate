using PassCrate.Core.Models;
using PassCrate.Core.Security;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Sync;

public sealed class ConflictResolutionTests
{
    [Fact]
    public async Task GroupDeletionConflict_IsNamedAndAcceptDeletionRemovesGroup()
    {
        await using var context = await TestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var group = new VaultGroup
        {
            Id = "group-conflict",
            Name = "Passwords",
            Icon = "🔑",
            Color = "#4F46E5",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddHours(-1),
        };
        var activeRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            group.Id,
            new VersionVector().Increment("phone-a"),
            false,
            null,
            group,
            isConflict: true);
        var deletedRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            group.Id,
            new VersionVector().Increment("phone-b"),
            true,
            now,
            group,
            isConflict: true);

        await context.Repository.ApplyMergedStateV2Async(
            GroupState(group.Id, activeRevision, deletedRevision),
            Configuration());

        var conflict = Assert.Single(await context.Repository.ListConflictsAsync());
        Assert.Equal("Passwords", conflict.DisplayName);
        Assert.True(conflict.IncludesDeletion);

        var details = await context.Repository.GetConflictDetailsAsync(SyncEntityKind.Group, group.Id);
        Assert.All(details.Revisions, revision => Assert.NotNull(revision.ChangedAt));

        await context.Repository.ResolveAsync(new ConflictResolutionRequest
        {
            EntityKind = SyncEntityKind.Group,
            RecordId = group.Id,
            Resolution = ConflictResolutionKind.AcceptDeletion,
            SelectedRevisionId = activeRevision.RevisionId,
        }, "resolving-phone");

        Assert.Null(await context.Repository.GetGroupAsync(group.Id));
        Assert.Empty(await context.Repository.ListConflictsAsync());
    }

    [Fact]
    public async Task GroupConflict_RejectsSecretOnlyKeepBothAction()
    {
        await using var context = await TestContext.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        var first = new VaultGroup
        {
            Id = "group-conflict",
            Name = "Personal",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddMinutes(-2),
        };
        var second = first with { Name = "Private", UpdatedAt = now.AddMinutes(-1) };
        var firstRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            first.Id,
            new VersionVector().Increment("phone-a"),
            false,
            null,
            first,
            isConflict: true);
        var secondRevision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            second.Id,
            new VersionVector().Increment("phone-b"),
            false,
            null,
            second,
            isConflict: true);

        await context.Repository.ApplyMergedStateV2Async(
            GroupState(first.Id, firstRevision, secondRevision),
            Configuration());

        var exception = await Assert.ThrowsAsync<UserInputValidationException>(() =>
            context.Repository.ResolveAsync(new ConflictResolutionRequest
            {
                EntityKind = SyncEntityKind.Group,
                RecordId = first.Id,
                Resolution = ConflictResolutionKind.KeepBoth,
                SelectedRevisionId = firstRevision.RevisionId,
            }, "resolving-phone"));

        Assert.Contains("secrets only", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await context.Repository.ListConflictsAsync());
    }

    private static CloudVaultStateV2 GroupState(
        string groupId,
        params SyncRecordRevision<VaultGroup>[] revisions) => new()
    {
        Groups =
        [
            new SyncRecordEnvelope<VaultGroup>
            {
                EntityKind = SyncEntityKind.Group,
                RecordId = groupId,
                Revisions = revisions,
            },
        ],
    };

    private static CloudSyncConfiguration Configuration() => new()
    {
        IsEnabled = true,
        Provider = CloudProviderKind.GoogleDrive,
        VaultId = "test-vault",
        DeviceId = "local-device",
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
}
