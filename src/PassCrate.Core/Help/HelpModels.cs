namespace PassCrate.Core.Help;

public enum HelpCategory
{
    GettingStarted,
    UsingYourVault,
    SettingsAndSecurity,
    CloudBackupAndSync,
}

public enum HelpNoteType
{
    Normal,
    Tip,
    Security,
    Warning,
    Danger,
}

public sealed record HelpStep(int Number, string Text);

public sealed record HelpTroubleshootingItem(string Problem, string Solution);

public sealed record HelpSection(
    string Heading,
    string Body,
    HelpNoteType NoteType,
    IReadOnlyList<HelpStep> Steps,
    IReadOnlyList<HelpTroubleshootingItem> Troubleshooting);

public sealed record HelpTopic(
    string Id,
    HelpCategory Category,
    string PageName,
    string Title,
    string Summary,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<HelpSection> Sections,
    IReadOnlyList<string> CoveredScreens);

public static class HelpTopicIds
{
    public const string Welcome = "welcome";
    public const string CreateVault = "create-vault";
    public const string Unlock = "unlock";
    public const string UnlockRecovery = "unlock-recovery";
    public const string Home = "home";
    public const string Search = "search";
    public const string GroupDetails = "group-details";
    public const string EditGroup = "edit-group";
    public const string SecretDetails = "secret-details";
    public const string EditSecret = "edit-secret";
    public const string Settings = "settings";
    public const string ChangePassphrase = "change-passphrase";
    public const string Reset = "reset-passcrate";
    public const string Restore = "restore-cloud";
    public const string CloudSync = "cloud-sync";
    public const string MultipleDevices = "multiple-devices";
    public const string Conflicts = "conflicts";
    public const string OfflineSecurity = "offline-security";
}

public static class HelpScreenIds
{
    public const string Welcome = "WelcomePage";
    public const string Registration = "RegistrationPage";
    public const string Unlock = "UnlockPage";
    public const string Dashboard = "DashboardPage";
    public const string Search = "SearchPage";
    public const string GroupDetails = "GroupDetailsPage";
    public const string GroupEditor = "GroupEditorPage";
    public const string SecretDetails = "SecretDetailsPage";
    public const string SecretEditor = "SecretEditorPage";
    public const string Settings = "SettingsPage";
    public const string ChangePassword = "ChangePasswordPage";
    public const string Reset = "ResetPage";
    public const string CloudSync = "CloudSyncPage";
    public const string CloudRestore = "CloudRestorePage";
    public const string ConflictReview = "ConflictReviewPage";

    public static IReadOnlyList<string> All { get; } =
    [
        Welcome,
        Registration,
        Unlock,
        Dashboard,
        Search,
        GroupDetails,
        GroupEditor,
        SecretDetails,
        SecretEditor,
        Settings,
        ChangePassword,
        Reset,
        CloudSync,
        CloudRestore,
        ConflictReview,
    ];
}
