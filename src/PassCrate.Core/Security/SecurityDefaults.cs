using System.Security.Cryptography;
using PassCrate.Core.Models;

namespace PassCrate.Core.Security;

public static class SecurityDefaults
{
    public const int SaltSize = 16;
    public const int KeySize = 32;
    public const int GcmNonceSize = 12;
    public const int GcmTagSize = 16;
    public const int MinimumVaultPassphraseLength = 16;

    // Conservative cross-platform starting point. These values remain versioned
    // and must be tuned with release-device benchmarks before store submission.
    public static KeyDerivationParameters CreateArgon2IdParameters() => new()
    {
        Algorithm = SecurityAlgorithms.Argon2Id,
        Version = 1,
        Salt = RandomNumberGenerator.GetBytes(SaltSize),
        MemorySizeKiB = 32 * 1024,
        Iterations = 3,
        Parallelism = 2,
        KeyLength = KeySize,
    };

    public static KeyDerivationParameters CreateArgon2IdV2Parameters() => new()
    {
        Algorithm = SecurityAlgorithms.Argon2Id,
        Version = 2,
        Salt = RandomNumberGenerator.GetBytes(SaltSize),
        MemorySizeKiB = 64 * 1024,
        Iterations = 3,
        Parallelism = 2,
        KeyLength = KeySize,
    };

    public static KeyDerivationParameters CreatePbkdf2Parameters() => new()
    {
        Algorithm = SecurityAlgorithms.Pbkdf2Sha256,
        Version = 1,
        Salt = RandomNumberGenerator.GetBytes(SaltSize),
        Iterations = 600_000,
        Parallelism = 1,
        KeyLength = KeySize,
    };
}

public static class CredentialPolicy
{
    private static readonly Lazy<HashSet<string>> CommonVaultPassphrases = new(LoadCommonVaultPassphrases);
    public static void ValidateVaultPassphrase(string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new CredentialValidationException("Enter your vault passphrase.");
        }

        if (passphrase.Trim().Length < SecurityDefaults.MinimumVaultPassphraseLength)
        {
            throw new CredentialValidationException(
                $"Vault passphrase must contain at least {SecurityDefaults.MinimumVaultPassphraseLength} characters.");
        }

        if (CommonVaultPassphrases.Value.Contains(passphrase.Trim()))
        {
            throw new CredentialValidationException("Choose a less common vault passphrase.");
        }
    }

    private static HashSet<string> LoadCommonVaultPassphrases()
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(CredentialPolicy).Assembly.GetManifestResourceStream(
            "PassCrate.Core.Resources.common-backup-passphrases.txt")
            ?? throw new InvalidOperationException("The backup-passphrase blocklist is missing.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length > 0)
            {
                values.Add(value);
            }
        }

        return values;
    }
}
