using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class SecretDetailsViewModel(
    IVaultService vaultService,
    IVaultRepository repository,
    IConflictRepository conflicts,
    ISensitiveClipboardService sensitiveClipboard) : BaseViewModel, ISensitiveStateViewModel
{
    public ObservableCollection<SecretFieldItemViewModel> Fields { get; } = [];

    [ObservableProperty] private string secretId = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string notes = string.Empty;
    [ObservableProperty] private bool hasUnresolvedConflict;

    public bool ShowNormalActions => !HasUnresolvedConflict;

    public async Task LoadAsync(string id) => await RunBusyAsync(async () =>
    {
        SecretId = id;
        HasUnresolvedConflict = await conflicts.HasConflictAsync(SyncEntityKind.Secret, id);
        var document = await vaultService.ReadSecretAsync(id);
        Name = document.Name;
        Notes = document.Notes;
        Fields.Clear();
        foreach (var field in document.Fields)
        {
            Fields.Add(new SecretFieldItemViewModel(field));
        }
    }, "Unable to open this secret.");

    [RelayCommand]
    private async Task CopyFieldAsync(SecretFieldItemViewModel field)
    {
        var seconds = Math.Clamp((await repository.GetSettingsAsync()).ClipboardClearSeconds, 5, 300);
        await sensitiveClipboard.CopyAsync(field.Value, TimeSpan.FromSeconds(seconds));
    }

    [RelayCommand]
    private async Task CopyNotesAsync()
    {
        if (string.IsNullOrWhiteSpace(Notes))
        {
            return;
        }

        var seconds = Math.Clamp((await repository.GetSettingsAsync()).ClipboardClearSeconds, 5, 300);
        await sensitiveClipboard.CopyAsync(Notes, TimeSpan.FromSeconds(seconds));
    }

    [RelayCommand]
    private Task EditAsync() => HasUnresolvedConflict
        ? ReviewConflictAsync()
        : Shell.Current.GoToAsync($"{nameof(Views.SecretEditorPage)}?id={Uri.EscapeDataString(SecretId)}");

    [RelayCommand]
    private Task ReviewConflictAsync() => Shell.Current.GoToAsync(
        $"{nameof(Views.ConflictReviewPage)}?entityKind={(int)SyncEntityKind.Secret}&recordId={Uri.EscapeDataString(SecretId)}");

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var confirmed = await Shell.Current.DisplayAlertAsync(
            "Delete secret?",
            "This permanently removes the encrypted secret from this device.",
            "Delete",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        await vaultService.DeleteSecretAsync(SecretId);
        ClearSensitiveState();
        await Shell.Current.GoToAsync("..");
    }

    public void ClearSensitiveState()
    {
        Fields.Clear();
        Notes = string.Empty;
    }

    partial void OnHasUnresolvedConflictChanged(bool value) =>
        OnPropertyChanged(nameof(ShowNormalActions));
}

public sealed partial class SecretFieldItemViewModel(SecretField secretField) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayValue))]
    private bool isRevealed = !secretField.IsSensitive;

    public string Key => secretField.Key;
    public string Value => secretField.Value;
    public bool IsSensitive => secretField.IsSensitive;
    public string DisplayValue => IsRevealed ? secretField.Value : new string('•', Math.Clamp(secretField.Value.Length, 8, 16));

    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;
}
