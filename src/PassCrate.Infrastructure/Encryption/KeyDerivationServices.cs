using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Encryption;

public sealed class KeyDerivationService : IKeyDerivationService
{
    public async Task<byte[]> DeriveKeyAsync(
        string secret,
        byte[] salt,
        KeyDerivationParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(parameters);
        Validate(salt, parameters);

        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.Algorithm.Equals(SecurityAlgorithms.Pbkdf2Sha256, StringComparison.Ordinal))
        {
            return await Task.Run(
                () => Rfc2898DeriveBytes.Pbkdf2(
                    secret,
                    salt,
                    parameters.Iterations,
                    HashAlgorithmName.SHA256,
                    parameters.KeyLength),
                cancellationToken).ConfigureAwait(false);
        }

        if (!parameters.Algorithm.Equals(SecurityAlgorithms.Argon2Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported key derivation algorithm.", nameof(parameters));
        }

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var argon2 = new Argon2id(secretBytes)
            {
                Salt = salt.ToArray(),
                DegreeOfParallelism = parameters.Parallelism,
                Iterations = parameters.Iterations,
                MemorySize = parameters.MemorySizeKiB,
            };

            cancellationToken.ThrowIfCancellationRequested();
            return await argon2.GetBytesAsync(parameters.KeyLength).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static void Validate(byte[] salt, KeyDerivationParameters parameters)
    {
        if (parameters.Version is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Unsupported KDF parameter version.");
        }

        if (salt.Length < SecurityDefaults.SaltSize ||
            !CryptographicOperations.FixedTimeEquals(salt, parameters.Salt))
        {
            throw new ArgumentException("KDF salt is invalid.", nameof(salt));
        }

        if (parameters.KeyLength is < 16 or > 64 ||
            parameters.Iterations is < 1 or > 10_000_000 ||
            parameters.Parallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "KDF parameters are invalid.");
        }

        if (parameters.Algorithm == SecurityAlgorithms.Argon2Id &&
            (parameters.MemorySizeKiB < 8 * 1024 || parameters.MemorySizeKiB > 128 * 1024 || parameters.Iterations > 10))
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "Argon2 parameters are outside the supported safety range.");
        }

        if (parameters.Algorithm == SecurityAlgorithms.Pbkdf2Sha256 && parameters.Iterations < 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), "PBKDF2 iteration count is below the supported minimum.");
        }
    }
}

public sealed class DefaultKeyDerivationParameterProvider : IKeyDerivationParameterProvider
{
    public KeyDerivationParameters CreateVaultPassphraseParameters() => SecurityDefaults.CreateArgon2IdV2Parameters();

    public KeyDerivationParameters CreateCloudRecoveryParameters() => SecurityDefaults.CreateArgon2IdV2Parameters();

    public bool NeedsUpgrade(KeyDerivationParameters parameters) =>
        parameters.Algorithm != SecurityAlgorithms.Argon2Id ||
        parameters.Version < 2 ||
        parameters.MemorySizeKiB != 64 * 1024 ||
        parameters.Iterations != 3 ||
        parameters.Parallelism != 2 ||
        parameters.KeyLength != SecurityDefaults.KeySize;
}
