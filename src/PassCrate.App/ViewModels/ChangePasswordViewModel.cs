using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public sealed partial class ChangePasswordViewModel(ICloudSyncService cloudSync) : BaseViewModel, ISensitiveStateViewModel
{
    [ObservableProperty] private string currentPassphrase = string.Empty;
    [ObservableProperty] private string newPassphrase = string.Empty;
    [ObservableProperty] private string confirmNewPassphrase = string.Empty;

    [RelayCommand]
    private async Task ChangeAsync() => await RunBusyAsync(async () =>
    {
        if (NewPassphrase != ConfirmNewPassphrase)
        {
            throw new InvalidOperationException("New vault passphrases do not match.");
        }

        await cloudSync.ChangePassphraseAsync(CurrentPassphrase, NewPassphrase);
        CurrentPassphrase = NewPassphrase = ConfirmNewPassphrase = string.Empty;
        await Shell.Current.DisplayAlertAsync(
            "Passphrase changed",
            "Your vault passphrase now protects local and cloud recovery access.",
            "Done");
        await Shell.Current.GoToAsync("..");
    }, "Unable to change the vault passphrase.");

    public void ClearSensitiveState() =>
        CurrentPassphrase = NewPassphrase = ConfirmNewPassphrase = string.Empty;
}
