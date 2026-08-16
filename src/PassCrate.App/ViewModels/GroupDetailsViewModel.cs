using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class GroupDetailsViewModel(IVaultService vaultService) : BaseViewModel
{
    public ObservableCollection<SecretSummary> Secrets { get; } = [];

    [ObservableProperty] private string groupId = string.Empty;
    [ObservableProperty] private string name = "Group";
    [ObservableProperty] private string icon = "◇";
    [ObservableProperty] private string color = "#4F46E5";

    public async Task LoadAsync(string id) => await RunBusyAsync(async () =>
    {
        GroupId = id;
        var group = (await vaultService.GetGroupsAsync()).FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new KeyNotFoundException("Group not found.");
        Name = group.Name;
        Icon = group.Icon;
        Color = group.Color;
        Secrets.Clear();
        foreach (var secret in await vaultService.GetSecretsAsync(id))
        {
            Secrets.Add(secret);
        }
    }, "Unable to load this group.");

    [RelayCommand]
    private Task AddSecretAsync() =>
        Shell.Current.GoToAsync($"{nameof(Views.SecretEditorPage)}?groupId={Uri.EscapeDataString(GroupId)}");

    [RelayCommand]
    private static Task OpenSecretAsync(SecretSummary secret) =>
        Shell.Current.GoToAsync($"{nameof(Views.SecretDetailsPage)}?id={Uri.EscapeDataString(secret.Id)}");

    [RelayCommand]
    private Task RenameAsync() =>
        Shell.Current.GoToAsync($"{nameof(Views.GroupEditorPage)}?id={Uri.EscapeDataString(GroupId)}");

    [RelayCommand]
    private async Task DeleteAsync()
    {
        string? destinationId = null;
        if (Secrets.Count > 0)
        {
            var destinations = (await vaultService.GetGroupsAsync())
                .Where(group => group.Id != GroupId)
                .ToArray();
            if (destinations.Length == 0)
            {
                await Shell.Current.DisplayAlertAsync(
                    "Create another group first",
                    "Secrets must be moved before this group can be deleted.",
                    "OK");
                return;
            }

            var choice = await Shell.Current.DisplayActionSheetAsync(
                "Move secrets before deleting",
                "Cancel",
                null,
                destinations.Select(group => group.Name).ToArray());
            destinationId = destinations.FirstOrDefault(group => group.Name == choice)?.Id;
            if (destinationId is null)
            {
                return;
            }
        }

        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Delete group?",
            Secrets.Count > 0 ? "Its secrets will be moved to the selected group." : "The empty group will be deleted.",
            "Delete",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        await vaultService.DeleteGroupAsync(GroupId, destinationId);
        await Shell.Current.GoToAsync("..");
    }
}
