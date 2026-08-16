using PassCrate.Core.Models;

namespace PassCrate.Core.Interfaces;

public interface IVaultRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<VaultMetadataV3?> GetVaultMetadataAsync(CancellationToken cancellationToken = default);
    Task SaveVaultMetadataAsync(VaultMetadataV3 metadata, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VaultGroup>> GetGroupsAsync(CancellationToken cancellationToken = default);
    Task<VaultGroup?> GetGroupAsync(string id, CancellationToken cancellationToken = default);
    Task SaveGroupAsync(VaultGroup group, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecretSummary>> GetSecretSummariesAsync(string? groupId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecretSummary>> SearchSecretsAsync(string query, CancellationToken cancellationToken = default);
    Task<VaultSearchResults> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VaultSecret>> GetAllSecretsAsync(CancellationToken cancellationToken = default);
    Task<VaultSecret?> GetSecretAsync(string id, CancellationToken cancellationToken = default);
    Task SaveSecretAsync(VaultSecret secret, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string id, CancellationToken cancellationToken = default);
    Task MoveSecretsAsync(string fromGroupId, string toGroupId, CancellationToken cancellationToken = default);

    Task<ApplicationSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(ApplicationSettings settings, CancellationToken cancellationToken = default);
    Task DeleteAllAsync(CancellationToken cancellationToken = default);
}

public interface IVaultService
{
    Task<IReadOnlyList<VaultGroup>> GetGroupsAsync(CancellationToken cancellationToken = default);
    Task<VaultGroup> CreateGroupAsync(string name, string icon, string color, CancellationToken cancellationToken = default);
    Task RenameGroupAsync(string groupId, string name, string icon, string color, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(string groupId, string? moveSecretsToGroupId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SecretSummary>> GetSecretsAsync(string? groupId = null, CancellationToken cancellationToken = default);
    Task<VaultSearchResults> SearchAsync(string query, CancellationToken cancellationToken = default);
    Task<SecretDocument> ReadSecretAsync(string id, CancellationToken cancellationToken = default);
    Task<string> AddSecretAsync(string groupId, SecretDocument document, CancellationToken cancellationToken = default);
    Task UpdateSecretAsync(string id, string groupId, SecretDocument document, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string id, CancellationToken cancellationToken = default);
}

public interface ISecureStorageService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveAllAsync(CancellationToken cancellationToken = default);
}

public interface IApplicationResetService
{
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public interface ISensitiveClipboardService
{
    Task CopyAsync(string sensitiveValue, TimeSpan? clearAfter = null, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IUserErrorMessageMapper
{
    string ToUserMessage(Exception exception);
}

public interface ILocalDataPathProvider
{
    string DatabasePath { get; }
    void ReapplyBackupExclusion();
}
