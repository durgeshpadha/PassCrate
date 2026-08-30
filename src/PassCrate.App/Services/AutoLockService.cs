using PassCrate.Core.Interfaces;
using PassCrate.Core.Models;

namespace PassCrate.App.Services;

public interface IAutoLockService
{
    void Start();
    void NotifyActivity();
    void UpdateTimeout(AutoLockTimeout timeout);
}

public static class UserActivityTracker
{
    public static event Action? Activity;

    public static void Notify() => Activity?.Invoke();
}

public sealed class AutoLockService(
    IVaultRepository repository,
    IKeyManagementService keyManagement,
    ICloudSyncScheduler cloudSync,
    ISensitiveClipboardService clipboard) : IAutoLockService
{
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private IDispatcherTimer? _timer;
    private AutoLockTimeout _timeout = AutoLockTimeout.OneMinute;
    private int _isChecking;

    public void Start()
    {
        if (_timer is not null || Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        UserActivityTracker.Activity += NotifyActivity;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;
        _timer.Start();
        _ = LoadTimeoutAsync();
    }

    public void NotifyActivity() => _lastActivity = DateTimeOffset.UtcNow;

    public void UpdateTimeout(AutoLockTimeout timeout)
    {
        _timeout = timeout;
        NotifyActivity();
    }

    private async void OnTimerTick(object? sender, EventArgs eventArgs)
    {
        if (!keyManagement.IsUnlocked || Interlocked.Exchange(ref _isChecking, 1) != 0)
        {
            return;
        }

        try
        {
            if (_timeout is AutoLockTimeout.Never or AutoLockTimeout.OnAppSwitch)
            {
                return;
            }

            var seconds = (int)_timeout;
            if (DateTimeOffset.UtcNow - _lastActivity < TimeSpan.FromSeconds(seconds))
            {
                return;
            }

            cloudSync.NotifyVaultLocked();
            var shell = Shell.Current;
            if (shell?.CurrentPage?.BindingContext is ViewModels.ISensitiveStateViewModel sensitive)
            {
                sensitive.ClearSensitiveState();
            }
            keyManagement.LockVault();
            await clipboard.ClearAsync();
            NotifyActivity();
            if (shell is not null)
            {
                await shell.GoToAsync("//unlock");
            }
        }
        catch (Exception)
        {
            // Auto-lock must not surface repository or navigation details. The
            // next lifecycle transition still applies the immediate hard lock.
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    private async Task LoadTimeoutAsync()
    {
        try
        {
            UpdateTimeout((await repository.GetSettingsAsync()).AutoLock);
        }
        catch (Exception)
        {
            // The repository may still be initializing. The documented default
            // remains active until settings are loaded or changed in the UI.
        }
    }
}
