using System.Security.Cryptography;
using System.Text;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Encryption;

namespace PassCrate.Tests.Security;

public sealed class EncryptionTests
{
    private readonly AesGcmEncryptionService _encryption = new();

    [Theory]
    [InlineData("")]
    [InlineData("PassCrate private by design")]
    [InlineData("नमस्ते · 密碼 · 🔐")]
    public void EncryptDecrypt_RoundTrips(string value)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var payload = _encryption.Encrypt(plaintext, key);
        var decrypted = _encryption.Decrypt(payload, key);

        Assert.Equal(plaintext, decrypted);
        if (plaintext.Length > 0)
        {
            Assert.NotEqual(plaintext, payload.Ciphertext);
        }
    }

    [Fact]
    public void Encrypt_UsesUniqueNonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = Encoding.UTF8.GetBytes("same input");
        var first = _encryption.Encrypt(plaintext, key);
        var second = _encryption.Encrypt(plaintext, key);

        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
    }

    [Fact]
    public void Decrypt_WithWrongKey_FailsAuthentication()
    {
        var payload = _encryption.Encrypt("secret"u8, RandomNumberGenerator.GetBytes(32));
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            _encryption.Decrypt(payload, RandomNumberGenerator.GetBytes(32)));
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("tag")]
    [InlineData("nonce")]
    public void Decrypt_WhenPayloadModified_FailsAuthentication(string part)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = _encryption.Encrypt("sensitive value"u8, key);
        var modified = payload with
        {
            Ciphertext = payload.Ciphertext.ToArray(),
            AuthenticationTag = payload.AuthenticationTag.ToArray(),
            Nonce = payload.Nonce.ToArray(),
        };

        if (part == "ciphertext") modified.Ciphertext[0] ^= 0x01;
        if (part == "tag") modified.AuthenticationTag[0] ^= 0x01;
        if (part == "nonce") modified.Nonce[0] ^= 0x01;

        Assert.Throws<AuthenticationTagMismatchException>(() => _encryption.Decrypt(modified, key));
    }

    [Fact]
    public void EncryptDecrypt_HandlesLargePayload()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plaintext = RandomNumberGenerator.GetBytes(1_048_576);
        var payload = _encryption.Encrypt(plaintext, key, "record-metadata"u8);
        Assert.Equal(plaintext, _encryption.Decrypt(payload, key, "record-metadata"u8));
    }
}
