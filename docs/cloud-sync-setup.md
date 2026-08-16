# Cloud sync provider setup

PassCrate has no backend. Release builds must be registered directly with Google and Dropbox before their sync buttons can authorize real accounts. Never commit an OAuth client secret, refresh token, signing key, or `GoogleService-Info.plist`.

## Google Drive

1. In Google Cloud Console, enable Drive API and configure the OAuth consent screen.
2. Declare only `https://www.googleapis.com/auth/drive.appdata`.
3. Create an Android OAuth client for package `com.passcrate.app` and the SHA-1 fingerprints of each authorized signing certificate. Android uses Google Identity `AuthorizationClient`; it does not use a client secret.
4. Create an iOS OAuth client for bundle ID `com.passcrate.app`. Record its client ID and reversed client ID. iOS uses Google Sign-In and requests the Drive scope only when cloud sync is enabled.
5. Supply the iOS values at build time:

```powershell
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-ios `
  -p:PassCrateGoogleClientId="YOUR_IOS_CLIENT_ID" `
  -p:PassCrateGoogleReversedClientId="YOUR_REVERSED_IOS_CLIENT_ID"
```

The build generates an intermediate partial iOS manifest containing the reversed callback scheme; it does not modify or persist credentials in source.

## Dropbox

1. Create a scoped Dropbox app with **App folder** access.
2. Enable the file metadata/content permissions needed to list, download, upload, and delete files inside that App Folder only.
3. Register `passcrate://oauth2redirect/dropbox` as an OAuth redirect URI.
4. Supply the public app key at build time. Mobile authorization uses code flow with PKCE; no client secret is embedded.

```powershell
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-android `
  -p:PassCrateDropboxAppKey="YOUR_DROPBOX_APP_KEY"
```

For iOS, pass `PassCrateDropboxAppKey` alongside the Google properties when building the same binary. `PassCrateGoogleRedirectUri` and `PassCrateDropboxRedirectUri` are overrideable MSBuild properties for controlled development environments; production should use the registered defaults.

## Required validation

Provider console registration cannot be validated by a simulator-only build. Before release, exercise the physical-device items in `RELEASE_SECURITY_CHECKLIST.md`, including revocation, callback routing, reinstall restore, quota/offline behavior, and a real two-device convergence test.
