using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Help;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class ConflictReviewViewModel(
    IConflictRepository conflicts,
    ISyncRepository syncRepository,
    IVaultService vaultService,
    ICloudSyncScheduler syncScheduler) : BaseViewModel, ISensitiveStateViewModel
{
    private static readonly GroupColorOption[] ConflictColorPalette =
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

    public ObservableCollection<ConflictItemViewModel> ConflictItems { get; } = [];
    public ObservableCollection<ConflictRevisionItemViewModel> Revisions { get; } = [];
    public ObservableCollection<ConflictFieldEditViewModel> MergedFields { get; } = [];

    [ObservableProperty] private bool isLoaded;
    [ObservableProperty] private bool isCloudSyncEnabled;
    [ObservableProperty] private bool hasConflicts;
    [ObservableProperty] private ConflictItemViewModel? selectedConflict;
    [ObservableProperty] private ConflictRevisionItemViewModel? selectedRevision;
    [ObservableProperty] private string mergedName = string.Empty;
    [ObservableProperty] private string mergedNotes = string.Empty;
    [ObservableProperty] private string mergedIcon = "◇";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MergedColor))]
    private GroupColorOption mergedColorOption = ConflictColorPalette[0];
    [ObservableProperty] private bool showSensitiveValues;
    [ObservableProperty] private bool isMergedNameInvalid;
    [ObservableProperty] private bool isMergedIconInvalid;
    [ObservableProperty] private bool isMergedColorInvalid;

    public IReadOnlyList<GroupColorOption> ColorOptions { get; } = ConflictColorPalette;
    public string MergedColor => MergedColorOption.Hex;
    public bool ShowLoadingState => !IsLoaded;
    public bool ShowCloudSyncDisabledState => IsLoaded && !IsCloudSyncEnabled;
    public bool ShowNoConflictsState => IsLoaded && IsCloudSyncEnabled && !HasConflicts;
    public bool ShowConflictWorkspace => IsLoaded && IsCloudSyncEnabled && HasConflicts;
    public bool ShowReviewEditor => ShowConflictWorkspace && SelectedConflict is not null && SelectedRevision is not null;
    public bool ShowSelectionPrompt => ShowConflictWorkspace && !ShowReviewEditor;
    public bool IsGroupConflict => SelectedConflict?.Conflict.EntityKind == SyncEntityKind.Group;
    public bool IsSecretConflict => SelectedConflict?.Conflict.EntityKind == SyncEntityKind.Secret;
    public bool ShowManualMerge => ShowReviewEditor && SelectedRevision?.IsDeleted == false;
    public bool ShowKeepSelected => ShowReviewEditor && SelectedRevision?.IsDeleted == false;
    public bool ShowKeepBoth => ShowReviewEditor && IsSecretConflict &&
        Revisions.Count(revision => !revision.IsDeleted) > 1;
    public bool ShowAcceptDeletion => ShowReviewEditor && SelectedConflict?.Conflict.IncludesDeletion == true;
    public bool ShowRestoreGroup => ShowAcceptDeletion && IsGroupConflict;
    public bool ShowMoveAndDeleteGroup => ShowAcceptDeletion && IsGroupConflict;

    partial void OnIsLoadedChanged(bool value) => NotifyPageStateChanged();

    partial void OnIsCloudSyncEnabledChanged(bool value) => NotifyPageStateChanged();

    partial void OnHasConflictsChanged(bool value) => NotifyPageStateChanged();

    partial void OnSelectedConflictChanged(ConflictItemViewModel? value) => NotifyReviewStateChanged();

    partial void OnSelectedRevisionChanged(ConflictRevisionItemViewModel? value) => NotifyReviewStateChanged();

    public async Task LoadAsync()
    {
        IsLoaded = false;
        IsCloudSyncEnabled = false;
        HasConflicts = false;
        await RunBusyAsync(async () =>
        {
            ClearEditor();
            ConflictItems.Clear();
            var configuration = await syncRepository.GetConfigurationAsync();
            IsCloudSyncEnabled = configuration?.IsEnabled == true;
            if (IsCloudSyncEnabled)
            {
                foreach (var conflict in await conflicts.ListConflictsAsync())
                {
                    ConflictItems.Add(new ConflictItemViewModel(conflict));
                }
            }

            HasConflicts = ConflictItems.Count > 0;
        }, "Unable to load sync conflicts.");
        IsLoaded = true;
        NotifyPageStateChanged();
    }

    [RelayCommand]
    private async Task OpenAsync(ConflictItemViewModel item) => await RunBusyAsync(async () =>
    {
        ClearEditor();
        var conflict = item.Conflict;
        var details = await conflicts.GetConflictDetailsAsync(conflict.EntityKind, conflict.RecordId);
        SelectedConflict = item;
        item.IsSelected = true;
        for (var index = 0; index < details.Revisions.Count; index++)
        {
            Revisions.Add(new ConflictRevisionItemViewModel(details.Revisions[index], index + 1));
        }

        SelectRevision(Revisions.FirstOrDefault(revision => !revision.IsDeleted) ?? Revisions.FirstOrDefault());
        NotifyReviewStateChanged();
    }, "Unable to open this conflict.");

    [RelayCommand]
    private void SelectRevision(ConflictRevisionItemViewModel? revision)
    {
        ShowSensitiveValues = false;
        ResetMergedItem();
        foreach (var candidate in Revisions)
        {
            candidate.IsSelected = candidate == revision;
        }

        SelectedRevision = revision;
        if (revision?.Revision.Secret is { } secret)
        {
            MergedName = secret.Name;
            MergedNotes = secret.Notes;
            foreach (var field in secret.Fields)
            {
                MergedFields.Add(new ConflictFieldEditViewModel(field, true));
            }
        }
        else if (revision?.Revision.Group is { } group)
        {
            MergedName = group.Name;
            MergedIcon = group.Icon;
            MergedColorOption = ColorOptions.FirstOrDefault(
                color => string.Equals(color.Hex, group.Color, StringComparison.OrdinalIgnoreCase)) ?? ColorOptions[0];
        }

        NotifyReviewStateChanged();
    }

    partial void OnShowSensitiveValuesChanged(bool value)
    {
        foreach (var field in MergedFields)
        {
            field.SetSensitiveValuesVisible(value);
        }
    }

    [RelayCommand]
    private void AddField() =>
        MergedFields.Add(new ConflictFieldEditViewModel(
            new SecretField { Key = string.Empty, Value = string.Empty, IsSensitive = true },
            true));

    [RelayCommand]
    private void RemoveField(ConflictFieldEditViewModel field) => MergedFields.Remove(field);

    [RelayCommand]
    private Task KeepSelectedAsync() => ResolveAsync(ConflictResolutionKind.KeepRevision);

    [RelayCommand]
    private Task SaveManualMergeAsync() => ResolveAsync(ConflictResolutionKind.ManualMerge, manualMerge: true);

    [RelayCommand]
    private Task KeepBothAsync() => ResolveAsync(ConflictResolutionKind.KeepBoth);

    [RelayCommand]
    private async Task AcceptDeletionAsync()
    {
        if (!ShowAcceptDeletion)
        {
            await RunBusyAsync(
                () => throw new UserInputValidationException("Select a conflict that includes a deleted version."),
                "Unable to keep this item deleted.");
            return;
        }

        if (Shell.Current.CurrentPage is Page page &&
            !await page.DisplayAlertAsync(
                "Keep this item deleted?",
                "This resolves the conflict by deleting the item from this vault and syncing that choice to your other devices.",
                "Keep deleted",
                "Cancel"))
        {
            return;
        }

        await ResolveAsync(ConflictResolutionKind.AcceptDeletion);
    }

    [RelayCommand]
    private Task RestoreGroupAsync() => ResolveAsync(ConflictResolutionKind.RestoreGroup);

    [RelayCommand]
    private async Task MoveAndDeleteGroupAsync() => await RunBusyAsync(async () =>
    {
        if (!ShowMoveAndDeleteGroup || SelectedConflict is null)
        {
            throw new UserInputValidationException("Select a deleted group conflict first.");
        }

        var destinations = (await vaultService.GetGroupsAsync())
            .Where(group => group.Id != SelectedConflict.Conflict.RecordId)
            .ToArray();
        if (destinations.Length == 0)
        {
            throw new UserInputValidationException(
                "Create another group before deleting this group so its secrets have somewhere safe to move.");
        }

        var choices = CreateDestinationChoices(destinations);
        var selectedLabel = await Shell.Current.DisplayActionSheetAsync(
            "Move affected secrets to",
            "Cancel",
            null,
            choices.Select(choice => choice.Label).ToArray());
        var destination = choices.FirstOrDefault(choice => choice.Label == selectedLabel)?.Group;
        if (destination is null)
        {
            return;
        }

        if (Shell.Current.CurrentPage is Page page &&
            !await page.DisplayAlertAsync(
                "Move secrets and delete this group?",
                $"Secrets will move to {destination.Name}. The conflicted group will then stay deleted.",
                "Move and delete",
                "Cancel"))
        {
            return;
        }

        await vaultService.DeleteGroupAsync(SelectedConflict.Conflict.RecordId, destination.Id);
        await ResolveCoreAsync(new ConflictResolutionRequest
        {
            EntityKind = SyncEntityKind.Group,
            RecordId = SelectedConflict.Conflict.RecordId,
            Resolution = ConflictResolutionKind.MoveSecretsAndDeleteGroup,
            SelectedRevisionId = SelectedRevision?.Revision.RevisionId,
            DestinationGroupId = destination.Id,
        });
    }, "Unable to resolve the group deletion conflict.");

    [RelayCommand]
    private static Task OpenCloudSyncAsync() => Shell.Current.GoToAsync(nameof(Views.CloudSyncPage));

    [RelayCommand]
    private static Task HelpAsync() => Shell.Current.GoToAsync(
        $"{nameof(Views.HelpPage)}?topic={Uri.EscapeDataString(HelpTopicIds.Conflicts)}");

    private async Task ResolveAsync(
        ConflictResolutionKind kind,
        bool manualMerge = false) => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (SelectedConflict is null || SelectedRevision is null)
        {
            throw new UserInputValidationException("Select a saved version before continuing.");
        }

        if (kind == ConflictResolutionKind.KeepBoth && !ShowKeepBoth)
        {
            throw new UserInputValidationException(
                "Keep both is available only when a secret has two saved versions.");
        }

        if (kind == ConflictResolutionKind.RestoreGroup && !ShowRestoreGroup)
        {
            throw new UserInputValidationException("Select a deleted group conflict first.");
        }

        if (manualMerge && string.IsNullOrWhiteSpace(MergedName))
        {
            IsMergedNameInvalid = true;
            throw new UserInputValidationException("Enter a name for the combined item.");
        }

        if (manualMerge && IsGroupConflict &&
            string.IsNullOrWhiteSpace(MergedIcon))
        {
            IsMergedIconInvalid = true;
            throw new UserInputValidationException("Enter an icon for the merged group.");
        }

        if (manualMerge && IsGroupConflict &&
            !ColorOptions.Contains(MergedColorOption))
        {
            IsMergedColorInvalid = true;
            throw new UserInputValidationException("Choose a color for the combined group.");
        }

        if (manualMerge && IsSecretConflict)
        {
            var invalidFields = MergedFields.Where(field => string.IsNullOrWhiteSpace(field.Key)).ToArray();
            if (invalidFields.Length > 0)
            {
                foreach (var field in invalidFields)
                {
                    field.IsKeyInvalid = true;
                }

                throw new UserInputValidationException(
                    "Enter a label for every combined field, or remove empty fields.");
            }
        }

        var conflict = SelectedConflict.Conflict;
        var selectedRevision = SelectedRevision.Revision;
        var request = new ConflictResolutionRequest
        {
            EntityKind = conflict.EntityKind,
            RecordId = conflict.RecordId,
            Resolution = kind,
            SelectedRevisionId = selectedRevision.RevisionId,
            MergedGroup = manualMerge && IsGroupConflict && selectedRevision.Group is not null
                ? selectedRevision.Group with
                {
                    Name = MergedName.Trim(),
                    Icon = MergedIcon,
                    Color = MergedColor,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
                : null,
            MergedSecretDocument = manualMerge && IsSecretConflict
                ? new SecretDocument
                {
                    Name = MergedName.Trim(),
                    Notes = MergedNotes,
                    Fields = MergedFields.Select(field => new SecretField
                    {
                        Key = field.Key.Trim(),
                        Value = field.Value,
                        IsSensitive = field.IsSensitive,
                    }).ToArray(),
                }
                : null,
        };
        await ResolveCoreAsync(request);
    }, "Unable to resolve this conflict.");

    private async Task ResolveCoreAsync(ConflictResolutionRequest request)
    {
        var configuration = await syncRepository.GetConfigurationAsync()
            ?? throw new UserInputValidationException("Turn on cloud sync before resolving conflicts.");
        await conflicts.ResolveAsync(request, configuration.DeviceId);
        syncScheduler.NotifyVaultChanged();
        ClearEditor();
        await ReloadConflictsAsync();
    }

    private async Task ReloadConflictsAsync()
    {
        ConflictItems.Clear();
        foreach (var conflict in await conflicts.ListConflictsAsync())
        {
            ConflictItems.Add(new ConflictItemViewModel(conflict));
        }

        HasConflicts = ConflictItems.Count > 0;
        NotifyPageStateChanged();
    }

    public void ClearSensitiveState()
    {
        ShowSensitiveValues = false;
        ClearEditor();
    }

    private void ClearEditor()
    {
        ShowSensitiveValues = false;
        foreach (var item in ConflictItems)
        {
            item.IsSelected = false;
        }

        SelectedConflict = null;
        SelectedRevision = null;
        Revisions.Clear();
        ResetMergedItem();
        ClearValidationState();
        NotifyReviewStateChanged();
    }

    private void ResetMergedItem()
    {
        MergedFields.Clear();
        MergedName = string.Empty;
        MergedNotes = string.Empty;
        MergedIcon = "◇";
        MergedColorOption = ColorOptions[0];
    }

    private void NotifyPageStateChanged()
    {
        OnPropertyChanged(nameof(ShowLoadingState));
        OnPropertyChanged(nameof(ShowCloudSyncDisabledState));
        OnPropertyChanged(nameof(ShowNoConflictsState));
        OnPropertyChanged(nameof(ShowConflictWorkspace));
        NotifyReviewStateChanged();
    }

    private void NotifyReviewStateChanged()
    {
        OnPropertyChanged(nameof(ShowSelectionPrompt));
        OnPropertyChanged(nameof(ShowReviewEditor));
        OnPropertyChanged(nameof(IsGroupConflict));
        OnPropertyChanged(nameof(IsSecretConflict));
        OnPropertyChanged(nameof(ShowManualMerge));
        OnPropertyChanged(nameof(ShowKeepSelected));
        OnPropertyChanged(nameof(ShowKeepBoth));
        OnPropertyChanged(nameof(ShowAcceptDeletion));
        OnPropertyChanged(nameof(ShowRestoreGroup));
        OnPropertyChanged(nameof(ShowMoveAndDeleteGroup));
    }

    private static DestinationChoice[] CreateDestinationChoices(IReadOnlyList<VaultGroup> groups)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return groups.Select(group =>
        {
            var baseLabel = $"{group.Icon}  {group.Name}";
            seen.TryGetValue(baseLabel, out var count);
            count++;
            seen[baseLabel] = count;
            return new DestinationChoice(group, count == 1 ? baseLabel : $"{baseLabel} ({count})");
        }).ToArray();
    }

    partial void OnMergedNameChanged(string value)
    {
        IsMergedNameInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnMergedIconChanged(string value)
    {
        IsMergedIconInvalid = false;
        ErrorMessage = string.Empty;
    }

    partial void OnMergedColorOptionChanged(GroupColorOption value)
    {
        IsMergedColorInvalid = false;
        ErrorMessage = string.Empty;
    }

    private void ClearValidationState()
    {
        IsMergedNameInvalid = false;
        IsMergedIconInvalid = false;
        IsMergedColorInvalid = false;
        foreach (var field in MergedFields)
        {
            field.IsKeyInvalid = false;
        }
    }

    private sealed record DestinationChoice(VaultGroup Group, string Label);
}

