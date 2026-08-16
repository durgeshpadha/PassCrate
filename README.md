# PassCrate

PassCrate is a privacy-first, local-first secrets vault for Android 13+ and iOS/iPadOS, built with .NET 10, .NET MAUI, MVVM, dependency injection, and SQLite.

PassCrate has no backend, analytics, or plaintext export. Optional end-to-end encrypted synchronization uses the user's Google Drive app-data folder or Dropbox App Folder directly.

## Security design

- Argon2id V2 password, PIN, and recovery derivation with versioned upgrade checks
- random 256-bit DEK, PIN-derived AES-GCM wrapping, and a device-bound outer wrapper
- Android Keystore and iOS ThisDeviceOnly/biometric-current-set platform protection
- persistent exponential PIN throttling without destructive attempt limits
- whole-document AES-256-GCM secret encryption with authenticated record metadata
- Android NoBackup storage/extraction exclusions and iOS backup exclusion
- app-scoped sensitive clipboard cleanup and live capture/background locking
- V2-only immutable encrypted cloud snapshots with opaque HMAC namespaces
- associative, commutative, idempotent revision-set merge and explicit conflict review
- bounded provider streaming, reconnect/retry states, namespace deletion, and delete-all
- locked dependency restore, vulnerability auditing, CI SBOM generation, and sanitized errors

Read [the security architecture](docs/SECURITY_ARCHITECTURE.md), [threat model](docs/THREAT_MODEL.md), [backup policy](docs/BACKUP_AND_PLATFORM_BEHAVIOR.md), and [cloud provider setup](docs/cloud-sync-setup.md) before changing security-sensitive code.

This application has not been deployed. It intentionally does not migrate obsolete development databases or cloud V1 snapshots; reset and recreate development vaults after format changes.

## Build and test

Requirements: .NET SDK 10.0.302 with MAUI Android/iOS workloads, Android SDK, JDK 17 or newer, and a paired Mac with Xcode for device iOS builds.

```powershell
./scripts/validate-jdk.ps1
dotnet restore PassCrate.slnx --locked-mode
dotnet test tests\PassCrate.Tests\PassCrate.Tests.csproj --no-restore --configuration Release
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-android --no-restore --configuration Release -p:AndroidPackageFormat=aab
dotnet build src\PassCrate.App\PassCrate.App.csproj -f net10.0-ios --no-restore --configuration Release
```

Do not commit locally signed APK/AAB files. Produce store artifacts only with JDK 17+ and a protected production upload key. Complete [the release security checklist](docs/RELEASE_SECURITY_CHECKLIST.md) and an independent mobile security review before treating a build as production-ready.
