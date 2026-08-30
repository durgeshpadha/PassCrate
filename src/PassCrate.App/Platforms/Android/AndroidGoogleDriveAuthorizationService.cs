using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Common;
using Android.Gms.Common.Apis;
using Android.Gms.Extensions;
using PassCrate.App.Services;
using PassCrate.Core.Security;

namespace PassCrate.App;

public sealed class AndroidGoogleDriveAuthorizationService : IGoogleDrivePlatformAuthorization
{
    private const string DriveAppDataScope = "https://www.googleapis.com/auth/drive.appdata";

    public async Task<string> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity ?? throw new CloudAuthorizationRequiredException();
        var client = Identity.GetAuthorizationClient(activity);
        var request = AuthorizationRequest.InvokeBuilder()
            .SetRequestedScopes([new Scope(DriveAppDataScope)])
            .Build();
        AuthorizationResult result;
        try
        {
            result = (AuthorizationResult)await client.Authorize(request);
        }
        catch (ApiException exception) when (exception.StatusCode == CommonStatusCodes.DeveloperError)
        {
            throw new CloudProviderConfigurationException(
                "Google Drive is not configured for this PassCrate build. Register the app package and signing certificate in Google Cloud, then try again.");
        }
        catch (ApiException)
        {
            throw new CloudAuthorizationRequiredException();
        }
        if (result.HasResolution)
        {
            if (!interactive || result.PendingIntent is null)
            {
                throw new CloudAuthorizationRequiredException();
            }

            var data = await GoogleAuthorizationActivityBroker.ResolveAsync(
                activity,
                result.PendingIntent,
                cancellationToken);
            result = client.GetAuthorizationResultFromIntent(data);
        }

        return string.IsNullOrWhiteSpace(result.AccessToken)
            ? throw new CloudAuthorizationRequiredException()
            : result.AccessToken;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null)
        {
            return;
        }

        var request = RevokeAccessRequest.InvokeBuilder().Build();
        await Identity.GetAuthorizationClient(activity).RevokeAccess(request);
    }
}

internal static class GoogleAuthorizationActivityBroker
{
    internal const int RequestCode = 7719;
    private static TaskCompletionSource<Intent>? _pending;

    public static async Task<Intent> ResolveAsync(
        Activity activity,
        PendingIntent pendingIntent,
        CancellationToken cancellationToken)
    {
        if (_pending is not null)
        {
            throw new InvalidOperationException("Google authorization is already in progress.");
        }

        var completion = new TaskCompletionSource<Intent>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            activity.StartIntentSenderForResult(
                pendingIntent.IntentSender,
                RequestCode,
                null,
                (ActivityFlags)0,
                (ActivityFlags)0,
                0);
            return await completion.Task;
        }
        finally
        {
            _pending = null;
        }
    }

    public static void Complete(Result resultCode, Intent? data)
    {
        if (resultCode == Result.Ok && data is not null)
        {
            _pending?.TrySetResult(data);
        }
        else
        {
            _pending?.TrySetException(new CloudAuthorizationRequiredException());
        }
    }
}
