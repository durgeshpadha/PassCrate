using PassCrate.Core.Security;

namespace PassCrate.Tests.Security;

public sealed class VaultPassphraseTests
{
    private const string Passphrase = "correct horse battery staple 2026";

    [Fact]
    public async Task PassphraseCreatesLocksUnlocksAndChangesVault()
    {
        await using var context = await TestContext.CreateAsync();
        await context.Keys.InitializeVaultAsync(Passphrase);
        Assert.True(context.Keys.IsUnlocked);
        context.Keys.LockVault();
        await Assert.ThrowsAsync<VaultUnlockException>(() => context.Keys.UnlockVaultAsync("wrong passphrase that is long enough"));
        await context.Keys.UnlockVaultAsync(Passphrase);
        await context.Keys.ChangePassphraseAsync(Passphrase, "new correct horse battery staple 2026");
        context.Keys.LockVault();
        await Assert.ThrowsAsync<VaultUnlockException>(() => context.Keys.UnlockVaultAsync(Passphrase));
        await context.Keys.UnlockVaultAsync("new correct horse battery staple 2026");
    }

    [Fact]
    public void VaultPassphrasePolicyRequiresStrength()
    {
        var missing = Assert.Throws<CredentialValidationException>(
            () => CredentialPolicy.ValidateVaultPassphrase(string.Empty));
        Assert.Equal("Enter your vault passphrase.", missing.Message);

        var shortPassphrase = Assert.Throws<CredentialValidationException>(
            () => CredentialPolicy.ValidateVaultPassphrase("short"));
        Assert.Contains("at least 16 characters", shortPassphrase.Message);
        CredentialPolicy.ValidateVaultPassphrase(Passphrase);
    }
}
