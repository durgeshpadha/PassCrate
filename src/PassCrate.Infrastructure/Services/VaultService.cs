using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Services;

public sealed class VaultService(
    IVaultRepository repository,
    IEncryptionService encryption,
    IVaultSession session,
    ICloudSyncScheduler syncScheduler) : IVaultService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<IReadOnlyList<VaultGroup>> GetGroupsAsync(CancellationToken cancellationToken = default) =>
        repository.GetGroupsAsync(cancellationToken);

    public async Task<VaultGroup> CreateGroupAsync(
        string name,
        string icon,
        string color,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = DateTimeOffset.UtcNow;
        var group = new VaultGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name.Trim(),
            Icon = string.IsNullOrWhiteSpace(icon) ? "◇" : icon,
            Color = string.IsNullOrWhiteSpace(color) ? "#64748B" : color,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await repository.SaveGroupAsync(group, cancellationToken).ConfigureAwait(false);
        syncScheduler.NotifyVaultChanged();
        return group;
    }

    public async Task RenameGroupAsync(
        string groupId,
        string name,
        string icon,
        string color,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var group = await repository.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Group not found.");
        await repository.SaveGroupAsync(group with
        {
            Name = name.Trim(),
            Icon = string.IsNullOrWhiteSpace(icon) ? group.Icon : icon,
            Color = string.IsNullOrWhiteSpace(color) ? group.Color : color,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken).ConfigureAwait(false);
        syncScheduler.NotifyVaultChanged();
    }

    public async Task DeleteGroupAsync(
        string groupId,
        string? moveSecretsToGroupId,
        CancellationToken cancellationToken = default)
    {
        var secrets = await repository.GetSecretSummariesAsync(groupId, cancellationToken).ConfigureAwait(false);
        if (secrets.Count > 0 && string.IsNullOrWhiteSpace(moveSecretsToGroupId))
        {
            throw new GroupNotEmptyException();
        }

        if (secrets.Count > 0)
        {
            if (groupId == moveSecretsToGroupId ||
                await repository.GetGroupAsync(moveSecretsToGroupId!, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new ArgumentException("A valid destination group is required.", nameof(moveSecretsToGroupId));
            }

            foreach (var secret in secrets)
            {
                var document = await ReadSecretAsync(secret.Id, cancellationToken).ConfigureAwait(false);
                await UpdateSecretAsync(secret.Id, moveSecretsToGroupId!, document, cancellationToken).ConfigureAwait(false);
            }
        }

        await repository.DeleteGroupAsync(groupId, cancellationToken).ConfigureAwait(false);
        syncScheduler.NotifyVaultChanged();
    }

    public Task<IReadOnlyList<SecretSummary>> GetSecretsAsync(
        string? groupId = null,
        CancellationToken cancellationToken = default) =>
        repository.GetSecretSummariesAsync(groupId, cancellationToken);

    public Task<VaultSearchResults> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        repository.SearchAsync(query, cancellationToken);

    public async Task<SecretDocument> ReadSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        var secret = await repository.GetSecretAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Secret not found.");
        var associatedData = CreateAssociatedData(secret.Id, secret.GroupId, secret.EncryptionVersion);
        byte[]? plaintext = null;
        try
        {
            plaintext = session.UseKey(key =>
                encryption.Decrypt(secret.EncryptedPayload, key.Span, associatedData));
            return JsonSerializer.Deserialize<SecretDocument>(plaintext, JsonOptions)
                ?? throw new VaultCorruptedException();
        }
        catch (CryptographicException)
        {
            throw new VaultCorruptedException();
        }
        catch (JsonException)
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

    public async Task<string> AddSecretAsync(
        string groupId,
        SecretDocument document,
        CancellationToken cancellationToken = default)
    {
        if (await repository.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Group not found.");
        }

        ValidateDocument(document);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await SaveEncryptedSecretAsync(new VaultSecret
        {
            Id = id,
            GroupId = groupId,
            Name = document.Name.Trim(),
            SearchableFieldNames = CreateSearchableFieldNames(document),
            EncryptedPayload = null!,
            EncryptionVersion = 1,
            CreatedAt = now,
            UpdatedAt = now,
        }, document, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task UpdateSecretAsync(
        string id,
        string groupId,
        SecretDocument document,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        var existing = await repository.GetSecretAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Secret not found.");
        if (await repository.GetGroupAsync(groupId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException("Group not found.");
        }

        await SaveEncryptedSecretAsync(existing with
        {
            GroupId = groupId,
            Name = document.Name.Trim(),
            SearchableFieldNames = CreateSearchableFieldNames(document),
            UpdatedAt = DateTimeOffset.UtcNow,
        }, document, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteSecretAsync(string id, CancellationToken cancellationToken = default)
    {
        await repository.DeleteSecretAsync(id, cancellationToken).ConfigureAwait(false);
        syncScheduler.NotifyVaultChanged();
    }

    private async Task SaveEncryptedSecretAsync(
        VaultSecret secret,
        SecretDocument document,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            if (plaintext.Length > CloudResourceLimits.MaximumSecretPlaintextBytes)
            {
                throw new ArgumentException("The secret is larger than PassCrate's 1 MiB safety limit.", nameof(document));
            }

            var associatedData = CreateAssociatedData(secret.Id, secret.GroupId, secret.EncryptionVersion);
            var payload = session.UseKey(key => encryption.Encrypt(plaintext, key.Span, associatedData));
            await repository.SaveSecretAsync(secret with { EncryptedPayload = payload }, cancellationToken)
                .ConfigureAwait(false);
            syncScheduler.NotifyVaultChanged();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateDocument(SecretDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Name);
        if (document.Fields.Any(field => string.IsNullOrWhiteSpace(field.Key)))
        {
            throw new ArgumentException("Every secret field requires a name.", nameof(document));
        }

        if (document.Fields.Count > CloudResourceLimits.MaximumFieldsPerSecret)
        {
            throw new ArgumentException(
                $"A secret cannot contain more than {CloudResourceLimits.MaximumFieldsPerSecret} fields.",
                nameof(document));
        }
    }

    private static string CreateSearchableFieldNames(SecretDocument document) =>
        string.Join(' ', document.Fields.Select(field => field.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));

    private static byte[] CreateAssociatedData(string id, string groupId, int version) =>
        Encoding.UTF8.GetBytes($"passcrate:secret:{version}:{id}:{groupId}");
}
