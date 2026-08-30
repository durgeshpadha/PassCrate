using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Help;

namespace PassCrate.App.ViewModels;

public sealed partial class HelpTopicItemViewModel(HelpTopic topic) : ObservableObject
{
    public HelpTopic Topic { get; } = topic;
    public string Id => Topic.Id;
    public string PageName => Topic.PageName;
    public string Title => Topic.Title;
    public string Summary => Topic.Summary;
    public IReadOnlyList<HelpSection> Sections => Topic.Sections;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionIndicator))]
    [NotifyPropertyChangedFor(nameof(AccessibilityDescription))]
    private bool isExpanded;

    public string ExpansionIndicator => IsExpanded ? "−" : "+";

    public string AccessibilityDescription => IsExpanded
        ? $"{Title}. {Summary} Expanded. Double tap to collapse."
        : $"{Title}. {Summary} Collapsed. Double tap to expand.";
}

public sealed class HelpCategoryGroup(
    HelpCategory category,
    IEnumerable<HelpTopicItemViewModel> topics) : ObservableCollection<HelpTopicItemViewModel>(topics)
{
    public HelpCategory Category { get; } = category;
    public string Title { get; } = HelpCatalog.GetCategoryTitle(category);
}

public sealed partial class HelpViewModel : ObservableObject
{
    private readonly HelpBrowserState state = new();
    private readonly IReadOnlyList<HelpTopicItemViewModel> allTopics =
        HelpCatalog.All.Select(topic => new HelpTopicItemViewModel(topic)).ToArray();

    public HelpViewModel()
    {
        VersionText = $"Help for PassCrate {AppInfo.Current.VersionString} · Available offline";
        ApplyFilter();
    }

    public ObservableCollection<HelpCategoryGroup> Groups { get; } = [];

    public string VersionText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuery))]
    private string query = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    private bool hasResults = true;

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool HasNoResults => !HasResults;

    partial void OnQueryChanged(string value)
    {
        state.SetQuery(value);
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearSearch() => Query = string.Empty;

    [RelayCommand]
    private void ToggleTopic(HelpTopicItemViewModel? topic)
    {
        if (topic is null)
        {
            return;
        }

        if (!state.Toggle(topic.Id))
        {
            return;
        }

        foreach (var item in allTopics)
        {
            item.IsExpanded = item.Id == state.ExpandedTopicId;
        }
    }

    public HelpTopicItemViewModel? OpenTopic(string? topicId)
    {
        var model = state.OpenTopic(topicId);
        if (model is null)
        {
            return null;
        }

        Query = string.Empty;
        var item = allTopics.First(topic => topic.Id == model.Id);
        foreach (var candidate in allTopics)
        {
            candidate.IsExpanded = candidate.Id == state.ExpandedTopicId;
        }

        return item;
    }

    public HelpCategoryGroup? FindGroup(HelpTopicItemViewModel topic) =>
        Groups.FirstOrDefault(group => group.Contains(topic));

    private void ApplyFilter()
    {
        var matchingIds = state.VisibleTopics
            .Select(topic => topic.Id)
            .ToHashSet(StringComparer.Ordinal);

        Groups.Clear();
        foreach (var category in Enum.GetValues<HelpCategory>())
        {
            var items = allTopics
                .Where(topic => topic.Topic.Category == category && matchingIds.Contains(topic.Id))
                .ToArray();
            if (items.Length > 0)
            {
                Groups.Add(new HelpCategoryGroup(category, items));
            }
        }

        HasResults = matchingIds.Count > 0;
    }
}
