namespace PassCrate.Core.Security;

public sealed class CredentialValidationException(string message) : Exception(message);

public sealed class AuthenticationFailedException() : Exception("Unable to authenticate.");

public sealed class VaultUnlockException() : Exception("Unable to unlock your vault.");

public sealed class VaultUnlockThrottledException(DateTimeOffset nextAllowedAt) : Exception(
    $"Try again after {nextAllowedAt.ToLocalTime():t}.")
{
    public DateTimeOffset NextAllowedAt { get; } = nextAllowedAt;
}

public sealed class DeviceKeyUnavailableException() : Exception(
    "This vault is protected by a device key that is unavailable. Restore from your encrypted cloud backup.");

public sealed class VaultCorruptedException() : Exception("PassCrate could not verify your vault data.");

public sealed class VaultLockedException() : Exception("Your vault is locked.");

public sealed class DuplicateAccountException() : Exception("A local PassCrate vault already exists.");

public sealed class GroupNotEmptyException() : Exception("Move the secrets in this group before deleting it.");

public sealed class CloudAuthorizationRequiredException() : Exception("Reconnect your cloud provider to continue syncing.");

public sealed class CloudVaultMismatchException() : Exception(
    "This provider account already contains a different PassCrate vault. Restore it or choose another provider account.");

public sealed class CloudSnapshotUnavailableException() : Exception("No valid PassCrate cloud snapshot was found.");

public sealed class CloudRestoreFailedException() : Exception(
    "PassCrate could not unlock a valid cloud snapshot with that vault passphrase.");

public sealed class CloudResourceLimitException(string message) : Exception(message);

public sealed class CloudQuotaException(TimeSpan? retryAfter = null) : Exception(
    "Cloud storage is temporarily unavailable because its quota or rate limit was reached.")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
