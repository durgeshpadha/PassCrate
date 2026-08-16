namespace PassCrate.Core.Models;

public static class SecurityAlgorithms
{
    public const string Argon2Id = "ARGON2ID";
    public const string Pbkdf2Sha256 = "PBKDF2-HMAC-SHA256";
    public const string Aes256Gcm = "AES-256-GCM";
}

public sealed record KeyDerivationParameters
{
    public required string Algorithm { get; init; }
    public int Version { get; init; } = 1;
    public required byte[] Salt { get; init; }
    public int MemorySizeKiB { get; init; }
    public int Iterations { get; init; }
    public int Parallelism { get; init; } = 1;
    public int KeyLength { get; init; } = 32;
}

public sealed record EncryptedPayload
{
    public int Version { get; init; } = 1;
    public string Algorithm { get; init; } = SecurityAlgorithms.Aes256Gcm;
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] AuthenticationTag { get; init; }
}

public enum DeviceKeyProtectionPolicy
{
    DeviceOnly,
    BiometricCurrentSet,
}

public sealed record DeviceProtectedPayload
{
    public int Version { get; init; } = 1;
    public required DeviceKeyProtectionPolicy Policy { get; init; }
    public required string KeyAlias { get; init; }
    public required EncryptedPayload Payload { get; init; }
}

public sealed record VaultMetadataV3
{
    public int Version { get; init; } = 3;
    public required KeyDerivationParameters PassphraseKdf { get; init; }
    public required DeviceProtectedPayload DeviceProtectedWrappedDataEncryptionKey { get; init; }
    public required string PlatformKeyAlias { get; init; }
    public int FailedUnlockAttempts { get; init; }
    public DateTimeOffset? LastFailedUnlockAt { get; init; }
    public DateTimeOffset? NextUnlockAllowedAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record UnlockThrottleState
{
    public int FailedAttempts { get; init; }
    public DateTimeOffset? LastFailedAt { get; init; }
    public DateTimeOffset? NextAllowedAt { get; init; }
}
