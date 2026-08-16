using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class DashboardViewModel(
    IVaultService vaultService,
    IConflictRepository conflictRepository) : BaseViewModel
{
    public ObservableCollection<GroupCardViewModel> Groups { get; } = [];
    public ObservableCollection<SecretSummary> RecentSecrets { get; } = [];

    [ObservableProperty] private int secretCount;
    [ObservableProperty] private int groupCount;
    [ObservableProperty] private string greeting = "Good morning";
    [ObservableProperty] private string conflictBadge = string.Empty;

    [RelayCommand]
    public async Task LoadAsync() => await RunBusyAsync(async () =>
    {
        Greeting = DateTime.Now.Hour switch
        {
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };

        var groups = await vaultService.GetGroupsAsync();
        var secrets = await vaultService.GetSecretsAsync();
        Groups.Clear();
        foreach (var group in groups)
        {
            Groups.Add(new GroupCardViewModel(
                group,
                secrets.Count(secret => secret.GroupId == group.Id)));
        }

        RecentSecrets.Clear();
        foreach (var secret in secrets.Take(4))
        {
            RecentSecrets.Add(secret);
        }

        GroupCount = groups.Count;
        SecretCount = secrets.Count;
        var conflicts = await conflictRepository.ListConflictsAsync();
        ConflictBadge = conflicts.Count == 0 ? string.Empty : $"{conflicts.Count} sync conflict(s) need review";
    }, "Unable to load your vault.");

    [RelayCommand]
    private static Task AddSecretAsync() => Shell.Current.GoToAsync(nameof(Views.SecretEditorPage));

    [RelayCommand]
    private static Task AddGroupAsync() => Shell.Current.GoToAsync(nameof(Views.GroupEditorPage));

    [RelayCommand]
    private static Task OpenGroupAsync(GroupCardViewModel group) =>
        Shell.Current.GoToAsync($"{nameof(Views.GroupDetailsPage)}?id={Uri.EscapeDataString(group.Id)}");

    [RelayCommand]
    private static Task OpenSecretAsync(SecretSummary secret) =>
        Shell.Current.GoToAsync($"{nameof(Views.SecretDetailsPage)}?id={Uri.EscapeDataString(secret.Id)}");

    [RelayCommand]
    private static Task ReviewConflictsAsync() =>
        Shell.Current.GoToAsync(nameof(Views.ConflictReviewPage));
}

public sealed record GroupCardViewModel(VaultGroup Group, int SecretCount)
{
    public string Id => Group.Id;
    public string Name => Group.Name;
    public string Icon => Group.Icon;
    public string Color => Group.Color;
    public string CountLabel => SecretCount == 1 ? "1 secret" : $"{SecretCount} secrets";
}
