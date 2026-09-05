using PassCrate.Core.Models;

namespace PassCrate.App.Services;

public sealed class CloudSyncSetupContinuation
{
    private readonly object _sync = new();
    private CloudProviderKind? _provider;

    public void WaitForUnlock(CloudProviderKind provider)
    {
        lock (_sync)
        {
            _provider = provider;
        }
    }

    public bool TryGetProvider(out CloudProviderKind provider)
    {
        lock (_sync)
        {
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
        }
    }
}
