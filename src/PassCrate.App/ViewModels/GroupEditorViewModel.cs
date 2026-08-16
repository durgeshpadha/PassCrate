using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;

namespace PassCrate.App.ViewModels;

public sealed partial class GroupEditorViewModel(IVaultService vaultService) : BaseViewModel
{
    public IReadOnlyList<string> Icons { get; } = ["◇", "✦", "▰", "⌁", "◆", "●", "★", "☰"];
    public IReadOnlyList<string> Colors { get; } =
        ["#4F46E5", "#0F766E", "#B45309", "#B91C1C", "#7E22CE", "#0369A1", "#475569"];

    [ObservableProperty] private string groupId = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string selectedIcon = "◇";
    [ObservableProperty] private string selectedColor = "#4F46E5";
    [ObservableProperty] private string pageTitle = "New group";

    public async Task LoadAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var group = (await vaultService.GetGroupsAsync())
                .FirstOrDefault(candidate => candidate.Id == id)
                ?? throw new KeyNotFoundException("Group not found.");
            GroupId = group.Id;
            Name = group.Name;
            SelectedIcon = group.Icon;
            SelectedColor = group.Color;
            PageTitle = "Edit group";
        }, "Unable to load the group.");
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunBusyAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!Icons.Contains(SelectedIcon, StringComparer.Ordinal) ||
            !Colors.Contains(SelectedColor, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose an icon and color from the PassCrate palette.");
        }

        if (string.IsNullOrWhiteSpace(GroupId))
        {
            await vaultService.CreateGroupAsync(Name, SelectedIcon, SelectedColor);
        }
        else
        {
            await vaultService.RenameGroupAsync(GroupId, Name, SelectedIcon, SelectedColor);
        }

        await Shell.Current.GoToAsync("..");
    }, "Unable to save the group.");
}
