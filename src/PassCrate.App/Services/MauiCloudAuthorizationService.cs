using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App.Services;

public sealed class MauiCloudAuthorizationService(
    HttpClient httpClient,
    ISecureStorageService secureStorage,
    CloudOAuthOptions options,
    IGoogleDrivePlatformAuthorization googlePlatformAuthorization) : ICloudAuthorizationService
{
    private readonly SemaphoreSlim _authorizationLock = new(1, 1);

    public async Task AuthorizeAsync(CloudProviderKind provider, CancellationToken cancellationToken = default)
    {
        await _authorizationLock.WaitAsync(cancellationToken);
        try
        {
#if ANDROID || IOS
            if (provider == CloudProviderKind.GoogleDrive)
            {
                var nativeToken = await googlePlatformAuthorization.GetAccessTokenAsync(interactive: true, cancellationToken);
                await WriteTokenAsync(provider, new OAuthToken(nativeToken, null, DateTimeOffset.UtcNow.AddMinutes(45)), cancellationToken);
                return;
            }
#endif
            var existing = await ReadTokenAsync(provider, cancellationToken);
            if (existing is not null)
            {
                try
                {
                    _ = await GetValidAccessTokenAsync(provider, existing, cancellationToken);
                    return;
                }
                catch (CloudAuthorizationRequiredException)
                {
                    await secureStorage.RemoveAsync(StorageKey(provider), cancellationToken);
                }
            }

            var (clientId, redirectUri) = GetClientConfiguration(provider);
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(24));
            var authorizationUri = BuildAuthorizationUri(provider, clientId, redirectUri, challenge, state);
            var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = authorizationUri,
                CallbackUrl = new Uri(redirectUri),
                PrefersEphemeralWebBrowserSession = true,
            });
            if (!result.Properties.TryGetValue("state", out var returnedState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(returnedState)))
            {
                throw new CloudAuthorizationRequiredException();
            }

            if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                throw new CloudAuthorizationRequiredException();
            }

            var token = await ExchangeCodeAsync(provider, clientId, redirectUri, code, verifier, cancellationToken);
            await WriteTokenAsync(provider, token, cancellationToken);
        }
        finally
        {
            _authorizationLock.Release();
        }
    }

    public async Task<string> GetAccessTokenAsync(
        CloudProviderKind provider,
        CancellationToken cancellationToken = default)
    {
        await _authorizationLock.WaitAsync(cancellationToken);
        try
        {
            var token = await ReadTokenAsync(provider, cancellationToken)
                ?? throw new CloudAuthorizationRequiredException();
#if ANDROID || IOS
            if (provider == CloudProviderKind.GoogleDrive && token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var nativeToken = await googlePlatformAuthorization.GetAccessTokenAsync(interactive: false, cancellationToken);
                token = new OAuthToken(nativeToken, null, DateTimeOffset.UtcNow.AddMinutes(45));
                await WriteTokenAsync(provider, token, cancellationToken);
            }
#endif
            var valid = await GetValidAccessTokenAsync(provider, token, cancellationToken);
            if (!ReferenceEquals(valid, token))
            {
                await WriteTokenAsync(provider, valid, cancellationToken);
            }

            return valid.AccessToken;
        }
        finally
        {
            _authorizationLock.Release();
        }
    }

    public async Task DisconnectAsync(CloudProviderKind provider, CancellationToken cancellationToken = default)
    {
        try
        {
#if ANDROID || IOS
            if (provider == CloudProviderKind.GoogleDrive)
            {
                await googlePlatformAuthorization.DisconnectAsync(cancellationToken);
            }
#endif
            if (provider == CloudProviderKind.Dropbox &&
                await ReadTokenAsync(provider, cancellationToken) is { AccessToken.Length: > 0 } token)
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    "https://api.dropboxapi.com/2/auth/token/revoke");
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        token.AccessToken);
                using var response = await httpClient.SendAsync(request, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Revocation is best-effort. Removing the local credential below is
            // the security boundary for disconnect and device reset.
        }
        finally
        {
            // Local disconnect/reset must succeed even if provider revocation is
            // unavailable. The provider account can still be revoked externally.
            await secureStorage.RemoveAsync(StorageKey(provider), CancellationToken.None);
        }
    }

    private async Task<OAuthToken> GetValidAccessTokenAsync(
        CloudProviderKind provider,
        OAuthToken token,
        CancellationToken cancellationToken)
    {
        if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return token;
        }

