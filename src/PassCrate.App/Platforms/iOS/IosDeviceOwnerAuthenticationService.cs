using LocalAuthentication;

namespace PassCrate.App.Services;

public sealed class IosDeviceOwnerAuthenticationService : IDeviceOwnerAuthenticationService
{
    public async Task<DeviceOwnerAuthenticationResult> AuthenticateAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var context = new LAContext();
        if (!context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out _))
        {
            return DeviceOwnerAuthenticationResult.Unavailable;
        }

        var completion = new TaskCompletionSource<DeviceOwnerAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() =>
        {
            context.Invalidate();
            completion.TrySetCanceled(cancellationToken);
        });
        context.EvaluatePolicy(
            LAPolicy.DeviceOwnerAuthentication,
            reason,
            (success, _) => completion.TrySetResult(
                success
                    ? DeviceOwnerAuthenticationResult.Success
                    : DeviceOwnerAuthenticationResult.Cancelled));
        return await completion.Task.WaitAsync(cancellationToken);
    }
}
