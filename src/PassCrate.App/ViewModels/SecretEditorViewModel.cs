using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.ViewModels;

public sealed partial class SecretEditorViewModel(IVaultService vaultService) : BaseViewModel, ISensitiveStateViewModel
{
    public ObservableCollection<VaultGroup> Groups { get; } = [];
    public ObservableCollection<FieldEditorViewModel> Fields { get; } = [];

    [ObservableProperty] private string secretId = string.Empty;
    [ObservableProperty] private string name = string.Empty;
    [ObservableProperty] private string notes = string.Empty;
    [ObservableProperty] private VaultGroup? selectedGroup;
    [ObservableProperty] private string pageTitle = "New secret";

    public async Task LoadAsync(string? id, string? groupId) => await RunBusyAsync(async () =>
    {
        SecretId = id ?? string.Empty;
        PageTitle = string.IsNullOrWhiteSpace(id) ? "New secret" : "Edit secret";
        Groups.Clear();
        foreach (var group in await vaultService.GetGroupsAsync())
        {
            Groups.Add(group);
        }

        SelectedGroup = Groups.FirstOrDefault(group => group.Id == groupId) ?? Groups.FirstOrDefault();
        Fields.Clear();
        if (string.IsNullOrWhiteSpace(id))
        {
            Fields.Add(new FieldEditorViewModel { Key = "Username" });
            Fields.Add(new FieldEditorViewModel { Key = "Password", IsSensitive = true });
            return;
        }

        var existing = await vaultService.ReadSecretAsync(id);
        var summary = (await vaultService.GetSecretsAsync()).First(secret => secret.Id == id);
        Name = existing.Name;
        Notes = existing.Notes;
        SelectedGroup = Groups.FirstOrDefault(group => group.Id == summary.GroupId);
        foreach (var field in existing.Fields)
        {
            Fields.Add(new FieldEditorViewModel
            {
                Key = field.Key,
                Value = field.Value,
                IsSensitive = field.IsSensitive,
            });
        }
    }, "Unable to prepare the editor.");

    [RelayCommand]
    private void AddField() => Fields.Add(new FieldEditorViewModel());

    [RelayCommand]
    private void RemoveField(FieldEditorViewModel field)
    {
        field.Value = string.Empty;
        Fields.Remove(field);
    }

    [RelayCommand]
    private async Task SaveAsync() => await RunBusyAsync(async () =>
    {
        if (SelectedGroup is null)
        {
            throw new InvalidOperationException("Choose a group.");
        }

        var document = new SecretDocument
        {
            Name = Name,
            Notes = Notes,
            Fields = Fields.Select(field => new SecretField
            {
                Key = field.Key,
                Value = field.Value,
                IsSensitive = field.IsSensitive,
            }).ToArray(),
        };

        if (string.IsNullOrWhiteSpace(SecretId))
        {
            SecretId = await vaultService.AddSecretAsync(SelectedGroup.Id, document);
        }
        else
        {
            await vaultService.UpdateSecretAsync(SecretId, SelectedGroup.Id, document);
        }

        ClearSensitiveState();
        await Shell.Current.GoToAsync("..");
    }, "Unable to save this secret.");

    public void ClearSensitiveState()
    {
        foreach (var field in Fields)
        {
            field.Value = string.Empty;
        }
        Fields.Clear();
        Notes = string.Empty;
    }
}

public sealed partial class FieldEditorViewModel : ObservableObject
{
    [ObservableProperty] private string key = string.Empty;
    [ObservableProperty] private string value = string.Empty;
    [ObservableProperty] private bool isSensitive;
}
