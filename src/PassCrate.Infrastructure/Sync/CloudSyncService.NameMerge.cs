using System.Security.Cryptography;
using System.Text.Json;
using PassCrate.Core.Models;

namespace PassCrate.Infrastructure.Sync;

public sealed partial class CloudSyncService
{
    private (CloudVaultStateV2 State, CloudVaultMergePreview Preview) CombineByIdentityAndName(
        CloudVaultStateV2 local, CloudVaultStateV2 cloud, ReadOnlyMemory<byte> key)
    {
        var groups = local.Groups.ToDictionary(item => item.RecordId, StringComparer.Ordinal);
        var secrets = local.Secrets.ToDictionary(item => item.RecordId, StringComparer.Ordinal);
        var localSecretIds = local.Secrets.Select(item => item.RecordId).ToHashSet(StringComparer.Ordinal);
        var groupsByName = IndexGroups(local.Groups);
        var secretsByGroupAndName = IndexSecrets(local.Secrets);
        var groupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var semanticDigests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var combinedGroups = 0;
        var duplicates = 0;
        var review = new HashSet<string>(StringComparer.Ordinal);
        var idCollisions = 0;
        var addedGroups = 0;
        var addedSecrets = 0;
        var ambiguousGroups = 0;
        var ambiguousSecrets = 0;
        var importDevice = $"merge-cloud-{Guid.NewGuid():N}";

        try
        {
        foreach (var envelope in cloud.Groups.OrderBy(item => item.RecordId, StringComparer.Ordinal))
        {
            if (groups.ContainsKey(envelope.RecordId))
            {
                groupMap[envelope.RecordId] = envelope.RecordId;
                idCollisions++;
                continue;
            }

            var source = SelectPrimary(envelope.Revisions);
            var matches = source is { IsDeleted: false, Payload: not null } && envelope.Revisions.Count == 1 &&
                          groupsByName.TryGetValue(NormalizeName(source.Payload.Name), out var candidates)
                ? candidates
                : [];
            if (matches.Count == 1)
            {
                groupMap[envelope.RecordId] = matches[0];
                combinedGroups++;
            }
            else
            {
                groupMap[envelope.RecordId] = envelope.RecordId;
                groups.Add(envelope.RecordId, envelope);
                if (source is { IsDeleted: false, Payload: not null })
                {
                    addedGroups++;
                    if (matches.Count > 1) ambiguousGroups++;
                }
            }
        }

        foreach (var envelope in cloud.Secrets.OrderBy(item => item.RecordId, StringComparer.Ordinal))
        {
            if (localSecretIds.Contains(envelope.RecordId))
            {
                idCollisions++;
                continue;
            }

            var mapped = RemapSecret(envelope, envelope.RecordId, groupMap, key);
            var source = SelectPrimary(mapped.Revisions);
            // Keep cloud-side conflict/deletion history as an independent record.
            // Name matching is safe only for a single, active cloud revision.
            if (source is not { IsDeleted: false, Payload: not null } || mapped.Revisions.Count != 1)
            {
                AddPreservedSecret(mapped, source, secrets, ref addedSecrets);
                continue;
            }

            var secretKey = new SecretNameKey(source.Payload.GroupId, NormalizeName(source.Payload.Name));
            var matches = secretsByGroupAndName.TryGetValue(secretKey, out var candidates)
                ? candidates
                : [];
            if (matches.Count != 1)
            {
                AddPreservedSecret(mapped, source, secrets, ref addedSecrets);
                if (matches.Count > 1) ambiguousSecrets++;
                continue;
            }

            var existing = secrets[matches[0]];
            if (existing.Revisions
                .Where(revision => !revision.IsDeleted && revision.Payload is not null)
                .Any(revision => SameSecretContents(source, revision, key, semanticDigests)))
            {
                duplicates++;
                continue;
            }

            var remapped = RemapSecret(mapped, existing.RecordId, new Dictionary<string, string>(), key);
            var imported = remapped.Revisions.Single();
            var revision = SyncRevisionFactory.Create(
                SyncEntityKind.Secret,
                existing.RecordId,
                new VersionVector().Increment(importDevice),
                imported.IsDeleted,
                imported.DeletedAt,
                imported.Payload,
                isConflict: true);
            secrets[existing.RecordId] = existing with
            {
                Revisions = existing.Revisions
                    .Select(item => item with { IsConflict = true })
                    .Append(revision)
                    .ToArray(),
            };
            review.Add(existing.RecordId);
        }

        return (new CloudVaultStateV2
        {
            Groups = groups.Values.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
            Secrets = secrets.Values.OrderBy(item => item.RecordId, StringComparer.Ordinal).ToArray(),
        }, new CloudVaultMergePreview(
            ActiveRecordIds(local.Groups).Count, addedGroups,
            ActiveRecordIds(local.Secrets).Count, addedSecrets, idCollisions)
        {
            CombinedGroups = combinedGroups,
            DuplicateSecrets = duplicates,
            SecretsNeedingReview = review.Count,
            AmbiguousGroupsPreserved = ambiguousGroups,
            AmbiguousSecretsPreserved = ambiguousSecrets,
        });
        }
        finally
        {
            foreach (var digest in semanticDigests.Values)
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
    }

    private static Dictionary<string, List<string>> IndexGroups(
        IEnumerable<SyncRecordEnvelope<VaultGroup>> groups) =>
        groups.Where(envelope => envelope.Revisions.Count == 1 &&
                SelectPrimary(envelope.Revisions) is { IsDeleted: false, Payload: not null })
            .GroupBy(envelope => NormalizeName(SelectPrimary(envelope.Revisions)!.Payload!.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(envelope => envelope.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);

    private static Dictionary<SecretNameKey, List<string>> IndexSecrets(
        IEnumerable<SyncRecordEnvelope<VaultSecret>> secrets) =>
        secrets.Where(envelope => SelectPrimary(envelope.Revisions) is { IsDeleted: false, Payload: not null })
            .GroupBy(envelope =>
            {
                var payload = SelectPrimary(envelope.Revisions)!.Payload!;
                return new SecretNameKey(payload.GroupId, NormalizeName(payload.Name));
            })
            .ToDictionary(group => group.Key,
                group => group.Select(envelope => envelope.RecordId).OrderBy(id => id, StringComparer.Ordinal).ToList());

    private static void AddPreservedSecret(
        SyncRecordEnvelope<VaultSecret> envelope,
        SyncRecordRevision<VaultSecret>? primary,
        IDictionary<string, SyncRecordEnvelope<VaultSecret>> secrets,
        ref int addedSecrets)
    {
        secrets.Add(envelope.RecordId, envelope);
        if (primary is { IsDeleted: false, Payload: not null }) addedSecrets++;
    }

    private static string NormalizeName(string name) => name.Trim().ToUpperInvariant();

    private bool SameSecretContents(
        SyncRecordRevision<VaultSecret> left,
        SyncRecordRevision<VaultSecret> right,
        ReadOnlyMemory<byte> key,
        IDictionary<string, byte[]> cache) =>
        CryptographicOperations.FixedTimeEquals(
            GetSecretSemanticDigest(left, key, cache),
            GetSecretSemanticDigest(right, key, cache));

    private byte[] GetSecretSemanticDigest(
        SyncRecordRevision<VaultSecret> revision,
        ReadOnlyMemory<byte> key,
        IDictionary<string, byte[]> cache)
    {
        if (cache.TryGetValue(revision.RevisionId, out var cached)) return cached;
        var secret = revision.Payload ?? throw new InvalidDataException("An active secret revision has no payload.");
        var plaintext = encryption.Decrypt(secret.EncryptedPayload, key.Span,
            CreateSecretAssociatedData(secret.Id, secret.GroupId, secret.EncryptionVersion));
        try
        {
            var document = JsonSerializer.Deserialize<SecretDocument>(plaintext, SecretJsonOptions)
                ?? throw new InvalidDataException("Invalid secret document.");
            var canonical = JsonSerializer.SerializeToUtf8Bytes(new SecretComparisonDocument(
                NormalizeName(document.Name),
                document.Notes,
                document.Fields
                    .OrderBy(field => field.Key, StringComparer.Ordinal)
                    .ThenBy(field => field.Value, StringComparer.Ordinal)
                    .ThenBy(field => field.IsSensitive)
                    .ToArray()), SecretJsonOptions);
            try
            {
                var digest = HMACSHA256.HashData(key.Span, canonical);
                cache.Add(revision.RevisionId, digest);
                return digest;
            }
            finally { CryptographicOperations.ZeroMemory(canonical); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private SyncRecordEnvelope<VaultSecret> RemapSecret(SyncRecordEnvelope<VaultSecret> envelope,
        string id, IReadOnlyDictionary<string, string> groupMap, ReadOnlyMemory<byte> key) => envelope with
    {
        RecordId = id,
        Revisions = envelope.Revisions.Select(revision =>
        {
            var payload = revision.Payload;
            if (payload is not null)
            {
                var groupId = groupMap.TryGetValue(payload.GroupId, out var mapped) ? mapped : payload.GroupId;
                if (id != payload.Id || groupId != payload.GroupId)
                {
                    var bytes = encryption.Decrypt(payload.EncryptedPayload, key.Span,
                        CreateSecretAssociatedData(payload.Id, payload.GroupId, payload.EncryptionVersion));
                    try
                    {
                        payload = payload with
                        {
                            Id = id, GroupId = groupId,
                            EncryptedPayload = encryption.Encrypt(bytes, key.Span,
                                CreateSecretAssociatedData(id, groupId, payload.EncryptionVersion)),
                        };
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            }
            return SyncRevisionFactory.Create(SyncEntityKind.Secret, id, revision.Vector,
                revision.IsDeleted, revision.DeletedAt, payload, revision.IsConflict);
        }).ToArray(),
    };

    private readonly record struct SecretNameKey(string GroupId, string Name);
    private sealed record SecretComparisonDocument(string Name, string Notes, IReadOnlyList<SecretField> Fields);
}
