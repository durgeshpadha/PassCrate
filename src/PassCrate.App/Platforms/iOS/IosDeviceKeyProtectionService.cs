using System.Security.Cryptography;
using System.Text;
using Foundation;
using LocalAuthentication;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;
using Security;

namespace PassCrate.App;

public sealed class IosDeviceKeyProtectionService : IDeviceKeyProtectionService
{
    private const string KeychainService = "com.passcrate.devicekeys";

    public Task<DeviceProtectedPayload> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        DeviceKeyProtectionPolicy policy,
        string keyAlias,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetOrCreateKey(keyAlias, policy, prompt: policy == DeviceKeyProtectionPolicy.BiometricCurrentSet);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(SecurityDefaults.GcmNonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[SecurityDefaults.GcmTagSize];
            using var aes = new AesGcm(key, SecurityDefaults.GcmTagSize);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, Encoding.UTF8.GetBytes(keyAlias));
            return Task.FromResult(new DeviceProtectedPayload
            {
                Policy = policy,
                KeyAlias = keyAlias,
                Payload = new EncryptedPayload
                {
                    Nonce = nonce,
                    Ciphertext = ciphertext,
                    AuthenticationTag = tag,
                },
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task<byte[]> UnprotectAsync(
        DeviceProtectedPayload protectedPayload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ReadKey(
            protectedPayload.KeyAlias,
            prompt: protectedPayload.Policy == DeviceKeyProtectionPolicy.BiometricCurrentSet)
            ?? throw new DeviceKeyUnavailableException();
        try
        {
            var plaintext = new byte[protectedPayload.Payload.Ciphertext.Length];
            using var aes = new AesGcm(key, SecurityDefaults.GcmTagSize);
            aes.Decrypt(
                protectedPayload.Payload.Nonce,
                protectedPayload.Payload.Ciphertext,
                protectedPayload.Payload.AuthenticationTag,
                plaintext,
                Encoding.UTF8.GetBytes(protectedPayload.KeyAlias));
            return Task.FromResult(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public Task DeleteKeyAsync(string keyAlias, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = CreateQuery(keyAlias);
        _ = SecKeyChain.Remove(query);
        return Task.CompletedTask;
    }

    public Task DeleteAllKeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = KeychainService,
        };
        _ = SecKeyChain.Remove(query);
        return Task.CompletedTask;
    }

    private static byte[] GetOrCreateKey(
        string alias,
        DeviceKeyProtectionPolicy policy,
        bool prompt)
    {
        var existing = ReadKey(alias, prompt);
        if (existing is not null)
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(SecurityDefaults.KeySize);
        using var record = CreateQuery(alias);
        record.ValueData = NSData.FromArray(key);
        if (policy == DeviceKeyProtectionPolicy.BiometricCurrentSet)
        {
            record.AccessControl = new SecAccessControl(
                SecAccessible.WhenPasscodeSetThisDeviceOnly,
                SecAccessControlCreateFlags.BiometryCurrentSet);
        }
        else
        {
            record.Accessible = SecAccessible.WhenPasscodeSetThisDeviceOnly;
        }

        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.Success)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException($"iOS Keychain rejected the device key ({(int)status}).");
        }

        if (policy == DeviceKeyProtectionPolicy.BiometricCurrentSet)
        {
            CryptographicOperations.ZeroMemory(key);
            var authenticated = ReadKey(alias, prompt: true);
            if (authenticated is null)
            {
                using var query = CreateQuery(alias);
                _ = SecKeyChain.Remove(query);
                throw new OperationCanceledException("Biometric authentication was cancelled.");
            }

            return authenticated;
        }

        return key;
    }

    private static byte[]? ReadKey(string alias, bool prompt)
    {
        using var query = CreateQuery(alias);
        using var context = prompt ? new LAContext { LocalizedReason = "Unlock PassCrate" } : null;
        if (prompt)
        {
            query.AuthenticationContext = context;
        }

        var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status is SecStatusCode.ItemNotFound or SecStatusCode.AuthFailed or SecStatusCode.UserCanceled)
        {
            return null;
        }

        if (status != SecStatusCode.Success || data is null)
        {
            throw new CryptographicException($"iOS Keychain could not access the device key ({(int)status}).");
        }

        using (data)
        {
            return data.ToArray();
        }
    }

    private static SecRecord CreateQuery(string alias) => new(SecKind.GenericPassword)
    {
        Service = KeychainService,
        Account = alias,
    };
}
