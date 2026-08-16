using System.Security.Cryptography;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.Infrastructure.Sync;

public sealed class SyncMergeService : ISyncMergeService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public CloudMergeResultV2 Merge(CloudVaultStateV2 left, CloudVaultStateV2 right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var groups = MergeEnvelopes(
            left.Groups,
            right.Groups,
            SyncEntityKind.Group).ToDictionary(item => item.RecordId, StringComparer.Ordinal);
        var secrets = MergeEnvelopes(
            left.Secrets,
            right.Secrets,
            SyncEntityKind.Secret).ToDictionary(item => item.RecordId, StringComparer.Ordinal);

        // A referenced concurrently deleted group remains materializable from its
        // presentation-only deletion payload until its dependent secrets are resolved.
        foreach (var secretEnvelope in secrets.Values)
        {
            var secret = SelectPrimary(secretEnvelope.Revisions);
            if (secret is null || secret.IsDeleted || secret.Payload is null)
            {
                continue;
            }

            if (!groups.TryGetValue(secret.Payload.GroupId, out var groupEnvelope))
            {
                throw new InvalidDataException("An active secret references a missing group.");
            }

            if (groupEnvelope.Revisions.Any(revision => !revision.IsDeleted && revision.Payload is not null))
            {
                continue;
            }

            if (groupEnvelope.Revisions.All(revision => revision.Payload is null))
            {
                throw new InvalidDataException("A group deletion lost required presentation metadata.");
            }

            groups[groupEnvelope.RecordId] = groupEnvelope with
            {
                Revisions = groupEnvelope.Revisions
                    .Select(revision => revision with { IsConflict = true })
                    .OrderBy(revision => revision.RevisionId, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        var conflicts = groups.Values
            .Select(ToConflictSet)
            .Concat(secrets.Values.Select(ToConflictSet))
            .Where(conflict => conflict is not null)
            .Select(conflict => conflict!)
            .OrderBy(conflict => conflict.EntityKind)
            .ThenBy(conflict => conflict.RecordId, StringComparer.Ordinal)
            .ToArray();
        return new CloudMergeResultV2(
            new CloudVaultStateV2
            {
                Groups = groups.Values.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
                Secrets = secrets.Values.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
            },
            conflicts);
    }

    private static IReadOnlyList<SyncRecordEnvelope<T>> MergeEnvelopes<T>(
        IReadOnlyList<SyncRecordEnvelope<T>> left,
        IReadOnlyList<SyncRecordEnvelope<T>> right,
        SyncEntityKind expectedKind)
    {
        return left.Concat(right)
            .GroupBy(envelope => envelope.RecordId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var revisions = new Dictionary<string, SyncRecordRevision<T>>(StringComparer.Ordinal);
                foreach (var envelope in group)
                {
                    if (envelope.EntityKind != expectedKind || string.IsNullOrWhiteSpace(envelope.RecordId))
                    {
                        throw new InvalidDataException("The sync record envelope is invalid.");
                    }

                    foreach (var revision in envelope.Revisions)
                    {
                        ValidateRevision(revision);
                        if (revisions.TryGetValue(revision.RevisionId, out var duplicate))
                        {
                            if (!RevisionEquivalent(duplicate, revision))
                            {
                                throw new InvalidDataException("A revision identifier maps to different content.");
                            }

                            continue;
                        }

                        revisions.Add(revision.RevisionId, revision);
                    }
                }

                var candidates = revisions.Values.ToArray();
                var nonDominated = candidates
                    .Where(candidate => !candidates.Any(other =>
                        !ReferenceEquals(candidate, other) &&
                        candidate.Vector.Compare(other.Vector) == VectorComparison.Before))
                    .ToArray();
                var survivors = nonDominated
                    .Select(candidate => candidate with
                    {
                        IsConflict = nonDominated.Length > 1,
                    })
                    .OrderBy(candidate => candidate.RevisionId, StringComparer.Ordinal)
                    .ToArray();
                return new SyncRecordEnvelope<T>
                {
                    EntityKind = expectedKind,
                    RecordId = group.Key,
                    Revisions = survivors,
                };
            })
            .ToArray();
    }

    private static void ValidateRevision<T>(SyncRecordRevision<T> revision)
    {
        if (string.IsNullOrWhiteSpace(revision.RevisionId) ||
            revision.Vector.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Value < 0) ||
            (!revision.IsDeleted && revision.Payload is null))
        {
            throw new InvalidDataException("The sync revision is invalid.");
        }
    }

    private static bool RevisionEquivalent<T>(
        SyncRecordRevision<T> left,
        SyncRecordRevision<T> right) =>
        left.RevisionId == right.RevisionId &&
        left.Vector.Compare(right.Vector) == VectorComparison.Equal &&
        left.IsDeleted == right.IsDeleted &&
        left.DeletedAt == right.DeletedAt &&
        left.ContentFingerprint == right.ContentFingerprint &&
        Fingerprint(left.Payload) == Fingerprint(right.Payload);

    private static string Fingerprint<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

    private static SyncRecordRevision<T>? SelectPrimary<T>(IReadOnlyList<SyncRecordRevision<T>> revisions) =>
        revisions
            .OrderBy(revision => revision.IsDeleted)
            .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
            .FirstOrDefault();

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
            RevisionIds = envelope.Revisions.Select(revision => revision.RevisionId).ToArray(),
            IncludesDeletion = envelope.Revisions.Any(revision => revision.IsDeleted),
        };
    }
}
