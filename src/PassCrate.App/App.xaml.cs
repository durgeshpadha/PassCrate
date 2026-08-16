using PassCrate.App.Services;
using PassCrate.Core.Interfaces;

namespace PassCrate.App;

public partial class App : Application
{
    private readonly AppShell _shell;
    private readonly IKeyManagementService _keyManagement;
    private readonly IAutoLockService _autoLock;
    private readonly ICloudSyncScheduler _cloudSync;
    private readonly ISensitiveClipboardService _clipboard;
    private readonly IFirstLaunchSecurityService _firstLaunchSecurity;

    public App(
        AppShell shell,
        IKeyManagementService keyManagement,
        IAutoLockService autoLock,
        ICloudSyncScheduler cloudSync,
        ISensitiveClipboardService clipboard,
        IFirstLaunchSecurityService firstLaunchSecurity)
    {
        InitializeComponent();
        _shell = shell;
        _keyManagement = keyManagement;
        _autoLock = autoLock;
        _cloudSync = cloudSync;
        _clipboard = clipboard;
        _firstLaunchSecurity = firstLaunchSecurity;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        _firstLaunchSecurity.EnsureCleanInstallAsync().GetAwaiter().GetResult();
        _autoLock.Start();
        var window = new Window(_shell);
        window.Activated += OnActivated;
        window.Deactivated += OnDeactivated;
        return window;
    }

    private void OnDeactivated(object? sender, EventArgs eventArgs)
    {
        _cloudSync.NotifyBackgrounded();
        _ = _clipboard.ClearAsync();
        if (Shell.Current?.CurrentPage?.BindingContext is ViewModels.ISensitiveStateViewModel sensitive)
        {
            sensitive.ClearSensitiveState();
        }
        if (!_keyManagement.IsUnlocked)
        {
            return;
        }

        _keyManagement.LockVault();
        _autoLock.NotifyActivity();
        MainThread.BeginInvokeOnMainThread(async () => await _shell.GoToAsync("//unlock"));
    }

    private void OnActivated(object? sender, EventArgs eventArgs) => _cloudSync.NotifyForegrounded();
}
