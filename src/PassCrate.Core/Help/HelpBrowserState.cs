namespace PassCrate.Core.Help;

public sealed class HelpBrowserState
{
    public string Query { get; private set; } = string.Empty;
    public string? ExpandedTopicId { get; private set; }
    public IReadOnlyList<HelpTopic> VisibleTopics { get; private set; } = HelpCatalog.All;

    public void SetQuery(string? query)
    {
        Query = query ?? string.Empty;
        VisibleTopics = HelpCatalog.Search(Query);
    }

    public bool Toggle(string topicId)
    {
        var topic = HelpCatalog.FindById(topicId);
        if (topic is null)
        {
            return false;
        }

        ExpandedTopicId = string.Equals(ExpandedTopicId, topic.Id, StringComparison.Ordinal)
            ? null
            : topic.Id;
        return true;
    }

    public HelpTopic? OpenTopic(string? topicId)
    {
        var topic = HelpCatalog.FindById(topicId);
        if (topic is null)
        {
            return null;
        }

        SetQuery(string.Empty);
        ExpandedTopicId = topic.Id;
        return topic;
    }
}
