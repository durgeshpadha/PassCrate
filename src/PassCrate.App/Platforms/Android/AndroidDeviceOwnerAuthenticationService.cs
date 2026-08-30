using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;

namespace PassCrate.App.Services;

public sealed class AndroidDeviceOwnerAuthenticationService : IDeviceOwnerAuthenticationService
{
    public async Task<DeviceOwnerAuthenticationResult> AuthenticateAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity;
        var executor = activity?.MainExecutor;
        var manager = activity?.GetSystemService(Context.BiometricService) as BiometricManager;
        var authenticators = (int)(
            BiometricManagerAuthenticators.BiometricStrong |
            BiometricManagerAuthenticators.DeviceCredential);
        if (activity is null || executor is null ||
            manager?.CanAuthenticate(authenticators) != BiometricCode.Success)
        {
            return DeviceOwnerAuthenticationResult.Unavailable;
        }

        var completion = new TaskCompletionSource<DeviceOwnerAuthenticationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(() =>
        {
            signal.Cancel();
            completion.TrySetCanceled(cancellationToken);
        });
        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle("Confirm it’s you")
            .SetSubtitle("Reset PassCrate")
            .SetDescription(reason)
            .SetAllowedAuthenticators(authenticators)
            .SetConfirmationRequired(true)
            .Build();
        prompt.Authenticate(signal, executor, new AuthenticationCallback(completion));
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private sealed class AuthenticationCallback(
        TaskCompletionSource<DeviceOwnerAuthenticationResult> completion)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) =>
            completion.TrySetResult(DeviceOwnerAuthenticationResult.Success);

        public override void OnAuthenticationError(
            BiometricErrorCode errorCode,
            Java.Lang.ICharSequence? errString) =>
            completion.TrySetResult(DeviceOwnerAuthenticationResult.Cancelled);
    }
}
