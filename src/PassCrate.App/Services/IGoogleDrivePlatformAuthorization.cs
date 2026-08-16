namespace PassCrate.App.Services;

public interface IGoogleDrivePlatformAuthorization
{
    Task<string> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public sealed class PortableGoogleDrivePlatformAuthorization : IGoogleDrivePlatformAuthorization
{
    public Task<string> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
