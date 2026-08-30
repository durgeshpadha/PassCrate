using PassCrate.Core.Models;

namespace PassCrate.Tests.Sync;

public sealed class SyncRepositoryInitializationTests
{
    [Fact]
    public async Task ExportStateV2_WithNewDeviceId_DoesNotRequireSavedSyncConfiguration()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        await context.Vault.AddSecretAsync(group.Id, new SecretDocument
        {
            Name = "First sync",
            Fields = [new SecretField { Key = "Password", Value = "private", IsSensitive = true }],
        });

        Assert.Null(await context.Repository.GetConfigurationAsync());

        var state = await context.Repository.ExportStateV2Async("new-device-id");

        Assert.Single(state.Groups);
        Assert.Single(state.Secrets);
        Assert.Null(await context.Repository.GetConfigurationAsync());
    }
}
