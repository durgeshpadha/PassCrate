# Backup and platform behavior

PassCrate does not use OS backup or device-to-device transfer for vault migration. End-to-end encrypted Google Drive or Dropbox restore is the only supported cross-device path.

## Android 13 and newer

- Android API 33 is the minimum supported version.
- SQLite is stored under `NoBackupFilesDir`.
- `android:allowBackup="false"` and Android 13+ `dataExtractionRules` exclude roots, files, databases, shared preferences, and external files from cloud backup and device transfer.
- The PIN envelope is protected by a non-exportable Android Keystore AES key.
- Biometric unlock uses a strong-biometric auth-per-operation Keystore key and `BiometricPrompt.CryptoObject`; enrollment changes invalidate that key.
- `FLAG_SECURE` blocks normal screenshots and recent-app thumbnails, subject to OS/vendor limitations.
- Google Drive authorization uses Google Identity `AuthorizationClient` with only `drive.appdata`. Dropbox uses OAuth authorization code with PKCE.

Android 11 and 12 are not supported and have no compatibility backup rules.

## iOS and iPadOS

- SQLite is stored in Application Support and `NSURLIsExcludedFromBackupKey` is reapplied after creation or replacement.
- Device wrapper and provider credentials use ThisDeviceOnly Keychain accessibility. Biometric material adds `BiometryCurrentSet`.
- A first-install Preferences sentinel clears PassCrate Keychain entries and provider state that may have survived uninstall before any account or restore flow.
- A privacy cover is shown while inactive. Live scene-capture changes and legacy screen-capture notifications immediately cover and lock the vault.
- Google Drive uses Google Sign-In with `drive.appdata`; Dropbox uses OAuth authorization code with PKCE.

## Restore and deletion consequences

Without cloud sync, reinstalling PassCrate, clearing app data, or losing the device permanently loses the vault. With sync enabled, a new installation authorizes the same provider, enters the backup passphrase, creates a new local password and PIN, and re-wraps the recovered DEK with the new device key. Prior local login/PIN credentials are deliberately not synchronized.

`Reset PassCrate` erases only the current device and local provider credentials. `Delete cloud vault` requires the local password plus `DELETE CLOUD VAULT` and removes the current opaque namespace. `Delete all PassCrate cloud data` additionally requires provider authorization and `DELETE ALL CLOUD VAULTS`; it affects every PassCrate vault in that provider app folder. Dropbox deletion means removal from the app folder and does not override Dropbox retention policy.

Keystore, Keychain, backup, screenshot/capture, OAuth, reset, and reinstall behavior remain mandatory physical-device release tests.
