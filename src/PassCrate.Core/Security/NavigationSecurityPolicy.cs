namespace PassCrate.Core.Security;

public static class NavigationSecurityPolicy
{
    private static readonly string[] ProtectedRouteFragments =
    [
        "//main",
        "GroupDetailsPage",
        "GroupEditorPage",
        "SecretDetailsPage",
        "SecretEditorPage",
        "ChangePasswordPage",
        "CloudSyncPage",
        "ConflictReviewPage",
        "SettingsPage",
    ];

    public static bool RequiresUnlockedVault(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        ProtectedRouteFragments.Any(fragment =>
            target.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
