# PassCrate threat model

PassCrate is a local-first mobile vault with optional end-to-end encrypted provider sync. It does not claim complete protection on a rooted/jailbroken, malicious, or already-unlocked device.

| Threat | Primary mitigation | Residual risk |
|---|---|---|
| Lost device or copied database | AES-256-GCM secrets; random DEK; PIN-wrapped envelope protected again by a device-bound Keystore/Keychain key; automatic/background lock. | A live unlocked process, coercion, or compromised OS can expose data. Losing a local-only device key is unrecoverable. |
| PIN guessing | Argon2id V2, per-vault salt, persistent 30-second exponential delay after five failures (max 15 minutes), stricter database/device-store state. | A six-digit PIN has low entropy. Hardware/OS compromise can weaken online controls. |
| Password guessing | Independent salted Argon2id verifier, fixed-time comparison, successful-login KDF upgrade, 12-character minimum. | User-chosen passwords may be weak/reused; a verifier enables offline guessing. |
| Database tampering | AES-GCM authentication plus record ID/group/version associated data; invalid metadata fails closed. | Whole-record deletion or filesystem rollback is possible without a trusted anti-rollback service. |
| Backup/device transfer | Android NoBackup storage and API 33 extraction exclusions; iOS Application Support backup exclusion; ThisDeviceOnly credentials. | Vendor behavior must be verified on every release OS/device family. |
| Biometric bypass or enrollment change | Auth-per-operation Android CryptoObject or iOS BiometryCurrentSet; strong biometrics; enrollment invalidation; no raw Base64 DEK in SecureStorage. | Compromised biometric subsystem, OS, or live instrumentation is out of scope. |
| Clipboard/UI capture | Explicit copy, compare-before-clear timer, clear on lock/background; hidden values; Android FLAG_SECURE; live iOS capture cover/lock. | Another process may read before clearing; cameras, hostile keyboards/accessibility, or compromised display pipelines remain. |
| Memory inspection | Session-scoped DEK, short-lived key/plaintext buffers, zeroing where practical, sensitive view cleanup. | Managed strings and rendered UI buffers cannot be reliably zeroed. |
| Interrupted reset/reinstall | Non-cancellable ordered local destruction; Preferences sentinel repeats first-install credential/key/provider cleanup; revocation is best effort after local deletion. | Flash wear leveling prevents guaranteed physical erasure. |
| Cloud-provider disclosure | Entire V2 state encrypted with DEK; separate 16+ character blocked-list recovery passphrase; opaque HMAC namespace filenames; app-private provider scope. | Size/timing/count correlation and offline passphrase guessing remain possible. |
| Malicious cloud content | AES-GCM; immutable parent graph; bounded streaming/files/records/JSON; transactional validation before replacement; fallback to healthy authenticated snapshots. | A provider can withhold/delete data or present a consistent older history to an isolated device. |
| Concurrent devices | Revision-set union and causal dominance provide associative, commutative, idempotent convergence; explicit edit/delete and group dependency conflicts; resolving revision dominates all siblings. | Users must review semantic conflicts; retained deletion history grows. |
| OAuth token theft | Device-only credential storage, least-privilege provider folders, reconnect verification, reset/disconnect cleanup, sanitized HTTP errors. | A stolen valid token can read/delete opaque files and cause denial of service until revoked. |
| Cloud deletion mistake | Current namespace requires password plus `DELETE CLOUD VAULT`; delete-all separately reauthorizes and requires `DELETE ALL CLOUD VAULTS`. | Provider retention policies may retain deleted data; delete-all intentionally affects every PassCrate vault in that app folder. |
| Dependency/build compromise | Lock files, NuGet audit, explicit AndroidX version alignment, CI warning gates, JDK 17 validation, and SBOM artifact. | Registry, toolchain, signing-key, or CI compromise still requires independent controls. |

## Trust boundaries

Trusted while uncompromised: the PassCrate process, .NET cryptography, OS random generator, app sandbox, Android Keystore/iOS Keychain, and native authentication UI.

Untrusted: SQLite and sidecars, backups, clipboard consumers, logs, network transport, Google Drive, Dropbox, and every downloaded snapshot until authenticated and validated.

Out of scope for full prevention: root/jailbreak, malicious kernel/OS, physical side channels, coercion, provider rollback/withholding, and live process instrumentation while unlocked.

