using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Tests.Integration;

public sealed class VaultServiceTests
{
    [Fact]
    public async Task SecretLifecycle_RoundTripsEncryptedDocument()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        var document = CreateDocument("MyVerySecretPassword123!");

        var id = await context.Vault.AddSecretAsync(group.Id, document);
        AssertDocumentEqual(document, await context.Vault.ReadSecretAsync(id));

        var updated = CreateDocument("ACompletelyNewSecret456!") with { Name = "Updated account" };
        await context.Vault.UpdateSecretAsync(id, group.Id, updated);
        AssertDocumentEqual(updated, await context.Vault.ReadSecretAsync(id));

        await context.Vault.DeleteSecretAsync(id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => context.Vault.ReadSecretAsync(id));
    }

    [Fact]
    public async Task LockedVault_DoesNotDecryptSecrets()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        var id = await context.Vault.AddSecretAsync(group.Id, CreateDocument("secret"));

        context.Keys.LockVault();
        await Assert.ThrowsAsync<VaultLockedException>(() => context.Vault.ReadSecretAsync(id));
    }

    [Fact]
    public async Task Search_UsesNamesAndFieldLabels_NotValues()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var group = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        await context.Vault.AddSecretAsync(group.Id, CreateDocument("UnsearchableSensitiveValue"));

        Assert.Single((await context.Vault.SearchAsync("GitHub")).Secrets);
        var groupResults = await context.Vault.SearchAsync("Password");
        Assert.Single(groupResults.Secrets);
        Assert.Single(groupResults.Groups);
        Assert.Empty((await context.Vault.SearchAsync("UnsearchableSensitiveValue")).Secrets);
    }

    [Fact]
    public async Task DeletingNonEmptyGroup_RequiresDestinationAndReencryptsMovedSecrets()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync("correct horse battery staple 2026");
        var source = await context.Vault.CreateGroupAsync("Passwords", "✦", "#4F46E5");
        var destination = await context.Vault.CreateGroupAsync("Other", "◇", "#64748B");
        var id = await context.Vault.AddSecretAsync(source.Id, CreateDocument("secret"));

        await Assert.ThrowsAsync<GroupNotEmptyException>(() => context.Vault.DeleteGroupAsync(source.Id, null));
        await context.Vault.DeleteGroupAsync(source.Id, destination.Id);

        var moved = Assert.Single(await context.Vault.GetSecretsAsync(destination.Id));
        Assert.Equal(id, moved.Id);
        Assert.Equal("GitHub Account", (await context.Vault.ReadSecretAsync(id)).Name);
    }

    private static SecretDocument CreateDocument(string password) => new()
    {
        Name = "GitHub Account",
        Fields =
        [
            new SecretField { Key = "Username", Value = "john@example.test" },
            new SecretField { Key = "Password", Value = password, IsSensitive = true },
        ],
        Notes = "Private test record",
    };

    private static void AssertDocumentEqual(SecretDocument expected, SecretDocument actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Notes, actual.Notes);
        Assert.Equal(expected.Fields.ToArray(), actual.Fields.ToArray());
    }
}
