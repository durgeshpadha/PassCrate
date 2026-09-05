using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.App.Services;
using PassCrate.App.Views;
using PassCrate.Core.Help;
using PassCrate.Core.Legal;

namespace PassCrate.App.ViewModels;

public sealed partial class LegalAcceptanceViewModel(
    ILegalAcceptanceStore acceptanceStore) : ObservableObject, IQueryAttributable
{
    private string destination = LegalAcceptancePolicy.CreateDestination;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptAndContinueCommand))]
    private bool acceptsTerms;

    [ObservableProperty] private string purposeText =
        "Review this information before creating an encrypted vault.";

    public string VersionText =>
        $"Terms {LegalAcceptancePolicy.CurrentTermsVersion} · Privacy {LegalAcceptancePolicy.CurrentPrivacyVersion}";

    private bool CanAcceptAndContinue() => AcceptsTerms;

    [RelayCommand]
    private void ToggleAcceptance() => AcceptsTerms = !AcceptsTerms;

    [RelayCommand(CanExecute = nameof(CanAcceptAndContinue))]
    private async Task AcceptAndContinueAsync()
    {
        acceptanceStore.AcceptCurrentVersions();
        var next = destination;
        AcceptsTerms = false;
        await Shell.Current.GoToAsync("..", false);
        await Shell.Current.GoToAsync(
            next == LegalAcceptancePolicy.RestoreDestination
                ? nameof(CloudRestorePage)
                : "//register");
    }

    [RelayCommand]
    private static Task CancelAsync() => Shell.Current.GoToAsync("..");

    [RelayCommand]
    private static Task ReadTermsAsync() =>
        Shell.Current.GoToAsync($"{nameof(LegalDocumentPage)}?document={LegalDocuments.TermsId}");

    [RelayCommand]
    private static Task ReadPrivacyAsync() =>
        Shell.Current.GoToAsync($"{nameof(LegalDocumentPage)}?document={LegalDocuments.PrivacyId}");

    [RelayCommand]
    private static Task BackupHelpAsync() =>
        Shell.Current.GoToAsync($"{nameof(HelpPage)}?topic={HelpTopicIds.Restore}");

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        destination = LegalAcceptancePolicy.NormalizeDestination(
            query.TryGetValue("next", out var value) ? Convert.ToString(value) : null);
        PurposeText = destination == LegalAcceptancePolicy.RestoreDestination
            ? "Review this information before connecting a cloud account and restoring an encrypted vault."
            : "Review this information before creating an encrypted vault.";
        AcceptsTerms = false;
    }
}
