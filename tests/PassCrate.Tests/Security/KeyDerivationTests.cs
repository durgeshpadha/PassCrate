using System.Security.Cryptography;
using PassCrate.Core.Models;
using PassCrate.Infrastructure.Encryption;

namespace PassCrate.Tests.Security;

public sealed class KeyDerivationTests
{
    private readonly KeyDerivationService _service = new();

    [Fact]
    public async Task Pbkdf2_DerivesDeterministically_AndSaltChangesOutput()
    {
        var firstParameters = CreatePbkdf2(RandomNumberGenerator.GetBytes(16));
        var secondParameters = CreatePbkdf2(RandomNumberGenerator.GetBytes(16));

        var first = await _service.DeriveKeyAsync("correct horse battery staple", firstParameters.Salt, firstParameters);
        var same = await _service.DeriveKeyAsync("correct horse battery staple", firstParameters.Salt, firstParameters);
        var differentSalt = await _service.DeriveKeyAsync("correct horse battery staple", secondParameters.Salt, secondParameters);

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentSalt);
    }

    [Fact]
    public async Task Argon2Id_DerivesA256BitKey()
    {
        var parameters = new KeyDerivationParameters
        {
            Algorithm = SecurityAlgorithms.Argon2Id,
            Version = 1,
            Salt = RandomNumberGenerator.GetBytes(16),
            MemorySizeKiB = 8 * 1024,
            Iterations = 1,
            Parallelism = 1,
            KeyLength = 32,
        };

        var result = await _service.DeriveKeyAsync("DemoPassword!2026", parameters.Salt, parameters);
        Assert.Equal(32, result.Length);
    }

    [Fact]
    public async Task InvalidVersion_IsRejected()
    {
        var parameters = CreatePbkdf2(RandomNumberGenerator.GetBytes(16)) with { Version = 99 };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _service.DeriveKeyAsync("DemoPassword!2026", parameters.Salt, parameters));
    }

    [Fact]
    public async Task MismatchedSalt_IsRejected()
    {
        var parameters = CreatePbkdf2(RandomNumberGenerator.GetBytes(16));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.DeriveKeyAsync("DemoPassword!2026", RandomNumberGenerator.GetBytes(16), parameters));
    }

    private static KeyDerivationParameters CreatePbkdf2(byte[] salt) => new()
    {
        Algorithm = SecurityAlgorithms.Pbkdf2Sha256,
        Version = 1,
        Salt = salt,
        Iterations = 100_000,
        Parallelism = 1,
        KeyLength = 32,
    };
}
