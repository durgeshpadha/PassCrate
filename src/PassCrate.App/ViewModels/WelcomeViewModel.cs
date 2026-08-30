using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Views;
using PassCrate.Core.Help;

namespace PassCrate.App.ViewModels;

public sealed partial class WelcomeViewModel : BaseViewModel
{
    [RelayCommand]
    private static Task CreateAccountAsync() => Shell.Current.GoToAsync("//register");

    [RelayCommand]
    private static Task RestoreFromCloudAsync() => Shell.Current.GoToAsync(nameof(CloudRestorePage));

    [RelayCommand]
    private static Task HelpAsync() =>
        Shell.Current.GoToAsync($"{nameof(HelpPage)}?topic={HelpTopicIds.Welcome}");
}
