# PassCrate

PassCrate is a privacy-first, local-first secrets vault for Android 13+ and iOS/iPadOS, built with .NET 10, .NET MAUI, MVVM, dependency injection, and SQLite.

PassCrate has no backend, analytics, or plaintext export. Optional end-to-end encrypted synchronization uses the user's Google Drive app-data folder or Dropbox App Folder directly.

## Security design

- one vault passphrase with a 16-character minimum, common-passphrase blocking, and versioned Argon2id derivation
- random 256-bit DEK, passphrase-derived AES-GCM wrapping, and a device-bound outer wrapper
- Android Keystore and iOS ThisDeviceOnly/biometric-current-set platform protection
- persistent exponential passphrase throttling without destructive attempt limits
- whole-document AES-256-GCM secret encryption with authenticated record metadata
- Android NoBackup storage/extraction exclusions and iOS backup exclusion
- app-scoped sensitive clipboard cleanup and live capture/background locking
- V2-only immutable encrypted cloud snapshots with opaque HMAC namespaces
- associative, commutative, idempotent revision-set merge and explicit conflict review
- bounded provider streaming, resumable OAuth setup, reconnect/retry states, namespace deletion, and delete-all
- guarded different-vault handling: verified restore, verified replace, local-preferred merge, or account switching
- locked dependency restore, vulnerability auditing, CI SBOM generation, and sanitized errors

Read [the security architecture](docs/SECURITY_ARCHITECTURE.md), [threat model](docs/THREAT_MODEL.md), [backup policy](docs/BACKUP_AND_PLATFORM_BEHAVIOR.md), and [cloud provider setup](docs/cloud-sync-setup.md) before changing security-sensitive code.

This application has not been deployed. It intentionally does not migrate obsolete development databases or cloud V1 snapshots; reset and recreate development vaults after format changes.

## Cloud recovery behavior

Cloud sync uses the vault passphrase that was current when its recovery envelope was uploaded. A passphrase change first verifies a cloud checkpoint, then commits the new local unlock metadata and pending cloud-recovery state together. If the final cloud publish is interrupted, normal sync retries it without reverting to the former recovery envelope. After that publish succeeds, an older passphrase is not accepted through an older snapshot. Normal synchronization retains the newest ten snapshots for recovery history, plus any older undominated concurrent heads needed for conflict resolution.

If a connected account contains a different PassCrate vault, the app never overwrites either vault automatically. It offers four explicit choices: verify and restore the cloud vault, upload and verify the local vault before replacing the selected cloud vault, preview and combine both vaults, or disconnect and use another account. Combine matches group names ignoring case and surrounding spaces only when there is one unambiguous phone match; ambiguous records remain separate. It skips identical secrets even after a retry and preserves same-name secrets with different contents in Conflict Review. Users can choose either version or keep both; keeping both appends `(duplicate)` to the cloud copy's name, adding a number when needed to keep names distinct. Unresolved secrets must be resolved before normal editing or deletion. Records sharing an existing ID retain the local version. Destructive choices require clear confirmation and phone-owner authentication.

## Cloud provider registration

- **Google Drive:** enable Google Drive API, configure an External OAuth audience and test users, request only `drive.appdata`, and register Android/iOS clients for the app identifier and signing identities.
- **Dropbox:** create a scoped **App Folder** application, enable only `files.metadata.read`, `files.content.read`, and `files.content.write`, register `passcrate://oauth2redirect/dropbox`, disable the unused implicit grant, and provide the public app key at build time. PassCrate uses OAuth authorization code flow with PKCE and offline refresh tokens; it never embeds the Dropbox app secret.

Follow the complete console and build instructions in [cloud provider setup](docs/cloud-sync-setup.md). Do not substitute Full Dropbox access or broader Google Drive scopes.

## First-use agreement

Before a new or reset installation can open either Create Vault or Restore from Cloud, it displays a shared Important Information screen. The user can read the bundled Terms of Use, Privacy Policy, and Backup & Recovery Help offline, must select an unchecked acceptance box, and can cancel without changing data. The acceptance receipt stores only the Terms version, Privacy version, app version, and UTC acceptance time in local preferences. Reset clears the receipt, and increasing either legal-document version requires renewed acceptance.

The in-app legal text is a product safeguard, not a substitute for jurisdiction-specific legal review. Distributed builds must also publish a valid public privacy-policy URL and accurate store data disclosures.

## Build and test

### Public website

`src/PassCrate.WebSite` is the .NET 10 MVC website with a React preview gallery and compiled Tailwind CSS. It serves the home page and the app's shared Privacy Policy and Terms of Use. With Node.js 22.12+ installed, run `dotnet run --project src/PassCrate.WebSite --launch-profile http` and open `http://localhost:5210`. See [the website README](src/PassCrate.WebSite/README.md) for frontend development and publishing instructions.

### Mobile app

Requirements: .NET SDK 10.0.400 with MAUI Android/iOS workloads, Android SDK, JDK 17 or newer, and a paired Mac with Xcode for device iOS builds.

```powershell
./scripts/validate-jdk.ps1
dotnet restore PassCrate.slnx --locked-mode
dotnet test tests\PassCrate.Tests\PassCrate.Tests.csproj --no-restore --configuration Release
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-android --no-restore --configuration Release -p:AndroidPackageFormat=aab
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-ios --no-restore --configuration Release
```

Do not commit locally signed APK/AAB files. Produce store artifacts only with JDK 17+ and a protected production upload key. Complete [the release security checklist](docs/RELEASE_SECURITY_CHECKLIST.md) and an independent mobile security review before treating a build as production-ready.
