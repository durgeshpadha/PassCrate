# Security architecture

## Credential and device-key separation

PassCrate uses one vault passphrase for local unlock and encrypted cloud recovery. It must contain at least 16 characters and must not match the bundled common-passphrase blocklist. There is no separate PassCrate account password, PIN, or recoverable verifier. The current Argon2id profile uses 64 MiB, three iterations, parallelism two, and a 32-byte result; release-device benchmarking must keep unlock between roughly 350 and 750 ms and memory between 32 and 128 MiB.

Vault creation generates a random 256-bit data-encryption key (DEK). A passphrase-derived key-encryption key wraps the DEK with AES-256-GCM, then a non-exportable/device-bound platform key protects that complete envelope again:

- Android 13+ uses an Android Keystore AES key. The normal vault wrapper is device-only. Biometric unlock uses an auth-per-operation key, strong biometrics, `BiometricPrompt.CryptoObject`, and enrollment invalidation.
- iOS/iPadOS stores wrapper material with `WhenPasscodeSetThisDeviceOnly`. Biometric material uses `SecAccessControl` with `BiometryCurrentSet`.

A copied SQLite database therefore cannot be unlocked on another installation without the originating platform key. PassCrate never writes a raw Base64 DEK to MAUI SecureStorage. If the device key is lost or invalidated, local unlock fails closed and cloud restore is the recovery path.

Five failed passphrase attempts start a persistent 30-second delay. Each subsequent failure doubles the delay up to 15 minutes. The counter and next-allowed timestamp are stored in both vault metadata and the device credential store, and the stricter value wins. Only authenticated DEK recovery resets the throttle.

## Record encryption and local storage

Secret documents are encrypted whole with AES-256-GCM and a fresh 96-bit nonce. Record ID, group ID, and encryption version are authenticated as associated data, so moving a secret decrypts and re-encrypts it. Temporary plaintext/key byte arrays are zeroed where practical; managed UI strings cannot be guaranteed to be immediately erased.

Secret values and notes are never searchable. Search covers group names, secret names, and field labels. Local searchable names and labels remain visible to an attacker who already extracted the database; cloud files encrypt all of this metadata.

SQLite is stored under Android `NoBackupFilesDir`. On iOS it is stored in Application Support and marked `NSURLIsExcludedFromBackupKey` after creation or replacement. Android cloud and device-transfer extraction rules exclude roots, files, databases, and shared preferences.

## Authentication lifecycle and biometrics

Passphrase KDF parameters are versioned and upgraded after successful authentication when required. Unsupported metadata fails closed. Biometric enrollment is opt-in and stores only a biometric-device-key-protected DEK envelope. Each biometric unlock is an authenticated cryptographic operation; changing biometric enrollment invalidates only that convenience envelope.

Backgrounding, inactivity, manual lock, and detected iOS capture lock the vault and clear the sensitive clipboard. The Auto-lock setting always locks on leaving the app; its 1-, 5-, and 15-minute options additionally lock an open app after inactivity. Android retains `FLAG_SECURE`. iOS observes both legacy capture notifications and live scene-capture trait changes and immediately covers sensitive content.

## Cloud recovery and V2 snapshots

Cloud sync is opt-in and has no PassCrate backend. Argon2id derives a recovery KEK from the current vault passphrase and wraps the DEK for cloud recovery. The passphrase is never stored or uploaded. Passphrase rotation verifies an old-recovery cloud checkpoint before atomically committing the new local metadata and a durable pending-recovery flag. The final new-recovery snapshot is then uploaded and verified; if that publish is interrupted, a later sync uses the pending local recovery envelope instead of adopting an older remote envelope. Restore selects the newest authoritative completed snapshot instead of accepting an obsolete passphrase through an older snapshot.

`CloudVaultSnapshotV2` authenticates a minimal random vault/snapshot header and encrypts the complete state with the DEK. PassCrate writes V2 only; this pre-release application intentionally has no V1 migration path. Development vaults created with obsolete formats must be reset and recreated.

