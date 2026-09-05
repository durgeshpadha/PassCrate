namespace PassCrate.Tests.Sync;

public sealed class CloudSyncResolutionUiContractTests
{
    [Fact]
    public void ResolutionChoices_AreOrderedForNonTechnicalUsers_WithNoDefaultAction()
    {
        var source = ReadRepositoryFile("src", "PassCrate.App", "ViewModels", "CloudSyncViewModel.cs");

        Assert.Contains(
            "selectedResolutionChoice = CloudVaultResolutionChoice.None",
            source,
            StringComparison.Ordinal);

        var combine = source.IndexOf("\"Combine both vaults\"", StringComparison.Ordinal);
        var useCloud = source.IndexOf("\"Use the cloud vault\"", StringComparison.Ordinal);
        var usePhone = source.IndexOf("\"Use this phone's vault\"", StringComparison.Ordinal);
        var anotherAccount = source.IndexOf("\"Choose another cloud account\"", StringComparison.Ordinal);

        Assert.True(combine >= 0);
        Assert.True(combine < useCloud);
        Assert.True(useCloud < usePhone);
        Assert.True(usePhone < anotherAccount);
        Assert.Contains("isRecommended: true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionPage_UsesTwoSteps_OneDetailsForm_AndFixedActions()
    {
        var xaml = ReadRepositoryFile("src", "PassCrate.App", "Views", "CloudSyncPage.xaml");

        Assert.Contains("Choose what you want to keep (1 of 4)", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsChoosingResolution}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsCombineResolution}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsUseCloudResolution}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsUsePhoneResolution}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsAnotherAccountResolution}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ContinueResolutionCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding CanContinueResolution}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("1  Restore the existing cloud vault", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionPage_HasPageLevelBlockingProgressFeedback()
    {
        var xaml = ReadRepositoryFile("src", "PassCrate.App", "Views", "CloudSyncPage.xaml");
        var source = ReadRepositoryFile("src", "PassCrate.App", "ViewModels", "CloudSyncViewModel.cs");

        Assert.Contains("Grid.RowSpan=\"2\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ZIndex=\"100\"", xaml, StringComparison.Ordinal);
        Assert.Contains("InputTransparent=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding IsBusy}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsEnabled=\"{Binding IsNotBusy}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NavigateBackCommand}\"", xaml, StringComparison.Ordinal);

        Assert.Contains("Verifying the cloud vault…", source, StringComparison.Ordinal);
        Assert.Contains("Preparing your merge preview…", source, StringComparison.Ordinal);
        Assert.Contains("Combining and securely uploading…", source, StringComparison.Ordinal);
        Assert.Contains("Uploading and verifying this vault…", source, StringComparison.Ordinal);
        Assert.Contains("Restoring the cloud vault…", source, StringComparison.Ordinal);
        Assert.Contains("Disconnecting this cloud account…", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingBackupOrChoice_ClearsSensitiveResolutionState()
    {
        var source = ReadRepositoryFile("src", "PassCrate.App", "ViewModels", "CloudSyncViewModel.cs");

        Assert.Contains("partial void OnSelectedMismatchVaultChanged", source, StringComparison.Ordinal);
        Assert.Contains("ClearMismatchSensitiveState();", source, StringComparison.Ordinal);
        Assert.Contains("ResetResolutionSelection();", source, StringComparison.Ordinal);
        Assert.Contains("private void ChangeResolutionChoice()", source, StringComparison.Ordinal);
        Assert.Contains("TryReturnToResolutionChoices", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PassCrate.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory!.FullName, .. segments]));
    }
}
