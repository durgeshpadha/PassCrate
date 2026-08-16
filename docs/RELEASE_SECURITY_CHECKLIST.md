# Release security checklist

- [ ] Install and validate JDK 17+ with `scripts/validate-jdk.ps1`; reject Java 11 packaging.
- [ ] Benchmark Argon2id V2 on the slowest supported Android 13+ and iOS 15+ release devices; target 350–750 ms and 32–128 MiB.
- [ ] Run all automated tests and Android/iOS managed Release builds with zero warnings.
- [ ] Confirm locked NuGet restore, full vulnerability audit, and generated SBOM contain no vulnerable runtime dependency.
- [ ] Test PIN delay progression/persistence, password/PIN KDF upgrades, missing device keys, biometric per-operation auth, and enrollment invalidation.
- [ ] Fault-inject every reset stage and confirm the next first-launch cleanup completes destruction.
- [ ] Verify Android NoBackup storage/data-extraction rules and iOS Application Support backup exclusion after database replacement.
- [ ] Verify first-install cleanup removes surviving Keychain, Dropbox, and Google authorization state.
- [ ] Test clipboard replacement/timeout/navigation/background/lock behavior and sensitive-page state clearing.
- [ ] Confirm Android screenshots/recents are blocked and iOS live capture immediately covers and locks an already-active app.
- [ ] Scan final SQLite, preferences, cache, logs, binaries, snapshots, and provider requests with plaintext canaries.
- [ ] Confirm provider exceptions and UI errors contain no response bodies, tokens, credentials, cryptographic data, or decrypted fields.
- [ ] Test V2 merge associativity, commutativity, idempotence, multi-head permutations, stale devices, edit/delete, group dependencies, and repeated resolution.
- [ ] Exercise keep-selected, manual field/note merge, keep-both, accept deletion, restore group, and move-then-delete conflict flows.
- [ ] Test snapshot file/count/cumulative/record/field/plaintext/JSON-depth limits and mid-stream overrun.
- [ ] Test reconnect without changing vault/recovery/device identity, provider account mismatch, revoked access, quota, retry-after, timeout, offline, and Wi-Fi policy.
- [ ] Test namespace-scoped deletion, corrupt namespace files, local catalog IDs, and separately confirmed delete-all behavior.
- [ ] Exercise real two-device and three-device convergence for both Google Drive and Dropbox.
- [ ] Configure production Google Android/iOS clients and Dropbox App Folder credentials without committing secrets.
- [ ] Complete accessibility, keyboard, orientation, phone/tablet, and light/dark/system-theme validation.
- [ ] Generate and protect signing assets outside source control.
- [ ] Complete an independent mobile security review and staged rollout before store submission.
- [ ] Document rooted/jailbroken-device, provider rollback/withholding, managed-memory, coercion, and provider-retention residual risks.
- [ ] Do not label PassCrate “unhackable” or promise guaranteed recovery or physical erasure.

