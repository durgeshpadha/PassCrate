using Foundation;
using Google.SignIn;
using PassCrate.App.Services;
using PassCrate.Core.Security;
using UIKit;

namespace PassCrate.App;

public sealed class IosGoogleDriveAuthorizationService(
    CloudOAuthOptions options) : IGoogleDrivePlatformAuthorization
{
    private const string DriveAppDataScope = "https://www.googleapis.com/auth/drive.appdata";

    public async Task<string> GetAccessTokenAsync(bool interactive, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.GoogleClientId))
        {
            throw new InvalidOperationException(
                "Google Drive OAuth is not configured for this build. See docs/cloud-sync-setup.md.");
        }

        var signIn = SignIn.SharedInstance;
        signIn.Configuration = new Configuration(options.GoogleClientId);
        var user = signIn.CurrentUser ?? await GetUserAsync(signIn, interactive, cancellationToken);
        if (user.GrantedScopes is null || !user.GrantedScopes.Contains(DriveAppDataScope))
        {
            if (!interactive)
            {
                throw new CloudAuthorizationRequiredException();
            }

            user = await AddDriveScopeAsync(user, cancellationToken);
        }

        user = await RefreshAsync(user, cancellationToken);
        return string.IsNullOrWhiteSpace(user.AccessToken.TokenString)
            ? throw new CloudAuthorizationRequiredException()
            : user.AccessToken.TokenString;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        SignIn.SharedInstance.DisconnectWithCompletion(error =>
        {
            if (error is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(new CloudAuthorizationRequiredException());
            }
        });
        return completion.Task;
    }

    private static Task<GoogleUser> GetUserAsync(
        SignIn signIn,
        bool interactive,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<GoogleUser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (interactive)
        {
            var presenter = Platform.GetCurrentUIViewController() ?? throw new CloudAuthorizationRequiredException();
            signIn.SignInWithPresentingViewController(presenter, (result, error) =>
            {
                registration.Dispose();
                if (error is not null || result?.User is null)
                {
                    completion.TrySetException(new CloudAuthorizationRequiredException());
                }
                else
                {
                    completion.TrySetResult(result.User);
                }
            });
        }
        else
        {
            signIn.RestorePreviousSignInWithCompletion((user, error) =>
            {
                registration.Dispose();
                if (error is not null || user is null)
                {
                    completion.TrySetException(new CloudAuthorizationRequiredException());
                }
                else
                {
                    completion.TrySetResult(user);
                }
            });
        }

        return completion.Task;
    }

    private static Task<GoogleUser> AddDriveScopeAsync(GoogleUser user, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<GoogleUser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        var presenter = Platform.GetCurrentUIViewController() ?? throw new CloudAuthorizationRequiredException();
        user.AddScopes([DriveAppDataScope], presenter, (result, error) =>
        {
            registration.Dispose();
            if (error is not null || result?.User is null)
            {
                completion.TrySetException(new CloudAuthorizationRequiredException());
            }
            else
            {
                completion.TrySetResult(result.User);
            }
        });
        return completion.Task;
    }

    private static Task<GoogleUser> RefreshAsync(GoogleUser user, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<GoogleUser>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        user.RefreshTokensIfNeededWithCompletion((refreshed, error) =>
        {
            registration.Dispose();
            if (error is not null || refreshed is null)
            {
                completion.TrySetException(new CloudAuthorizationRequiredException());
            }
            else
            {
                completion.TrySetResult(refreshed);
            }
        });
        return completion.Task;
    }
}
