using PassCrate.Core.Interfaces;

namespace PassCrate.App.Services;

public sealed class MauiCloudNetworkPolicy : ICloudNetworkPolicy
{
    public bool IsInternetAvailable => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    public bool IsUsingWifi => Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
}
