using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App.ViewModels;

public sealed partial class ConflictReviewViewModel(
    IConflictRepository conflicts,
    ISyncRepository syncRepository,
    IVaultService vaultService) : BaseViewModel, ISensitiveStateViewModel
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

    public ObservableCollection<SyncConflictSet> ConflictSets { get; } = [];
    public ObservableCollection<SyncConflictRevisionView> Revisions { get; } = [];
    public ObservableCollection<ConflictFieldEditViewModel> MergedFields { get; } = [];

    [ObservableProperty] private SyncConflictSet? selectedConflict;
    [ObservableProperty] private SyncConflictRevisionView? selectedRevision;
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

    public async Task LoadAsync() => await RunBusyAsync(async () =>
    {
        ConflictSets.Clear();
        foreach (var conflict in await conflicts.ListConflictsAsync())
        {
            ConflictSets.Add(conflict);
        }

        if (SelectedConflict is not null &&
            ConflictSets.All(item =>
                item.EntityKind != SelectedConflict.EntityKind ||
                item.RecordId != SelectedConflict.RecordId))
        {
            ClearEditor();
        }
    }, "Unable to load sync conflicts.");

    [RelayCommand]
    private async Task OpenAsync(SyncConflictSet conflict) => await RunBusyAsync(async () =>
    {
        SelectedConflict = conflict;
        var details = await conflicts.GetConflictDetailsAsync(conflict.EntityKind, conflict.RecordId);
        Revisions.Clear();
        foreach (var revision in details.Revisions)
        {
            Revisions.Add(revision);
        }

        SelectRevision(Revisions.FirstOrDefault(revision => !revision.IsDeleted) ?? Revisions.FirstOrDefault());
    }, "Unable to open this conflict.");

    [RelayCommand]
    private void SelectRevision(SyncConflictRevisionView? revision)
    {
        SelectedRevision = revision;
        MergedFields.Clear();
        if (revision?.Secret is { } secret)
        {
            MergedName = secret.Name;
            MergedNotes = secret.Notes;
            foreach (var field in secret.Fields)
            {
                MergedFields.Add(new ConflictFieldEditViewModel(field, !ShowSensitiveValues));
            }
        }
        else if (revision?.Group is { } group)
        {
            MergedName = group.Name;
            MergedIcon = group.Icon;
            MergedColorOption = ColorOptions.FirstOrDefault(
                color => string.Equals(color.Hex, group.Color, StringComparison.OrdinalIgnoreCase)) ?? ColorOptions[0];
        }
    }

    partial void OnShowSensitiveValuesChanged(bool value)
    {
        foreach (var field in MergedFields)
        {
            field.IsHidden = field.IsSensitive && !value;
        }
    }

    [RelayCommand]
    private void AddField() =>
        MergedFields.Add(new ConflictFieldEditViewModel(
            new SecretField { Key = string.Empty, Value = string.Empty },
            false));

    [RelayCommand]
    private void RemoveField(ConflictFieldEditViewModel field) => MergedFields.Remove(field);

    [RelayCommand]
    private Task KeepSelectedAsync() => ResolveAsync(ConflictResolutionKind.KeepRevision);

    [RelayCommand]
    private Task SaveManualMergeAsync() => ResolveAsync(ConflictResolutionKind.ManualMerge, manualMerge: true);

    [RelayCommand]
    private Task KeepBothAsync() => ResolveAsync(ConflictResolutionKind.KeepBoth);

    [RelayCommand]
    private Task AcceptDeletionAsync() => ResolveAsync(ConflictResolutionKind.AcceptDeletion);

    [RelayCommand]
    private Task RestoreGroupAsync() => ResolveAsync(ConflictResolutionKind.RestoreGroup);

    [RelayCommand]
    private async Task MoveAndDeleteGroupAsync() => await RunBusyAsync(async () =>
    {
        if (SelectedConflict?.EntityKind != SyncEntityKind.Group)
        {
            throw new UserInputValidationException("Select a group conflict first.");
        }

        var destinations = (await vaultService.GetGroupsAsync())
            .Where(group => group.Id != SelectedConflict.RecordId)
            .ToArray();
        var choice = await Shell.Current.DisplayActionSheetAsync(
            "Move affected secrets to",
            "Cancel",
            null,
            destinations.Select(group => group.Name).ToArray());
        var destination = destinations.FirstOrDefault(group => group.Name == choice);
        if (destination is null)
        {
            return;
        }

        await vaultService.DeleteGroupAsync(SelectedConflict.RecordId, destination.Id);
        await ResolveCoreAsync(new ConflictResolutionRequest
        {
            EntityKind = SyncEntityKind.Group,
            RecordId = SelectedConflict.RecordId,
            Resolution = ConflictResolutionKind.MoveSecretsAndDeleteGroup,
            SelectedRevisionId = SelectedRevision?.RevisionId,
        });
    }, "Unable to resolve the group deletion conflict.");

    private async Task ResolveAsync(
        ConflictResolutionKind kind,
        bool manualMerge = false) => await RunBusyAsync(async () =>
    {
        ClearValidationState();
        if (SelectedConflict is null || SelectedRevision is null)
        {
            throw new UserInputValidationException("Select a saved version before continuing.");
        }

        if (manualMerge && string.IsNullOrWhiteSpace(MergedName))
        {
            IsMergedNameInvalid = true;
            throw new UserInputValidationException("Enter a name for the combined item.");
        }

        if (manualMerge && SelectedConflict.EntityKind == SyncEntityKind.Group &&
            string.IsNullOrWhiteSpace(MergedIcon))
        {
            IsMergedIconInvalid = true;
            throw new UserInputValidationException("Enter an icon for the merged group.");
        }

        if (manualMerge && SelectedConflict.EntityKind == SyncEntityKind.Group &&
            !ColorOptions.Contains(MergedColorOption))
        {
            IsMergedColorInvalid = true;
            throw new UserInputValidationException("Choose a color for the combined group.");
        }

        if (manualMerge && SelectedConflict.EntityKind == SyncEntityKind.Secret)
        {
            var invalidFields = MergedFields.Where(field => string.IsNullOrWhiteSpace(field.Key)).ToArray();
            if (invalidFields.Length > 0)
            {
                foreach (var field in invalidFields)
                {
                    field.IsKeyInvalid = true;
                }

                throw new UserInputValidationException(
                    "Enter a label for every merged field, or remove empty fields.");
            }
        }

        var request = new ConflictResolutionRequest
        {
            EntityKind = SelectedConflict.EntityKind,
            RecordId = SelectedConflict.RecordId,
            Resolution = kind,
            SelectedRevisionId = SelectedRevision.RevisionId,
            MergedGroup = manualMerge && SelectedConflict.EntityKind == SyncEntityKind.Group &&
                          SelectedRevision.Group is not null
                ? SelectedRevision.Group with
                {
                    Name = MergedName.Trim(),
                    Icon = MergedIcon,
                    Color = MergedColor,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }
                : null,
            MergedSecretDocument = manualMerge && SelectedConflict.EntityKind == SyncEntityKind.Secret
                ? new SecretDocument
                {
                    Name = MergedName.Trim(),
                    Notes = MergedNotes,
                    Fields = MergedFields.Select(field => new SecretField
                    {
                        Key = field.Key,
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
            ?? throw new InvalidOperationException("Cloud sync is not configured.");
        await conflicts.ResolveAsync(request, configuration.DeviceId);
        ClearEditor();
        await LoadAsync();
    }

    public void ClearSensitiveState() => ClearEditor();

    private void ClearEditor()
    {
        SelectedConflict = null;
        SelectedRevision = null;
        Revisions.Clear();
        MergedFields.Clear();
        MergedName = MergedNotes = string.Empty;
        ClearValidationState();
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
}

public sealed partial class ConflictFieldEditViewModel : ObservableObject
{
    public ConflictFieldEditViewModel(SecretField field, bool isHidden)
    {
        Key = field.Key;
        Value = field.Value;
        IsSensitive = field.IsSensitive;
        IsHidden = field.IsSensitive && isHidden;
    }

    [ObservableProperty] private string key;
    [ObservableProperty] private string value;
    [ObservableProperty] private bool isSensitive;
    [ObservableProperty] private bool isHidden;
    [ObservableProperty] private bool isKeyInvalid;

    partial void OnKeyChanged(string value) => IsKeyInvalid = false;
}
