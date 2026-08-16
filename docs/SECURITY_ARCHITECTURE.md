# Security architecture

## Credential and device-key separation

PassCrate stores a local account password verifier and a separate six-digit vault PIN. Both use independently salted, versioned Argon2id parameters. The current V2 profile starts at 64 MiB, three iterations, parallelism two, and a 32-byte result; release-device benchmarking must keep unlock between roughly 350 and 750 ms and memory between 32 and 128 MiB.

Vault creation generates a random 256-bit data-encryption key (DEK). The PIN-derived key-encryption key wraps the DEK with AES-256-GCM, then a non-exportable/device-bound platform key protects that complete PIN envelope again:

- Android 13+ uses an Android Keystore AES key. The normal PIN key is device-only. Biometric unlock uses an auth-per-operation key, strong biometrics, `BiometricPrompt.CryptoObject`, and enrollment invalidation.
- iOS/iPadOS stores wrapper material with `WhenPasscodeSetThisDeviceOnly`. Biometric material uses `SecAccessControl` with `BiometryCurrentSet`.

A copied SQLite database therefore cannot be unlocked on another installation without the originating platform key. PassCrate never writes a raw Base64 DEK to MAUI SecureStorage. If the device key is lost or invalidated, local unlock fails closed and cloud restore is the recovery path.

Five failed PIN attempts start a persistent 30-second delay. Each subsequent failure doubles the delay up to 15 minutes. The counter and next-allowed timestamp are stored in both vault metadata and the device credential store, and the stricter value wins. Only authenticated DEK recovery resets the throttle.

## Record encryption and local storage

Secret documents are encrypted whole with AES-256-GCM and a fresh 96-bit nonce. Record ID, group ID, and encryption version are authenticated as associated data, so moving a secret decrypts and re-encrypts it. Temporary plaintext/key byte arrays are zeroed where practical; managed UI strings cannot be guaranteed to be immediately erased.

Secret values and notes are never searchable. Search covers group names, secret names, and field labels. Local searchable names and labels remain visible to an attacker who already extracted the database; cloud files encrypt all of this metadata.

SQLite is stored under Android `NoBackupFilesDir`. On iOS it is stored in Application Support and marked `NSURLIsExcludedFromBackupKey` after creation or replacement. Android cloud and device-transfer extraction rules exclude roots, files, databases, and shared preferences.

## Authentication lifecycle and biometrics

Password and PIN KDF parameters are upgraded after a successful authentication. Unsupported metadata fails closed. Biometric enrollment is opt-in and stores only a biometric-device-key-protected DEK envelope. Each biometric unlock is an authenticated cryptographic operation; changing biometric enrollment invalidates only that convenience envelope.

Backgrounding, inactivity, manual lock, and detected iOS capture lock the vault and clear the sensitive clipboard. The Auto-lock setting always locks on leaving the app; its 1-, 5-, and 15-minute options additionally lock an open app after inactivity. Android retains `FLAG_SECURE`. iOS observes both legacy capture notifications and live scene-capture trait changes and immediately covers sensitive content.

## Cloud recovery and V2 snapshots

Cloud sync is opt-in and has no PassCrate backend. A separate backup passphrase of at least 16 characters is checked against a bundled common-passphrase blocklist. Argon2id derives a recovery KEK that wraps the DEK. The passphrase is never stored.

`CloudVaultSnapshotV2` authenticates a minimal random vault/snapshot header and encrypts the complete state with the DEK. PassCrate writes V2 only; this pre-release application intentionally has no V1 migration path. Development vaults created with obsolete formats must be reset and recreated.

Record state is a set of immutable revisions. A revision ID hashes record kind/ID, canonical version vector, deletion state, and content fingerprint. Merge takes the union and removes causally dominated revisions, which makes it associative, commutative, and idempotent. Equal-vector/different-content, concurrent edits, and edit/delete siblings remain explicit conflicts. A resolution merges all sibling vectors and increments the resolving device, causally dominating every branch.

Group deletion tombstones retain presentation metadata for dependency review; secret deletion tombstones retain no old secret payload. An active secret can never materialize without a group. Conflict review supports selecting a revision, manual field/note merging, keep-both with re-encryption under new record IDs, deletion acceptance, group restoration, and moving secrets before final group deletion.

New filenames are `pv2-{HMAC-derived namespace}-{snapshot ID}.pvs`. Current-vault deletion uses that namespace plus the local provider catalog. A separately confirmed delete-all action removes every PassCrate `.pvs` file in the provider app folder.

## Resource and provider boundaries

Provider listing and download are bounded: 500 files, 100 MiB per snapshot, 512 MiB cumulative download, 100,000 records, JSON depth 64, 256 fields per secret, and 1 MiB serialized plaintext per secret. Limits are enforced while streaming rather than trusting provider-reported sizes. Corrupt, oversized, unauthenticated, or unparsable data never replaces healthy local state.

Google Drive is restricted to `drive.appdata`; Dropbox is restricted to App Folder access with OAuth PKCE. Provider tokens are device-only credentials and never enter snapshots. Sync occurs only while foregrounded and unlocked. The scheduler preserves single-flight sync, a five-second mutation debounce, periodic 15-minute attempts, reconnect state, quota handling, and bounded transient backoff.

Snapshots never contain the login account/verifier, PIN/KDF metadata, device or biometric keys, OAuth credentials, settings, or clipboard state.

## Reset, errors, and dependencies

A confirmed reset is non-cancellable: cancel sync, lock/clear the session, delete SQLite/WAL/SHM, delete platform keys and provider credentials, clear preferences/cache, then attempt provider revocation without allowing revocation failure to retain local data. A first-install sentinel purges surviving iOS Keychain/provider state before account or restore flows.

User-facing errors pass through a sanitizer. Provider response bodies, tokens, cryptographic material, and plaintext fields are not logged. Dependency restore uses lock files, NuGet auditing, explicit AndroidX alignment, and CI SBOM generation. Android packaging requires JDK 17 or newer.
