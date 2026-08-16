using System.Security.Cryptography;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;
using PassCrate.Core.Security;

namespace PassCrate.App;

public sealed class AndroidDeviceKeyProtectionService : IDeviceKeyProtectionService
{
    private const string KeyStoreName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";

    public async Task<DeviceProtectedPayload> ProtectAsync(
        ReadOnlyMemory<byte> plaintext,
        DeviceKeyProtectionPolicy policy,
        string keyAlias,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetOrCreateKey(keyAlias, policy);
        using var cipher = Cipher.GetInstance(Transformation)
            ?? throw new CryptographicException("AES-GCM is unavailable.");
        cipher.Init(Javax.Crypto.CipherMode.EncryptMode, key);
        var authenticatedCipher = policy == DeviceKeyProtectionPolicy.BiometricCurrentSet
            ? await AuthenticateAsync(cipher, "Protect PassCrate biometric unlock", cancellationToken)
            : cipher;
        var output = authenticatedCipher.DoFinal(plaintext.ToArray())
            ?? throw new CryptographicException("Device key encryption failed.");
        if (output.Length < SecurityDefaults.GcmTagSize)
        {
            throw new CryptographicException("Device key encryption output is invalid.");
        }

        return new DeviceProtectedPayload
        {
            Policy = policy,
            KeyAlias = keyAlias,
            Payload = new EncryptedPayload
            {
                Nonce = authenticatedCipher.GetIV() ?? [],
                Ciphertext = output[..^SecurityDefaults.GcmTagSize],
                AuthenticationTag = output[^SecurityDefaults.GcmTagSize..],
            },
        };
    }

    public async Task<byte[]> UnprotectAsync(
        DeviceProtectedPayload protectedPayload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = LoadKeyStore();
        var key = store.GetKey(protectedPayload.KeyAlias, null)
            ?? throw new DeviceKeyUnavailableException();
        using var cipher = Cipher.GetInstance(Transformation)
            ?? throw new CryptographicException("AES-GCM is unavailable.");
        using var parameters = new GCMParameterSpec(
            SecurityDefaults.GcmTagSize * 8,
            protectedPayload.Payload.Nonce);
        try
        {
            cipher.Init(Javax.Crypto.CipherMode.DecryptMode, key, parameters);
        }
        catch (KeyPermanentlyInvalidatedException)
        {
            throw new DeviceKeyUnavailableException();
        }

        var authenticatedCipher = protectedPayload.Policy == DeviceKeyProtectionPolicy.BiometricCurrentSet
            ? await AuthenticateAsync(cipher, "Unlock PassCrate", cancellationToken)
            : cipher;
        var combined = new byte[
            protectedPayload.Payload.Ciphertext.Length +
            protectedPayload.Payload.AuthenticationTag.Length];
        protectedPayload.Payload.Ciphertext.CopyTo(combined, 0);
        protectedPayload.Payload.AuthenticationTag.CopyTo(
            combined,
            protectedPayload.Payload.Ciphertext.Length);
        try
        {
            return authenticatedCipher.DoFinal(combined)
                ?? throw new CryptographicException("Device key decryption failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(combined);
        }
    }

    public Task DeleteKeyAsync(string keyAlias, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = LoadKeyStore();
        if (store.ContainsAlias(keyAlias))
        {
            store.DeleteEntry(keyAlias);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllKeysAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = LoadKeyStore();
        var aliases = store.Aliases();
        while (aliases?.HasMoreElements == true)
        {
            var alias = aliases.NextElement()?.ToString();
            if (alias?.StartsWith("passcrate.", StringComparison.Ordinal) == true)
            {
                store.DeleteEntry(alias);
            }
        }

        return Task.CompletedTask;
    }

    private static IKey GetOrCreateKey(string alias, DeviceKeyProtectionPolicy policy)
    {
        using var store = LoadKeyStore();
        if (store.GetKey(alias, null) is IKey existing)
        {
            return existing;
        }

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeyStoreName)
            ?? throw new CryptographicException("Android Keystore AES is unavailable.");
        var builder = new KeyGenParameterSpec.Builder(
                alias,
                KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(SecurityDefaults.KeySize * 8);
        if (policy == DeviceKeyProtectionPolicy.BiometricCurrentSet)
        {
            builder
                .SetUserAuthenticationRequired(true)
                .SetUserAuthenticationParameters(0, (int)KeyPropertiesAuthType.BiometricStrong)
                .SetInvalidatedByBiometricEnrollment(true);
        }

        generator.Init(builder.Build());
        return generator.GenerateKey()
            ?? throw new CryptographicException("Android Keystore key generation failed.");
    }

    private static KeyStore LoadKeyStore()
    {
        var store = KeyStore.GetInstance(KeyStoreName)
            ?? throw new CryptographicException("Android Keystore is unavailable.");
        store.Load(null);
        return store;
    }

    private static async Task<Cipher> AuthenticateAsync(
        Cipher cipher,
        string reason,
        CancellationToken cancellationToken)
    {
        var activity = Platform.CurrentActivity ?? throw new InvalidOperationException("No Android activity is active.");
        var executor = activity.MainExecutor ?? throw new InvalidOperationException("No Android UI executor is available.");
        var completion = new TaskCompletionSource<Cipher>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var signal = new CancellationSignal();
        using var registration = cancellationToken.Register(signal.Cancel);
        var callback = new CryptoAuthenticationCallback(completion);
        var prompt = new BiometricPrompt.Builder(activity)
            .SetTitle("PassCrate")
            .SetSubtitle(reason)
            .SetAllowedAuthenticators((int)BiometricManagerAuthenticators.BiometricStrong)
            .SetNegativeButton("Use passphrase", executor, new CancelListener(signal, completion))
            .Build();
        prompt.Authenticate(new BiometricPrompt.CryptoObject(cipher), signal, executor, callback);
        return await completion.Task.WaitAsync(cancellationToken);
    }

    private sealed class CryptoAuthenticationCallback(TaskCompletionSource<Cipher> completion)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            var cipher = result?.CryptoObject?.Cipher;
            if (cipher is null)
            {
                completion.TrySetException(new CryptographicException("Biometric operation was not authenticated."));
            }
            else
            {
                completion.TrySetResult(cipher);
            }
        }

        public override void OnAuthenticationError(
            BiometricErrorCode errorCode,
            Java.Lang.ICharSequence? errString) =>
            completion.TrySetException(new System.OperationCanceledException("Biometric authentication was cancelled."));
    }

    private sealed class CancelListener(
        CancellationSignal signal,
        TaskCompletionSource<Cipher> completion)
        : Java.Lang.Object, Android.Content.IDialogInterfaceOnClickListener
    {
        public void OnClick(Android.Content.IDialogInterface? dialog, int which)
        {
            signal.Cancel();
            completion.TrySetException(new System.OperationCanceledException("Biometric authentication was cancelled."));
        }
    }
}
