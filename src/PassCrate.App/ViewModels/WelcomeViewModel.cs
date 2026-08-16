using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Views;

namespace PassCrate.App.ViewModels;

public sealed partial class WelcomeViewModel : BaseViewModel
{
    [RelayCommand]
    private static Task CreateAccountAsync() => Shell.Current.GoToAsync("//register");

    [RelayCommand]
    private static Task SignInAsync() => Shell.Current.GoToAsync("//unlock");

    [RelayCommand]
    private static Task RestoreFromCloudAsync() => Shell.Current.GoToAsync(nameof(CloudRestorePage));
}
