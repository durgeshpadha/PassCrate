using System.Text;
using PassCrate.Core.Models;

namespace PassCrate.Tests.Integration;

public sealed class StorageSecurityTests
{
    [Fact]
    public async Task PlaintextSecretValue_DoesNotAppearInDatabase()
    {
        await using var context = await TestContext.CreateAsync();
        const string canary = "MyVerySecretPassword123!";
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        await context.Vault.AddSecretAsync(group.Id, new SecretDocument
        {
            Name = "Storage canary",
            Fields = [new SecretField { Key = "Password", Value = canary, IsSensitive = true }],
            Notes = "This note is encrypted too",
        });

        byte[] bytes;
        await using (var stream = new FileStream(
            context.DatabasePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes);
        }
        var persisted = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("This note is encrypted too", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResettableRepository_RemovesAllLocalRecords()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        await context.Vault.AddSecretAsync(group.Id, new SecretDocument
        {
            Name = "Example",
            Fields = [new SecretField { Key = "Password", Value = "private", IsSensitive = true }],
        });

        context.Keys.LockVault();
        await context.Repository.DeleteAllAsync();
        Assert.Null(await context.Repository.GetVaultMetadataAsync());
        Assert.Empty(await context.Repository.GetGroupsAsync());
        Assert.Empty(await context.Repository.GetSecretSummariesAsync());
    }
}
