using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Models;

namespace PassCrate.Infrastructure.Sync;

public static class SyncRevisionFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SyncRecordRevision<T> Create<T>(
        SyncEntityKind kind,
        string recordId,
        VersionVector vector,
        bool isDeleted,
        DateTimeOffset? deletedAt,
        T? payload,
        bool isConflict = false)
    {
        var contentFingerprint = Fingerprint(payload);
        return new SyncRecordRevision<T>
        {
            RevisionId = CreateRevisionId(kind, recordId, vector, isDeleted, contentFingerprint),
            Vector = vector,
            IsDeleted = isDeleted,
            DeletedAt = deletedAt,
            IsConflict = isConflict,
            ContentFingerprint = contentFingerprint,
            Payload = payload,
        };
    }

    public static string CreateNamespaceId(string vaultId, ReadOnlySpan<byte> dataEncryptionKey)
    {
        var input = Encoding.UTF8.GetBytes($"passcrate:namespace:v2:{vaultId}");
        var hash = HMACSHA256.HashData(dataEncryptionKey, input);
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    public static VersionVector MergeAll<T>(IEnumerable<SyncRecordRevision<T>> revisions)
    {
        var merged = new VersionVector();
        foreach (var revision in revisions)
        {
            merged = merged.Merge(revision.Vector);
        }

        return merged;
    }

    private static string CreateRevisionId(
        SyncEntityKind kind,
        string recordId,
        VersionVector vector,
        bool isDeleted,
        string contentFingerprint)
    {
        var canonicalVector = string.Join(
            ',',
            vector.Entries
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}:{entry.Value}"));
        var bytes = Encoding.UTF8.GetBytes(
            $"passcrate:revision:v2:{(int)kind}:{recordId}:{canonicalVector}:{isDeleted}:{contentFingerprint}");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Fingerprint<T>(T? payload)
    {
        if (payload is null)
        {
            return Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        }

        return Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)))
            .ToLowerInvariant();
    }
}
