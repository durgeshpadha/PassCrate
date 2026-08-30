using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class GroupEditorViewModel(IVaultService vaultService) : BaseViewModel
{
    private static readonly GroupIconOption[] IconPalette =
    [
        new("Diamond", "◇"),
        new("Spark", "✦"),
        new("Card", "▰"),
        new("Key", "⌁"),
        new("Gem", "◆"),
        new("Circle", "●"),
        new("Star", "★"),
        new("List", "☰"),
    ];

    private static readonly GroupColorOption[] ColorPalette =
    [
        new("Indigo", "#4F46E5"),
        new("Teal", "#0F766E"),
        new("Green", "#10B981"),
        new("Amber", "#B45309"),
        new("Gold", "#F59E0B"),
        new("Red", "#B91C1C"),
        new("Purple", "#7E22CE"),
        new("Blue", "#0369A1"),
        new("Slate", "#475569"),
        new("Gray", "#64748B"),
    ];

    public IReadOnlyList<GroupIconOption> Icons { get; } = IconPalette;
    public IReadOnlyList<GroupColorOption> Colors { get; } = ColorPalette;

    [ObservableProperty] private string groupId = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedIcon))]
    private GroupIconOption selectedIconOption = IconPalette[0];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedColorHex))]
    private GroupColorOption selectedColorOption = ColorPalette[0];
    [ObservableProperty] private string pageTitle = "New group";
    [ObservableProperty] private bool isNameInvalid;
    [ObservableProperty] private bool isIconInvalid;
    [ObservableProperty] private bool isColorInvalid;

    public string SelectedIcon => SelectedIconOption.Glyph;
    public string SelectedColorHex => SelectedColorOption.Hex;

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
            SelectedIconOption = Icons.FirstOrDefault(
                icon => string.Equals(icon.Glyph, group.Icon, StringComparison.Ordinal)) ?? Icons[0];
            SelectedColorOption = Colors.FirstOrDefault(
                color => string.Equals(color.Hex, group.Color, StringComparison.OrdinalIgnoreCase)) ?? Colors[0];
            PageTitle = "Edit group";
        }, "Unable to load the group.");
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (string.IsNullOrWhiteSpace(Name))
        {
            IsNameInvalid = true;
            throw new UserInputValidationException("Enter a group name.");
        }

        if (!Icons.Contains(SelectedIconOption))
        {
            IsIconInvalid = true;
            throw new UserInputValidationException("Choose an icon for the group.");
        }

        if (!Colors.Contains(SelectedColorOption))
        {
            IsColorInvalid = true;
            throw new UserInputValidationException("Choose a color for the group.");
        }

        if (string.IsNullOrWhiteSpace(GroupId))
        {
            await vaultService.CreateGroupAsync(Name, SelectedIcon, SelectedColorHex);
        }
        else
        {
            await vaultService.RenameGroupAsync(GroupId, Name, SelectedIcon, SelectedColorHex);
        }

        await Shell.Current.GoToAsync("..");
    }, "Unable to save the group.");

    partial void OnNameChanged(string value)
    {
        IsNameInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnSelectedIconOptionChanged(GroupIconOption value)
    {
        IsIconInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnSelectedColorOptionChanged(GroupColorOption value)
    {
        IsColorInvalid = false;
        ErrorMessage = string.Empty;
    }

    private void ClearValidationState()
    {
        IsNameInvalid = false;
        IsIconInvalid = false;
        IsColorInvalid = false;
    }
}

public sealed record GroupIconOption(string Name, string Glyph)
{
    public string DisplayName => $"{Glyph}  {Name}";
}

public sealed record GroupColorOption(string Name, string Hex);
