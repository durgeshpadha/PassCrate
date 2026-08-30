using PassCrate.Core.Security;

namespace PassCrate.Tests.Security;

public sealed class InstallationSecurityPolicyTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void Cleanup_only_runs_for_an_uninitialized_install_without_a_vault(
        bool cleanupCompleted,
        bool hasLocalVault,
        bool expected)
    {
        Assert.Equal(
            expected,
            InstallationSecurityPolicy.ShouldClearDeviceSecurityState(
                cleanupCompleted,
                hasLocalVault));
    }

    [Fact]
    public async Task Missing_marker_after_vault_creation_must_not_remove_the_device_key()
    {
        await using var context = await TestContext.CreateAsync();
        const string passphrase = "Correct Horse Battery Staple 42!";
        await context.Keys.InitializeVaultAsync(passphrase);
        context.Keys.LockVault();

        var hasLocalVault = await context.Repository.GetVaultMetadataAsync() is not null;
        var shouldClearKeys = InstallationSecurityPolicy.ShouldClearDeviceSecurityState(
            cleanupCompleted: false,
            hasLocalVault);

        Assert.False(shouldClearKeys);
        await context.Keys.UnlockVaultAsync(passphrase);
        Assert.True(context.Keys.IsUnlocked);
    }
}
