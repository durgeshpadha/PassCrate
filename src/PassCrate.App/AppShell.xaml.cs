using Microsoft.Extensions.DependencyInjection;
using PassCrate.App.Views;
using PassCrate.Core.Interfaces;

namespace PassCrate.App;

public partial class AppShell : Shell
{
    private readonly IVaultRepository _repository;
    private readonly IKeyManagementService _keyManagement;

    public AppShell(
        IServiceProvider services,
        IVaultRepository repository,
        IKeyManagementService keyManagement)
    {
        _repository = repository;
        _keyManagement = keyManagement;
        InitializeComponent();
        Items.Add(CreateShellContent<WelcomePage>(services, "welcome", "Welcome"));
        Items.Add(CreateShellContent<RegistrationPage>(services, "register", "Create account"));
        Items.Add(CreateShellContent<UnlockPage>(services, "unlock", "Unlock"));

        var tabs = new TabBar { Route = "main" };
        tabs.Items.Add(CreateShellContent<DashboardPage>(services, "dashboard", "Home", "⌂"));
        tabs.Items.Add(CreateShellContent<SearchPage>(services, "search", "Search", "⌕"));
        tabs.Items.Add(CreateShellContent<SettingsPage>(services, "settings", "Settings", "⚙"));
        Items.Add(tabs);

        Routing.RegisterRoute(nameof(GroupDetailsPage), typeof(GroupDetailsPage));
        Routing.RegisterRoute(nameof(GroupEditorPage), typeof(GroupEditorPage));
        Routing.RegisterRoute(nameof(SecretDetailsPage), typeof(SecretDetailsPage));
        Routing.RegisterRoute(nameof(SecretEditorPage), typeof(SecretEditorPage));
        Routing.RegisterRoute(nameof(ChangePasswordPage), typeof(ChangePasswordPage));
        Routing.RegisterRoute(nameof(ResetPage), typeof(ResetPage));
        Routing.RegisterRoute(nameof(CloudSyncPage), typeof(CloudSyncPage));
        Routing.RegisterRoute(nameof(CloudRestorePage), typeof(CloudRestorePage));
        Routing.RegisterRoute(nameof(ConflictReviewPage), typeof(ConflictReviewPage));

        Loaded += OnLoaded;
        Navigating += OnNavigating;
    }

    private async void OnLoaded(object? sender, EventArgs eventArgs)
    {
        Loaded -= OnLoaded;
        await _repository.InitializeAsync();
        ApplyTheme((await _repository.GetSettingsAsync()).Theme);
        await GoToAsync(await _repository.GetVaultMetadataAsync() is null ? "//welcome" : "//unlock");
    }

    private void OnNavigating(object? sender, ShellNavigatingEventArgs eventArgs)
    {
        var target = eventArgs.Target.Location.OriginalString;
        var requiresUnlockedVault =
            target.Contains("//main", StringComparison.OrdinalIgnoreCase) ||
            target.Contains(nameof(GroupDetailsPage), StringComparison.Ordinal) ||
            target.Contains(nameof(GroupEditorPage), StringComparison.Ordinal) ||
            target.Contains(nameof(SecretDetailsPage), StringComparison.Ordinal) ||
            target.Contains(nameof(SecretEditorPage), StringComparison.Ordinal) ||
            target.Contains(nameof(ChangePasswordPage), StringComparison.Ordinal) ||
            target.Contains(nameof(ResetPage), StringComparison.Ordinal) ||
            target.Contains(nameof(CloudSyncPage), StringComparison.Ordinal) ||
            target.Contains(nameof(ConflictReviewPage), StringComparison.Ordinal) ||
            target.Contains(nameof(SettingsPage), StringComparison.Ordinal);
        if (!requiresUnlockedVault || _keyManagement.IsUnlocked)
        {
            return;
        }

        eventArgs.Cancel();
        MainThread.BeginInvokeOnMainThread(async () => await GoToAsync("//unlock"));
    }

    private static void ApplyTheme(PassCrate.Core.Models.AppTheme theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.UserAppTheme = theme switch
        {
            PassCrate.Core.Models.AppTheme.Light => Microsoft.Maui.ApplicationModel.AppTheme.Light,
            PassCrate.Core.Models.AppTheme.Dark => Microsoft.Maui.ApplicationModel.AppTheme.Dark,
            _ => Microsoft.Maui.ApplicationModel.AppTheme.Unspecified,
        };
    }

    private static ShellContent CreateShellContent<TPage>(
        IServiceProvider services,
        string route,
        string title,
        string? icon = null)
        where TPage : Page => new()
    {
        Route = route,
        Title = title,
        Icon = string.IsNullOrEmpty(icon)
            ? null
            : new FontImageSource
            {
                Glyph = icon,
                FontFamily = "OpenSansSemibold",
                Color = Color.FromArgb("#4F46E5"),
                Size = 20,
            },
        ContentTemplate = new DataTemplate(() => services.GetRequiredService<TPage>()),
    };
}
