namespace PassCrate.Core.Security;

public static class InstallationSecurityPolicy
{
    public static bool ShouldClearDeviceSecurityState(
        bool cleanupCompleted,
        bool hasLocalVault) => !cleanupCompleted && !hasLocalVault;
}
