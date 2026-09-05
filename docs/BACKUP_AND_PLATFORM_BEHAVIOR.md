# Backup and platform behavior

PassCrate does not use OS backup or device-to-device transfer for vault migration. End-to-end encrypted Google Drive or Dropbox restore is the only supported cross-device path.

## Android 13 and newer

- Android API 33 is the minimum supported version.
- SQLite is stored under `NoBackupFilesDir`.
- `android:allowBackup="false"` and Android 13+ `dataExtractionRules` exclude roots, files, databases, shared preferences, and external files from cloud backup and device transfer.
- The passphrase-wrapped DEK envelope is protected by a non-exportable Android Keystore AES key.
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

Without cloud sync, reinstalling PassCrate, clearing app data, or losing the device permanently loses the vault. With sync enabled, a new installation authorizes the same provider and account, selects a readable backup entry, and enters the vault passphrase that protects that backup. PassCrate authenticates and decrypts the newest authoritative recovery snapshot, restores the vault, and re-wraps the recovered DEK with a new device key. Device keys, biometric material, failed-attempt state, and app settings are deliberately not synchronized.

Before either first-time creation or restore, the user must accept the current offline Terms of Use, Privacy Policy, and data-loss warning. The versioned acceptance receipt is local only, contains no vault data, is not synchronized, and is erased by Reset PassCrate. Declining or cancelling returns to Welcome without changing local or cloud data.

Changing the vault passphrase while cloud sync is enabled first verifies a checkpoint, commits the new local passphrase with a durable pending-cloud marker, and then publishes the new recovery information. If the last publish is interrupted, the next normal sync retries it using the new recovery envelope. Until Cloud Sync reports Ready, keep the former passphrase for emergency recovery from the last completed cloud snapshot. After the new recovery snapshot is verified, restore does not fall back to an older snapshot merely because an old passphrase can decrypt it. Normal synchronization keeps the newest ten snapshots, plus any older undominated concurrent heads that are still needed for convergence or conflict review.

## A different vault in the selected cloud account

PassCrate compares opaque vault identifiers before enabling sync. If the account already contains another vault, neither side is overwritten automatically. A two-step resolution screen first shows all four outcomes together with no preselected action, then shows only the fields and confirmations for the selected outcome. The newest readable cloud-backup entry is selected by default, and the user chooses one of four paths:

1. **Combine both vaults (recommended):** verify both passphrases, preview counts, re-encrypt cloud-only secret payloads under the local DEK, and keep the local version for record-ID overlaps. The merged snapshot is verified before the selected old-vault snapshots are removed.
2. **Use the cloud vault:** authenticate and decrypt it first, show its group/secret count, require phone-owner verification, and only then replace this device's local vault. A wrong cloud passphrase leaves the local vault unchanged.
3. **Use this phone's vault:** verify the local passphrase and phone owner, upload and download-verify the new local-vault snapshot, commit it, and only then delete every snapshot for the selected old vault. If deletion fails, the verified new backup remains and the UI reports cleanup is required.
4. **Choose another cloud account:** revoke/disconnect the current account on this device without deleting either vault or any cloud file.

Network, decryption, merge, upload, restore, and disconnect work displays a centered operation-specific progress overlay. The overlay remains visible regardless of scroll position, blocks all page interaction and in-app Back navigation, and does not report a fabricated percentage.

`Reset PassCrate` requires exact `RESET` confirmation and operating-system phone-owner verification. It erases only this device's vault, device keys, provider credentials, preferences, and cache; existing encrypted cloud files remain. `Delete cloud vault` requires the local passphrase plus `DELETE CLOUD VAULT` and removes the current opaque namespace. `Delete all PassCrate cloud data` additionally requires provider authorization and `DELETE ALL CLOUD VAULTS`; it affects every PassCrate vault in that provider app folder. Dropbox deletion means removal from the app folder and does not override Dropbox retention policy.

Keystore, Keychain, backup, screenshot/capture, OAuth, reset, and reinstall behavior remain mandatory physical-device release tests.
