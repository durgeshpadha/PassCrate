using PassCrate.Core.Models;

namespace PassCrate.App.Services;

public sealed class CloudSyncSetupContinuation
{
    private const string PendingProviderKey = "passcrate.cloud-sync.pending-provider";
    private readonly object _sync = new();
    private CloudProviderKind? _provider;

    public void WaitForUnlock(CloudProviderKind provider)
    {
        lock (_sync)
        {
            _provider = provider;
            try
            {
                Preferences.Default.Set(PendingProviderKey, (int)provider);
            }
            catch
            {
                // In-memory continuation still works for the current process.
            }
        }
    }

    public bool TryGetProvider(out CloudProviderKind provider)
    {
        lock (_sync)
        {
            try
            {
                if (_provider is null && Preferences.Default.ContainsKey(PendingProviderKey))
                {
                    var persisted = Preferences.Default.Get(PendingProviderKey, -1);
                    if (Enum.IsDefined(typeof(CloudProviderKind), persisted))
                    {
                        _provider = (CloudProviderKind)persisted;
                    }
                    else
                    {
                        Preferences.Default.Remove(PendingProviderKey);
                    }
                }
            }
            catch
            {
                // A damaged preference must not prevent vault unlock.
            }

            if (_provider is not { } pendingProvider)
            {
                provider = default;
                return false;
            }

            provider = pendingProvider;
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _provider = null;
            try
            {
                Preferences.Default.Remove(PendingProviderKey);
            }
            catch
            {
                // Clearing the in-memory marker remains sufficient for this process.
            }
        }
    }
}
