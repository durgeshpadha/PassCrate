using System.Security.Cryptography;
using System.Text.Json;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

#if ANDROID
using Android.Content;
using Android.Hardware.Biometrics;
#elif IOS
using LocalAuthentication;
#endif

namespace PassCrate.App.Services;

public interface IBiometricUnlockService
{
    Task<bool> IsAvailableAsync();
    Task<bool> IsEnabledAsync();
    Task<bool> EnableAsync();
    Task DisableAsync();
    Task<bool> UnlockAsync();
}

public sealed class BiometricUnlockService(
    IVaultSession session,
    IDeviceKeyProtectionService deviceProtection,
    IDeviceCredentialStore credentials) : IBiometricUnlockService
{
    private const string EnvelopeCredentialName = "biometric.envelope.v2";
    private const string BiometricKeyAlias = "passcrate.biometric.current-set.v2";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<bool> IsAvailableAsync() => IsPlatformAvailableAsync();

    public async Task<bool> IsEnabledAsync() =>
        await IsPlatformAvailableAsync() &&
        !string.IsNullOrWhiteSpace(await credentials.GetAsync(EnvelopeCredentialName));

    public async Task<bool> EnableAsync()
    {
        if (!session.IsUnlocked || !await IsPlatformAvailableAsync())
        {
            return false;
        }

        var dataEncryptionKey = session.UseKey(key => key.ToArray());
        try
        {
            var envelope = await deviceProtection.ProtectAsync(
                dataEncryptionKey,
                DeviceKeyProtectionPolicy.BiometricCurrentSet,
                BiometricKeyAlias);
            await credentials.SetAsync(
                EnvelopeCredentialName,
                JsonSerializer.Serialize(envelope, JsonOptions));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    public async Task DisableAsync()
    {
        await credentials.RemoveAsync(EnvelopeCredentialName);
        await deviceProtection.DeleteKeyAsync(BiometricKeyAlias);
    }

    public async Task<bool> UnlockAsync()
    {
        var json = await credentials.GetAsync(EnvelopeCredentialName);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        DeviceProtectedPayload? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<DeviceProtectedPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            await DisableAsync();
            return false;
        }

        if (envelope is null ||
            envelope.Policy != DeviceKeyProtectionPolicy.BiometricCurrentSet ||
            envelope.KeyAlias != BiometricKeyAlias)
        {
            await DisableAsync();
            return false;
        }

        byte[]? dataEncryptionKey = null;
        try
        {
            dataEncryptionKey = await deviceProtection.UnprotectAsync(envelope);
            session.Unlock(dataEncryptionKey);
            return true;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or CryptographicException or PassCrate.Core.Security.DeviceKeyUnavailableException)
        {
            if (exception is not OperationCanceledException)
            {
                await DisableAsync();
            }

            return false;
        }
        finally
        {
            if (dataEncryptionKey is not null)
            {
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
            }
        }
    }

    private static Task<bool> IsPlatformAvailableAsync()
    {
#if ANDROID
        try
        {
            var activity = Platform.CurrentActivity;
            var manager = activity?.GetSystemService(Context.BiometricService) as BiometricManager;
            return Task.FromResult(
                manager?.CanAuthenticate((int)BiometricManagerAuthenticators.BiometricStrong) ==
                BiometricCode.Success);
        }
        catch (Exception exception) when (exception is Java.Lang.SecurityException or InvalidOperationException)
        {
            return Task.FromResult(false);
        }
#elif IOS
        using var context = new LAContext();
        try
        {
            return Task.FromResult(context.CanEvaluatePolicy(
                LAPolicy.DeviceOwnerAuthenticationWithBiometrics,
                out _));
        }
        catch
        {
            return Task.FromResult(false);
        }
#else
        return Task.FromResult(false);
#endif
    }
}