public sealed partial class ConflictItemViewModel(SyncConflictSet conflict) : ObservableObject
{
    public SyncConflictSet Conflict { get; } = conflict;
    public string DisplayName => string.IsNullOrWhiteSpace(Conflict.DisplayName)
        ? Conflict.EntityKind == SyncEntityKind.Group ? "Group conflict" : "Secret conflict"
        : Conflict.DisplayName;
    public string KindLabel => Conflict.EntityKind == SyncEntityKind.Group ? "GROUP CONFLICT" : "SECRET CONFLICT";
    public string RevisionSummary => Conflict.RevisionIds.Count == 1
        ? "1 saved version"
        : $"{Conflict.RevisionIds.Count} saved versions";
    public bool IncludesDeletion => Conflict.IncludesDeletion;

    [ObservableProperty] private bool isSelected;
}

public sealed partial class ConflictRevisionItemViewModel : ObservableObject
{
    private readonly int _position;

    public ConflictRevisionItemViewModel(SyncConflictRevisionView revision, int position)
    {
        Revision = revision;
        _position = position;
    }

    public SyncConflictRevisionView Revision { get; }
    public string DisplayName => Revision.DisplayName;
    public bool IsDeleted => Revision.IsDeleted;
    public string DetailText => Revision.IsDeleted
        ? Revision.ChangedAt is { } deletedAt ? $"Deleted {deletedAt.ToLocalTime():g}" : "Deleted version"
        : Revision.ChangedAt is { } updatedAt
            ? $"Version {_position} · Updated {updatedAt.ToLocalTime():g}"
            : $"Saved version {_position}";
    public string AccessibilityDescription =>
        $"{DisplayName}. {DetailText}. Double tap to select this version.";

    [ObservableProperty] private bool isSelected;
}

public sealed partial class ConflictFieldEditViewModel : ObservableObject
{
    private bool _showSensitiveValues;

    public ConflictFieldEditViewModel(SecretField field, bool hideSensitiveValues)
    {
        Key = field.Key;
        Value = field.Value;
        IsSensitive = field.IsSensitive;
        _showSensitiveValues = !hideSensitiveValues;
        IsHidden = field.IsSensitive && hideSensitiveValues;
    }

    [ObservableProperty] private string key;
    [ObservableProperty] private string value;
    [ObservableProperty] private bool isSensitive;
    [ObservableProperty] private bool isHidden;
    [ObservableProperty] private bool isKeyInvalid;

    public void SetSensitiveValuesVisible(bool value)
    {
        _showSensitiveValues = value;
        IsHidden = IsSensitive && !value;
    }

    partial void OnKeyChanged(string value) => IsKeyInvalid = false;

    partial void OnIsSensitiveChanged(bool value) => IsHidden = value && !_showSensitiveValues;
}