Record state is a set of immutable revisions. A revision ID hashes record kind/ID, canonical version vector, deletion state, and content fingerprint. Merge takes the union and removes causally dominated revisions, which makes it associative, commutative, and idempotent. Equal-vector/different-content, concurrent edits, and edit/delete siblings remain explicit conflicts. A resolution merges all sibling vectors and increments the resolving device, causally dominating every branch.

Group deletion tombstones retain presentation metadata for dependency review; secret deletion tombstones retain no old secret payload. An active secret can never materialize without a group. Conflict review supports selecting a revision, manual field/note merging, keep-both with re-encryption under new record IDs, deletion acceptance, group restoration, and moving secrets before final group deletion.

New filenames are `pv2-{HMAC-derived namespace}-{snapshot ID}.pvs`. Current-vault deletion uses that namespace plus the local provider catalog. A separately confirmed delete-all action removes every PassCrate `.pvs` file in the provider app folder.

Routine synchronization retains the newest ten snapshots and does not prune older undominated concurrent heads required for convergence or conflict review. The provider listing boundary remains 500 files, so corrupt files, concurrent heads, interrupted cleanup, or multiple vault namespaces can make the physical file count higher than ten.

Before first enabling sync, PassCrate compares the local vault ID with vault IDs already in the authorized provider account. A mismatch cannot upload automatically. The user must explicitly choose to restore the cloud vault, replace the selected cloud vault, merge it into the local vault, or use another account. Restore fully authenticates/decrypts cloud state before local deletion and requires phone-owner verification. Replace and merge upload, download, authenticate, and decrypt the new snapshot before old-vault cleanup. Merge decrypts cloud secrets with the cloud DEK, re-encrypts imported payloads under the local DEK, and prefers the local record on an ID overlap. Cleanup failure retains the verified new backup and enters `CloudCleanupRequired` instead of rolling back to an unsafe state.

## Resource and provider boundaries

Provider listing and download are bounded: 500 files, 100 MiB per snapshot, 512 MiB cumulative download, 100,000 records, JSON depth 64, 256 fields per secret, and 1 MiB serialized plaintext per secret. Limits are enforced while streaming rather than trusting provider-reported sizes. Corrupt, oversized, unauthenticated, or unparsable data never replaces healthy local state.

Google Drive is restricted to `drive.appdata`; Dropbox is restricted to App Folder access with OAuth PKCE. Provider tokens are device-only credentials and never enter snapshots. Sync occurs only while foregrounded and unlocked. The scheduler preserves single-flight sync, a five-second mutation debounce, periodic 15-minute attempts, reconnect state, quota handling, and bounded transient backoff.

Snapshots never contain local passphrase KDF metadata, the device-wrapped local DEK, failed-attempt state, device or biometric keys, OAuth credentials, settings, or clipboard state.

## Reset, errors, and dependencies

A reset requires exact `RESET` text plus operating-system phone-owner authentication and is non-cancellable once deletion starts: cancel sync, lock/clear the session, clear the clipboard, delete SQLite/WAL/SHM, delete platform keys and provider credentials, clear preferences/cache, then attempt provider revocation without allowing revocation failure to retain local data. Cloud files are not deleted by local reset. A first-install sentinel purges surviving iOS Keychain/provider state before account or restore flows.

Create Vault and Restore from Cloud are guarded by one versioned first-use acceptance policy. The local Preferences receipt records only the accepted Terms version, Privacy version, app version, and UTC time. It grants no vault access, is never synchronized, and is cleared by reset. Legal and Help pages remain available while locked and have no vault-service dependencies.

User-facing errors pass through a sanitizer. Provider response bodies, tokens, cryptographic material, and plaintext fields are not logged. Dependency restore uses lock files, NuGet auditing, explicit AndroidX alignment, and CI SBOM generation. Android packaging requires JDK 17 or newer.
