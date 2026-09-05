namespace PassCrate.Core.Legal;

public sealed record LegalAcceptanceReceipt(
    string TermsVersion,
    string PrivacyVersion,
    string AppVersion,
    DateTimeOffset AcceptedAt);

public sealed record LegalDocumentSection(string Heading, string Body);

public sealed record LegalDocument(
    string Id,
    string Title,
    string Version,
    string EffectiveDate,
    string Introduction,
    IReadOnlyList<LegalDocumentSection> Sections);

public static class LegalAcceptancePolicy
{
    public const string CurrentTermsVersion = "1.0";
    public const string CurrentPrivacyVersion = "1.0";
    public const string CreateDestination = "create";
    public const string RestoreDestination = "restore";

    public static bool IsCurrent(LegalAcceptanceReceipt? receipt) =>
        receipt is not null &&
        string.Equals(receipt.TermsVersion, CurrentTermsVersion, StringComparison.Ordinal) &&
        string.Equals(receipt.PrivacyVersion, CurrentPrivacyVersion, StringComparison.Ordinal);

    public static string? GetDestinationForRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return null;
        }

        if (route.Contains("//register", StringComparison.OrdinalIgnoreCase) ||
            route.Contains("RegistrationPage", StringComparison.OrdinalIgnoreCase))
        {
            return CreateDestination;
        }

        return route.Contains("CloudRestorePage", StringComparison.OrdinalIgnoreCase)
            ? RestoreDestination
            : null;
    }

    public static string NormalizeDestination(string? destination) =>
        string.Equals(destination, RestoreDestination, StringComparison.OrdinalIgnoreCase)
            ? RestoreDestination
            : CreateDestination;
}

public static class LegalDocuments
{
    public const string TermsId = "terms";
    public const string PrivacyId = "privacy";

    public static LegalDocument Terms { get; } = new(
        TermsId,
        "Terms of Use",
        LegalAcceptancePolicy.CurrentTermsVersion,
        "5 September 2026",
        "These terms explain the conditions for using PassCrate. They apply to the maximum extent permitted by applicable law.",
        [
            new("Using PassCrate", "You may use PassCrate to store information you are legally entitled to possess. You are responsible for complying with laws, workplace rules, and third-party agreements that apply to your data and devices."),
            new("Your passphrase and backups", "PassCrate cannot retrieve a forgotten vault passphrase. You are responsible for remembering it and for maintaining and periodically testing a usable encrypted backup. Resetting PassCrate, uninstalling it, clearing app data, losing a device, or relying on an incomplete synchronization can permanently remove access to data."),
            new("Cloud services", "Cloud synchronization is optional and connects directly to the Google Drive or Dropbox account you choose. Those services are operated by third parties under their own terms and may be unavailable, changed, suspended, or affected by account and network problems."),
            new("No warranty", "PassCrate is provided “as is” and “as available,” without a promise that it will always be available, uninterrupted, error-free, secure against every threat, or suitable for a particular purpose. No software can guarantee prevention of every data-loss or security event."),
            new("Limitation of liability", "To the maximum extent permitted by applicable law, the provider of PassCrate is not liable for indirect, incidental, special, consequential, or loss-of-data damages arising from use of or inability to use the application. This limitation does not exclude any warranty, remedy, right, or liability that applicable law does not permit to be excluded or limited."),
            new("Changes", "Material changes to these terms or the privacy policy require acceptance of a new version before creating or restoring a vault. The version accepted on this device is stored locally."),
        ]);

    public static LegalDocument Privacy { get; } = new(
        PrivacyId,
        "Privacy Policy",
        LegalAcceptancePolicy.CurrentPrivacyVersion,
        "5 September 2026",
        "This policy describes how the current PassCrate application handles information on the device and through optional cloud synchronization.",
        [
            new("Local-first operation", "PassCrate has no application backend, advertising, analytics, or activity tracking. Vault data is stored on the device. Secret values and private notes are encrypted; local group names, secret names, and field labels remain searchable metadata inside the protected application database."),
            new("Optional cloud synchronization", "When you enable cloud synchronization or restore, PassCrate sends encrypted snapshot files directly to the Google Drive or Dropbox account you authorize. The selected provider processes account authorization, network identifiers, and stored files under its own privacy policy. PassCrate does not upload the vault passphrase."),
            new("Device security features", "Operating-system services handle device-owner and biometric authentication. PassCrate receives only the success, cancellation, or availability result—not your fingerprint, face template, phone PIN, or phone password. Provider credentials and device-key material are kept in platform-protected storage."),
            new("Clipboard", "Copying a secret or note places plaintext on the operating-system clipboard, where other permitted applications may read it. PassCrate attempts to clear only the value it placed there after the configured interval and whenever the vault locks."),
            new("Retention and deletion", "Reset PassCrate deletes the local vault, local settings, device keys, and connected-provider credentials, but it does not delete existing encrypted cloud files. The Cloud Sync page provides separately confirmed controls for deleting the current cloud vault or all PassCrate files from the connected provider, subject to provider retention behavior."),
            new("Policy availability", "This offline copy is available before vault creation or restoration. A public privacy-policy URL and accurate app-store data disclosures must also be maintained for distributed builds."),
        ]);

    public static LegalDocument Get(string? id) =>
        string.Equals(id, PrivacyId, StringComparison.OrdinalIgnoreCase) ? Privacy : Terms;
}
