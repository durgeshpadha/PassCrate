namespace PassCrate.App.Services;

public interface IInstallationStateStore
{
    bool IsCleanupComplete { get; }
    void MarkCleanupComplete();
    void ClearUserPreferencesForReset();
}

public sealed class InstallationStateStore : IInstallationStateStore
{
    private const string CleanupSentinel = "passcrate.install.cleaned.v2";

    public bool IsCleanupComplete => Preferences.Default.Get(CleanupSentinel, false);

    public void MarkCleanupComplete() => Preferences.Default.Set(CleanupSentinel, true);

    public void ClearUserPreferencesForReset()
    {
        Preferences.Default.Clear();
        MarkCleanupComplete();
    }
}
