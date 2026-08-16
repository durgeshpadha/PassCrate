namespace PassCrate.Core.Models;

public sealed record VaultGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Icon { get; init; } = "◇";
    public string Color { get; init; } = "#64748B";
    public int SortOrder { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SecretField
{
    public required string Key { get; init; }
    public required string Value { get; init; }
    public bool IsSensitive { get; init; }
}

public sealed record SecretDocument
{
    public required string Name { get; init; }
    public IReadOnlyList<SecretField> Fields { get; init; } = [];
    public string Notes { get; init; } = string.Empty;
}

public sealed record VaultSecret
{
    public required string Id { get; init; }
    public required string GroupId { get; init; }
    public required string Name { get; init; }
    public string SearchableFieldNames { get; init; } = string.Empty;
    public required EncryptedPayload EncryptedPayload { get; init; }
    public int EncryptionVersion { get; init; } = 1;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SecretSummary(
    string Id,
    string GroupId,
    string Name,
    DateTimeOffset UpdatedAt);

public sealed record VaultSearchResults
{
    public IReadOnlyList<VaultGroup> Groups { get; init; } = [];
    public IReadOnlyList<SecretSummary> Secrets { get; init; } = [];
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum AutoLockTimeout
{
    // The vault always locks when PassCrate leaves the foreground. This value
    // disables only the additional in-app inactivity timer.
    OnAppSwitch = 0,
    OneMinute = 60,
    FiveMinutes = 300,
    FifteenMinutes = 900,
    // Retained only so an old local settings value continues to mean no
    // foreground inactivity timer. It is no longer exposed in the UI.
    Never = -1,
}

public sealed record ApplicationSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;
    public AutoLockTimeout AutoLock { get; init; } = AutoLockTimeout.OneMinute;
    public bool BiometricUnlockEnabled { get; init; }
    public int ClipboardClearSeconds { get; init; } = 30;
}
