# Cloud sync provider setup

PassCrate has no backend. Release builds must be registered directly with Google and Dropbox before their sync buttons can authorize real accounts. Never commit an OAuth client secret, refresh token, signing key, or `GoogleService-Info.plist`.

## Google Drive

1. In Google Cloud Console, enable Google Drive API and complete the required Branding fields for the OAuth consent screen.
2. Under Data Access, declare only `https://www.googleapis.com/auth/drive.appdata`. Do not add `drive.file`, full Drive, email, profile, or OpenID scopes; PassCrate does not request them.
3. Keep the audience set to External/Testing during development and add every Google account used for testing as a test user. Publish only after the production branding, policy, and verification requirements are complete.
4. Create an Android OAuth client for package `com.passcrate.app` and the SHA-1 fingerprint of every certificate that signs an authorized build, including the local debug certificate and each release/Play App Signing certificate. Android uses Google Identity `AuthorizationClient`; it does not use a client secret.
5. Create an iOS OAuth client for bundle ID `com.passcrate.app`. Record its client ID and reversed client ID. iOS uses Google Sign-In and requests the Drive scope only when cloud sync is enabled.
6. Supply the iOS values at build time:

```powershell
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-ios `
  -p:PassCrateGoogleClientId="YOUR_IOS_CLIENT_ID" `
  -p:PassCrateGoogleReversedClientId="YOUR_REVERSED_IOS_CLIENT_ID"
```

The build generates an intermediate partial iOS manifest containing the reversed callback scheme; it does not modify or persist credentials in source.

## Dropbox

1. Open the [Dropbox App Console](https://www.dropbox.com/developers/apps) and choose **Create app**.
2. Select **Scoped access**, choose **App folder**, and give the app its production-facing name. Do not select Full Dropbox: PassCrate reads and writes only its own folder under the user's Apps area.
3. On the **Permissions** tab, enable exactly:
   - `files.metadata.read` for listing backup files.
   - `files.content.read` for downloading and verifying backups.
   - `files.content.write` for uploading and deleting backups.
4. Do not enable account-information, sharing, team, OpenID, or unrelated file scopes. Save/submit the permission changes in the console before testing a newly issued authorization.
5. On the app's **Settings** tab, add this exact OAuth 2 redirect URI: `passcrate://oauth2redirect/dropbox`. Scheme, host, path, spelling, and slash placement must match.
6. Disable **Allow implicit grant** because PassCrate uses authorization code flow with PKCE. A mobile app cannot safely keep a client secret, so no Dropbox app secret is built into PassCrate.
7. A new Dropbox app starts in **Development** status and initially links only the owner's account. In the app's Settings, select **Enable additional users** before testing with other Dropbox accounts. Apply for **Production** status before allowing unrestricted public account linking; follow Dropbox's [production approval guidance](https://www.dropbox.com/developers/reference/developer-guide#production-approval).
8. Copy the public **App key**—not the app secret—and supply it at build time:

```powershell
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-android `
  -p:PassCrateDropboxAppKey="YOUR_DROPBOX_APP_KEY"
```

For iOS, pass `PassCrateDropboxAppKey` alongside the Google properties when building the same binary. `PassCrateGoogleRedirectUri` and `PassCrateDropboxRedirectUri` are overrideable MSBuild properties for controlled development environments; production should use the registered defaults.

PassCrate sends `token_access_type=offline`, receives a short-lived access token plus refresh token, and stores provider credentials in device-only secure storage. It refreshes access without asking the user each time, until the grant is revoked or invalid. Reset and Disconnect remove the local credentials and make a best-effort provider revocation; neither action deletes Dropbox backup files. These choices follow Dropbox's [OAuth guide](https://developers.dropbox.com/oauth-guide) for client-side mobile apps: authorization code flow with PKCE and refresh tokens.

### Dropbox validation

- Confirm a build without `PassCrateDropboxAppKey` fails with the sanitized “not configured” message instead of opening authorization.
- Confirm an unregistered or mistyped redirect URI is rejected and cannot return tokens to the app.
- Confirm first authorization returns to PassCrate, locks the vault, and resumes setup after the user unlocks.
- Confirm listing, upload/download verification, current-vault deletion, delete-all, disconnect, token refresh, and revocation work using only the three declared file scopes.
- Confirm the app can access only its Dropbox App Folder and cannot list unrelated user files.
- Confirm revoked access changes the UI to Reconnect and never exposes a provider response body or token.

## Interactive authorization behavior

Opening Google or Dropbox authorization moves PassCrate out of the foreground, so the vault locks by design and its in-memory key is cleared. After the provider grants access, unlock PassCrate again; the pending provider setup resumes instead of requiring a second Connect attempt. Authorization approval alone never unlocks the vault.

If the authorized account contains a different PassCrate vault, setup enters a dedicated decision screen instead of uploading automatically. The user may verify and restore the cloud vault, replace the selected cloud vault only after a verified local upload, preview and combine both vaults, or disconnect and choose another account. Combine matches group names ignoring capitalization and surrounding spaces only when one phone group is an unambiguous match; ambiguous groups and secrets stay separate. Identical secrets are kept once, including during a retry after cloud cleanup fails; same-name secrets with different contents are preserved in Conflict Review. Editing or deleting an unresolved secret is paused until that review is complete. Choosing Keep both adds `(duplicate)` to the cloud copy's name, or a numbered duplicate suffix when that name already exists. Matching record IDs retain local preference.

## Required validation

Provider console registration cannot be validated by a simulator-only build. Before release, exercise the physical-device items in `RELEASE_SECURITY_CHECKLIST.md`, including first-consent lock/unlock continuation, revocation, callback routing, reinstall restore, different-vault handling, quota/offline behavior, and a real two-device convergence test.
