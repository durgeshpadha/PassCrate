using PassCrate.Core.Help;

namespace PassCrate.Tests.Help;

public sealed class HelpCatalogTests
{
    private static readonly string[] RequiredSectionHeadings =
    [
        "What this page is for",
        "How to use it",
        "What happens next",
        "Security or data notes",
        "Common problems and solutions",
    ];

    [Fact]
    public void Catalog_UsesUniqueStableIdsAndCompleteContent()
    {
        Assert.NotEmpty(HelpCatalog.All);
        Assert.Equal(
            HelpCatalog.All.Count,
            HelpCatalog.All.Select(topic => topic.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var topic in HelpCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Id));
            Assert.False(string.IsNullOrWhiteSpace(topic.PageName));
            Assert.False(string.IsNullOrWhiteSpace(topic.Title));
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary));
            Assert.NotEmpty(topic.Keywords);
            Assert.Equal(RequiredSectionHeadings, topic.Sections.Select(section => section.Heading));
            Assert.All(topic.Sections, section => Assert.False(string.IsNullOrWhiteSpace(section.Body)));
            Assert.NotEmpty(topic.Sections.Single(section => section.Heading == "How to use it").Steps);
            Assert.NotEmpty(topic.Sections.Single(section => section.Heading == "Common problems and solutions").Troubleshooting);
        }
    }

    [Fact]
    public void Catalog_CoversEveryApplicationScreenExactlyOnce()
    {
        var coveredScreens = HelpCatalog.All
            .SelectMany(topic => topic.CoveredScreens)
            .OrderBy(screen => screen, StringComparer.Ordinal)
            .ToArray();
        var expectedScreens = HelpScreenIds.All
            .OrderBy(screen => screen, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedScreens, coveredScreens);
    }

    [Theory]
    [InlineData("biometric", HelpTopicIds.Unlock)]
    [InlineData("BIOMETRIC", HelpTopicIds.Settings)]
    [InlineData("field label", HelpTopicIds.Search)]
    [InlineData("real-time push", HelpTopicIds.CloudSync)]
    [InlineData("phone verification", HelpTopicIds.Reset)]
    [InlineData("hexadecimal", HelpTopicIds.EditGroup)]
    public void Search_MatchesTitlesKeywordsStepsAndGuidance(string query, string expectedTopicId)
    {
        var results = HelpCatalog.Search(query);

        Assert.Contains(results, topic => topic.Id == expectedTopicId);
    }

    [Fact]
    public void Search_EmptyQueryReturnsAllTopics()
    {
        Assert.Equal(HelpCatalog.All, HelpCatalog.Search(null));
        Assert.Equal(HelpCatalog.All, HelpCatalog.Search("  "));
    }

    [Fact]
    public void Search_RequiresEveryWordButIgnoresCase()
    {
        var results = HelpCatalog.Search("CLOUD PASSPHRASE");

        Assert.NotEmpty(results);
        Assert.All(results, topic =>
        {
            var allText = string.Join(
                ' ',
                topic.Sections.Select(section =>
                    $"{section.Heading} {section.Body} {string.Join(' ', section.Steps.Select(step => step.Text))}"));
            var metadata = $"{topic.Title} {topic.Summary} {string.Join(' ', topic.Keywords)} {allText}";
            Assert.Contains("cloud", metadata, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("passphrase", metadata, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Search_UnknownPhraseReturnsFriendlyEmptyResult()
    {
        Assert.Empty(HelpCatalog.Search("zebra quantum telescope"));
    }

    [Fact]
    public void FindById_HandlesKnownMixedCaseAndUnknownIds()
    {
        Assert.Equal(HelpTopicIds.UnlockRecovery, HelpCatalog.FindById("UNLOCK-RECOVERY")?.Id);
        Assert.Null(HelpCatalog.FindById("not-a-topic"));
        Assert.Null(HelpCatalog.FindById(null));
    }

    [Fact]
    public void BrowserState_SearchClearAndExclusiveExpansionWorkTogether()
    {
        var state = new HelpBrowserState();

        state.SetQuery("biometric");
        Assert.NotEmpty(state.VisibleTopics);
        Assert.All(state.VisibleTopics, topic =>
            Assert.Contains(topic.Id, HelpCatalog.Search("biometric").Select(result => result.Id)));

        Assert.True(state.Toggle(HelpTopicIds.Unlock));
        Assert.Equal(HelpTopicIds.Unlock, state.ExpandedTopicId);

        Assert.True(state.Toggle(HelpTopicIds.Settings));
        Assert.Equal(HelpTopicIds.Settings, state.ExpandedTopicId);

        Assert.True(state.Toggle(HelpTopicIds.Settings));
        Assert.Null(state.ExpandedTopicId);

        state.SetQuery(string.Empty);
        Assert.Equal(HelpCatalog.All, state.VisibleTopics);
    }

    [Fact]
    public void BrowserState_ContextualTopicClearsSearchAndUnknownTopicIsIgnored()
    {
        var state = new HelpBrowserState();
        state.SetQuery("cloud");

        var opened = state.OpenTopic(HelpTopicIds.UnlockRecovery);

        Assert.Equal(HelpTopicIds.UnlockRecovery, opened?.Id);
        Assert.Equal(HelpTopicIds.UnlockRecovery, state.ExpandedTopicId);
        Assert.Equal(string.Empty, state.Query);
        Assert.Equal(HelpCatalog.All, state.VisibleTopics);

        Assert.Null(state.OpenTopic("unknown-topic"));
        Assert.Equal(HelpTopicIds.UnlockRecovery, state.ExpandedTopicId);
    }
}
