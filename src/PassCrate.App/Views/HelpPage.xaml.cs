using PassCrate.App.ViewModels;

namespace PassCrate.App.Views;

public partial class HelpPage : ContentPage, IQueryAttributable
{
    private readonly HelpViewModel viewModel;
    private HelpTopicItemViewModel? pendingTopic;
    private bool isVisible;

    public HelpPage(HelpViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = this.viewModel = viewModel;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        var topicId = query.TryGetValue("topic", out var value)
            ? Uri.UnescapeDataString(Convert.ToString(value) ?? string.Empty)
            : string.Empty;
        pendingTopic = viewModel.OpenTopic(topicId);
        ScheduleTopicScroll();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        isVisible = true;
        ScheduleTopicScroll();
    }

    protected override void OnDisappearing()
    {
        isVisible = false;
        base.OnDisappearing();
    }

    private void ScheduleTopicScroll()
    {
        if (!isVisible || pendingTopic is null)
        {
            return;
        }

        var topic = pendingTopic;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            if (!isVisible || pendingTopic != topic)
            {
                return;
            }

            var group = viewModel.FindGroup(topic);
            if (group is not null)
            {
                HelpTopics.ScrollTo(topic, group, ScrollToPosition.Start, animate: false);
                pendingTopic = null;
            }
        });
    }
}
