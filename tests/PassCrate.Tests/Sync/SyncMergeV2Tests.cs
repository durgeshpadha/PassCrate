using System.Text.Json;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Encryption;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.Tests.Sync;

public sealed class SyncMergeV2Tests
{
    private readonly SyncMergeService _merge = new();

    [Fact]
    public void Merge_IsAssociativeCommutativeAndIdempotent()
    {
        var states = new[]
        {
            State("device-a", "Alpha"),
            State("device-b", "Bravo"),
            State("device-c", "Charlie"),
        };

        var canonicalResults = Permutations(states)
            .Select(permutation => permutation
                .Skip(1)
                .Aggregate(permutation[0], (current, next) => _merge.Merge(current, next).State))
            .Select(Canonical)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Single(canonicalResults);

        var merged = _merge.Merge(states[0], states[1]).State;
        Assert.Equal(Canonical(merged), Canonical(_merge.Merge(merged, merged).State));
        Assert.Equal(
            Canonical(_merge.Merge(states[0], states[1]).State),
            Canonical(_merge.Merge(states[1], states[0]).State));
    }

    [Fact]
    public void EqualVectorDifferentContent_RemainsAConflict()
    {
        var vector = new VersionVector().Increment("device-a");
        var first = GroupState(SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            "group",
            vector,
            false,
            null,
            Group("Alpha")));
        var second = GroupState(SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            "group",
            vector,
            false,
            null,
            Group("Bravo")));

        var result = _merge.Merge(first, second);

        Assert.Equal(2, Assert.Single(result.State.Groups).Revisions.Count);
        Assert.Single(result.Conflicts);
    }

    [Fact]
    public void NewerRevision_CausallyDominatesOlderRevision()
    {
        var olderVector = new VersionVector().Increment("device-a");
        var newerVector = olderVector.Increment("device-a");
        var older = GroupState(SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            "group",
            olderVector,
            false,
            null,
            Group("Old")));
        var newer = GroupState(SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            "group",
            newerVector,
            false,
            null,
            Group("New")));

        var result = _merge.Merge(older, newer);

        Assert.Equal("New", Assert.Single(Assert.Single(result.State.Groups).Revisions).Payload!.Name);
        Assert.Empty(result.Conflicts);
    }

    private static CloudVaultStateV2 State(string deviceId, string name)
    {
        var revision = SyncRevisionFactory.Create(
            SyncEntityKind.Group,
            "group",
            new VersionVector().Increment(deviceId),
            false,
            null,
            Group(name));
        return GroupState(revision);
    }

    private static CloudVaultStateV2 GroupState(SyncRecordRevision<VaultGroup> revision) => new()
    {
        Groups =
        [
            new SyncRecordEnvelope<VaultGroup>
            {
                EntityKind = SyncEntityKind.Group,
                RecordId = "group",
                Revisions = [revision],
            },
        ],
    };

    private static VaultGroup Group(string name) => new()
    {
        Id = "group",
        Name = name,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static string Canonical(CloudVaultStateV2 state) =>
        JsonSerializer.Serialize(state);

    private static IEnumerable<CloudVaultStateV2[]> Permutations(CloudVaultStateV2[] values)
    {
        for (var first = 0; first < values.Length; first++)
        {
            for (var second = 0; second < values.Length; second++)
            {
                if (second == first)
                {
                    continue;
                }

                var third = Enumerable.Range(0, values.Length)
                    .Single(index => index != first && index != second);
                yield return [values[first], values[second], values[third]];
            }
        }
    }
}
