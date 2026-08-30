namespace PassCrate.App.Services;

public enum DeviceOwnerAuthenticationResult
{
    Success,
    Cancelled,
    Unavailable,
}

public interface IDeviceOwnerAuthenticationService
{
    Task<DeviceOwnerAuthenticationResult> AuthenticateAsync(
        string reason,
        CancellationToken cancellationToken = default);
}