#if ANDROID || IOS
        if (provider == CloudProviderKind.GoogleDrive)
        {
            throw new CloudAuthorizationRequiredException();
        }
#endif

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new CloudAuthorizationRequiredException();
        }

        var (clientId, _) = GetClientConfiguration(provider);
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint(provider))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken,
                ["client_id"] = clientId,
            }),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudAuthorizationRequiredException();
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        try
        {
            return ParseToken(responseBytes, token.RefreshToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private async Task<OAuthToken> ExchangeCodeAsync(
        CloudProviderKind provider,
        string clientId,
        string redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint(provider))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new CloudAuthorizationRequiredException();
        }

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        try
        {
            return ParseToken(responseBytes, null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }

    private (string ClientId, string RedirectUri) GetClientConfiguration(CloudProviderKind provider)
    {
        var result = provider switch
        {
            CloudProviderKind.GoogleDrive => (options.GoogleClientId, options.GoogleRedirectUri),
            CloudProviderKind.Dropbox => (options.DropboxAppKey, options.DropboxRedirectUri),
            _ => throw new NotSupportedException(),
        };
        if (string.IsNullOrWhiteSpace(result.Item1))
        {
            throw new InvalidOperationException(
                $"{ProviderName(provider)} OAuth is not configured for this build. See docs/cloud-sync-setup.md.");
        }

        return result;
    }

    private static Uri BuildAuthorizationUri(
        CloudProviderKind provider,
        string clientId,
        string redirectUri,
        string challenge,
        string state)
    {
        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        string endpoint;
        if (provider == CloudProviderKind.GoogleDrive)
        {
            endpoint = "https://accounts.google.com/o/oauth2/v2/auth";
            values["scope"] = "https://www.googleapis.com/auth/drive.appdata";
            values["access_type"] = "offline";
            values["prompt"] = "consent";
        }
        else
        {
            endpoint = "https://www.dropbox.com/oauth2/authorize";
            values["token_access_type"] = "offline";
        }

        return new Uri($"{endpoint}?{string.Join('&', values.Select(value =>
            $"{Uri.EscapeDataString(value.Key)}={Uri.EscapeDataString(value.Value)}"))}");
    }

    private static OAuthToken ParseToken(byte[] json, string? fallbackRefreshToken)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new CloudAuthorizationRequiredException();
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 14_400;
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString()
            : fallbackRefreshToken;
        return new OAuthToken(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn)));
    }

    private async Task<OAuthToken?> ReadTokenAsync(CloudProviderKind provider, CancellationToken cancellationToken)
    {
        var json = await secureStorage.GetAsync(StorageKey(provider), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OAuthToken>(json);
        }
        catch (JsonException)
        {
            await secureStorage.RemoveAsync(StorageKey(provider), cancellationToken);
            return null;
        }
    }

    private Task WriteTokenAsync(CloudProviderKind provider, OAuthToken token, CancellationToken cancellationToken) =>
        secureStorage.SetAsync(StorageKey(provider), JsonSerializer.Serialize(token), cancellationToken);

    private static string StorageKey(CloudProviderKind provider) => $"passcrate.oauth.{provider}.v2";
    private static string TokenEndpoint(CloudProviderKind provider) => provider == CloudProviderKind.GoogleDrive
        ? "https://oauth2.googleapis.com/token"
        : "https://api.dropboxapi.com/oauth2/token";
    private static string ProviderName(CloudProviderKind provider) => provider == CloudProviderKind.GoogleDrive
        ? "Google Drive"
        : "Dropbox";
    private static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record OAuthToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);
}
