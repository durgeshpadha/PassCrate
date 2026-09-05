using PassCrate.Core.Security;

namespace PassCrate.Tests.Security;

public sealed class NavigationSecurityPolicyTests
{
    [Theory]
    [InlineData("//main/dashboard")]
    [InlineData("GroupDetailsPage?id=1")]
    [InlineData("SecretEditorPage")]
    [InlineData("SettingsPage")]
    [InlineData("CloudSyncPage")]
    public void ProtectedRoutesRequireAnUnlockedVault(string route)
    {
        Assert.True(NavigationSecurityPolicy.RequiresUnlockedVault(route));
    }

    [Theory]
    [InlineData("//welcome")]
    [InlineData("//unlock")]
    [InlineData("HelpPage")]
    [InlineData("HelpPage?topic=unlock-recovery")]
    [InlineData("LegalAcceptancePage?next=restore")]
    [InlineData("LegalDocumentPage?document=privacy")]
    [InlineData("CloudRestorePage")]
    [InlineData("ResetPage")]
    public void PublicAndRecoveryRoutesRemainAvailableWhileLocked(string route)
    {
        Assert.False(NavigationSecurityPolicy.RequiresUnlockedVault(route));
    }

    [Fact]
    public void EmptyRouteDoesNotRequireAnUnlockedVault()
    {
        Assert.False(NavigationSecurityPolicy.RequiresUnlockedVault(null));
        Assert.False(NavigationSecurityPolicy.RequiresUnlockedVault(string.Empty));
    }
}
