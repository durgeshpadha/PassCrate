using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class SearchViewModel(IVaultService vaultService) : BaseViewModel
{
    private bool isResetting;
    public ObservableCollection<VaultGroup> GroupResults { get; } = [];
    public ObservableCollection<SecretSummary> SecretResults { get; } = [];

    [ObservableProperty] private string query = string.Empty;

    partial void OnQueryChanged(string value)
    {
        if (!isResetting)
        {
            SearchCommand.Execute(null);
        }
    }

    [RelayCommand]
    private async Task SearchAsync() => await RunBusyAsync(async () =>
    {
        GroupResults.Clear();
        SecretResults.Clear();
        var results = await vaultService.SearchAsync(Query);
        foreach (var result in results.Groups)
        {
            GroupResults.Add(result);
        }

        foreach (var result in results.Secrets)
        {
            SecretResults.Add(result);
        }
    }, "Unable to search your vault.");

    [RelayCommand]
    private static Task OpenSecretAsync(SecretSummary secret) =>
        Shell.Current.GoToAsync($"{nameof(Views.SecretDetailsPage)}?id={Uri.EscapeDataString(secret.Id)}");

    [RelayCommand]
    private static Task OpenGroupAsync(VaultGroup group) =>
        Shell.Current.GoToAsync($"{nameof(Views.GroupDetailsPage)}?id={Uri.EscapeDataString(group.Id)}");

    public void ResetState()
    {
        isResetting = true;
        Query = string.Empty;
        isResetting = false;
        GroupResults.Clear();
        SecretResults.Clear();
        ErrorMessage = string.Empty;
    }
}
