using System.Security.Cryptography;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.Infrastructure.Encryption;

public sealed class AesGcmEncryptionService : IEncryptionService
{
    public EncryptedPayload Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default)
    {
        ValidateKey(key);
        var nonce = RandomNumberGenerator.GetBytes(SecurityDefaults.GcmNonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[SecurityDefaults.GcmTagSize];

        using var aes = new AesGcm(key, SecurityDefaults.GcmTagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return new EncryptedPayload
        {
            Version = 1,
            Algorithm = SecurityAlgorithms.Aes256Gcm,
            Nonce = nonce,
            Ciphertext = ciphertext,
            AuthenticationTag = tag,
        };
    }

    public byte[] Decrypt(
        EncryptedPayload payload,
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> associatedData = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ValidateKey(key);
        if (payload.Version != 1 ||
            payload.Algorithm != SecurityAlgorithms.Aes256Gcm ||
            payload.Nonce.Length != SecurityDefaults.GcmNonceSize ||
            payload.AuthenticationTag.Length != SecurityDefaults.GcmTagSize)
        {
            throw new CryptographicException("Encrypted payload metadata is invalid.");
        }

        var plaintext = new byte[payload.Ciphertext.Length];
        using var aes = new AesGcm(key, SecurityDefaults.GcmTagSize);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.AuthenticationTag, plaintext, associatedData);
        return plaintext;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != SecurityDefaults.KeySize)
        {
            throw new ArgumentException("AES-256 requires a 256-bit key.", nameof(key));
        }
    }
}

