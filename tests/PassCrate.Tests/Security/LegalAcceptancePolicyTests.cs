using PassCrate.Core.Legal;

namespace PassCrate.Tests.Security;

public sealed class LegalAcceptancePolicyTests
{
    [Fact]
    public void IsCurrent_RequiresBothCurrentDocumentVersions()
    {
        var current = new LegalAcceptanceReceipt(
            LegalAcceptancePolicy.CurrentTermsVersion,
            LegalAcceptancePolicy.CurrentPrivacyVersion,
            "1.0",
            DateTimeOffset.UtcNow);

        Assert.True(LegalAcceptancePolicy.IsCurrent(current));
        Assert.False(LegalAcceptancePolicy.IsCurrent(current with { TermsVersion = "older" }));
        Assert.False(LegalAcceptancePolicy.IsCurrent(current with { PrivacyVersion = "older" }));
        Assert.False(LegalAcceptancePolicy.IsCurrent(null));
    }

    [Theory]
    [InlineData("//register", LegalAcceptancePolicy.CreateDestination)]
    [InlineData("RegistrationPage", LegalAcceptancePolicy.CreateDestination)]
    [InlineData("CloudRestorePage", LegalAcceptancePolicy.RestoreDestination)]
    public void GetDestinationForRoute_ProtectsBothFirstSetupFlows(
        string route,
        string expectedDestination) =>
        Assert.Equal(expectedDestination, LegalAcceptancePolicy.GetDestinationForRoute(route));

    [Theory]
    [InlineData("//welcome")]
    [InlineData("HelpPage?topic=restore-cloud")]
    [InlineData("LegalAcceptancePage?next=restore")]
    [InlineData("LegalDocumentPage?document=terms")]
    public void GetDestinationForRoute_LeavesPublicInformationPagesAvailable(string route) =>
        Assert.Null(LegalAcceptancePolicy.GetDestinationForRoute(route));

    [Fact]
    public void LegalDocuments_MatchCurrentVersionsAndContainRequiredSafeguards()
    {
        Assert.Equal(LegalAcceptancePolicy.CurrentTermsVersion, LegalDocuments.Terms.Version);
        Assert.Equal(LegalAcceptancePolicy.CurrentPrivacyVersion, LegalDocuments.Privacy.Version);
        Assert.Contains(LegalDocuments.Terms.Sections, section => section.Heading == "No warranty");
        Assert.Contains(LegalDocuments.Terms.Sections, section => section.Heading == "Limitation of liability");
        Assert.Contains(LegalDocuments.Privacy.Sections, section => section.Heading == "Retention and deletion");
        Assert.All(new[] { LegalDocuments.Terms, LegalDocuments.Privacy }, document =>
        {
            Assert.False(string.IsNullOrWhiteSpace(document.Introduction));
            Assert.NotEmpty(document.Sections);
            Assert.All(document.Sections, section =>
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Heading));
                Assert.False(string.IsNullOrWhiteSpace(section.Body));
            });
        });
    }
}
