using PassCrate.Core.Interfaces;

namespace PassCrate.Infrastructure.Services;

public sealed class NullCloudSyncScheduler : ICloudSyncScheduler
{
    public void NotifyVaultChanged() { }
    public void NotifyVaultUnlocked() { }
    public void NotifyVaultLocked() { }
    public void NotifyForegrounded() { }
    public void NotifyBackgrounded() { }
}
