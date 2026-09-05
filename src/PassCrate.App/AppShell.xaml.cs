using Microsoft.Extensions.DependencyInjection;
using PassCrate.App.Services;
using PassCrate.App.Views;
using PassCrate.Core.Interfaces;
using PassCrate.Core.Legal;
using PassCrate.Core.Security;

namespace PassCrate.App;

public partial class AppShell : Shell
{
    private readonly IVaultRepository _repository;
    private readonly IKeyManagementService _keyManagement;
    private readonly ILegalAcceptanceStore _legalAcceptance;
    private readonly TabBar _mainTabs;
    private bool _legalRedirectPending;

    public AppShell(
        IServiceProvider services,
        IVaultRepository repository,
        IKeyManagementService keyManagement,
        ILegalAcceptanceStore legalAcceptance)
    {
        _repository = repository;
        _keyManagement = keyManagement;
        _legalAcceptance = legalAcceptance;
        InitializeComponent();
        Items.Add(CreateShellContent<WelcomePage>(services, "welcome", "Welcome"));
        Items.Add(CreateShellContent<RegistrationPage>(services, "register", "Create account"));
        Items.Add(CreateShellContent<UnlockPage>(services, "unlock", "Unlock"));

        _mainTabs = new TabBar { Route = "main" };
        _mainTabs.Items.Add(CreateShellContent<DashboardPage>(services, "dashboard", "Home", "⌂"));
        _mainTabs.Items.Add(CreateShellContent<SearchPage>(services, "search", "Search", "⌕"));
        _mainTabs.Items.Add(CreateShellContent<SettingsPage>(services, "settings", "Settings", "⚙"));
        Items.Add(_mainTabs);

        Routing.RegisterRoute(nameof(GroupDetailsPage), typeof(GroupDetailsPage));
        Routing.RegisterRoute(nameof(GroupEditorPage), typeof(GroupEditorPage));
        Routing.RegisterRoute(nameof(SecretDetailsPage), typeof(SecretDetailsPage));
        Routing.RegisterRoute(nameof(SecretEditorPage), typeof(SecretEditorPage));
        Routing.RegisterRoute(nameof(ChangePasswordPage), typeof(ChangePasswordPage));
        Routing.RegisterRoute(nameof(ResetPage), typeof(ResetPage));
        Routing.RegisterRoute(nameof(CloudSyncPage), typeof(CloudSyncPage));
        Routing.RegisterRoute(nameof(CloudRestorePage), typeof(CloudRestorePage));
        Routing.RegisterRoute(nameof(ConflictReviewPage), typeof(ConflictReviewPage));
        Routing.RegisterRoute(nameof(HelpPage), typeof(HelpPage));
        Routing.RegisterRoute(nameof(LegalAcceptancePage), typeof(LegalAcceptancePage));
        Routing.RegisterRoute(nameof(LegalDocumentPage), typeof(LegalDocumentPage));

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
        var legalDestination = LegalAcceptancePolicy.GetDestinationForRoute(target);
        if (legalDestination is not null && !_legalAcceptance.HasAcceptedCurrentVersions)
        {
            eventArgs.Cancel();
            if (!_legalRedirectPending)
            {
                _legalRedirectPending = true;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await GoToAsync($"{nameof(LegalAcceptancePage)}?next={legalDestination}");
                    }
                    finally
                    {
                        _legalRedirectPending = false;
                    }
                });
            }

            return;
        }

        var requiresUnlockedVault = NavigationSecurityPolicy.RequiresUnlockedVault(target);
        if (!requiresUnlockedVault || _keyManagement.IsUnlocked)
        {
            return;
        }

        eventArgs.Cancel();
        MainThread.BeginInvokeOnMainThread(async () => await GoToAsync("//unlock"));
    }

    public Task NavigateToSelectedTabRootAsync()
    {
        if (!_keyManagement.IsUnlocked)
        {
            return Task.CompletedTask;
        }

        var selectedRoute = CurrentItem?.CurrentItem?.CurrentItem?.Route;
        return selectedRoute switch
        {
            "dashboard" => GoToAsync("//main/dashboard"),
            "search" => GoToAsync("//main/search"),
            "settings" => GoToAsync("//main/settings"),
            _ => Task.CompletedTask,
        };
    }

    public async Task NavigateToFreshMainAsync()
    {
        await GoToAsync("//main/dashboard");
        await ClearMainTabStacksAsync();
    }

    public async Task NavigateToWelcomeAfterResetAsync()
    {
        if (Application.Current is not null)
        {
            Application.Current.UserAppTheme = Microsoft.Maui.ApplicationModel.AppTheme.Unspecified;
        }

        await GoToAsync("//welcome");
        await ClearMainTabStacksAsync();
    }

    private async Task ClearMainTabStacksAsync()
    {
        foreach (var section in _mainTabs.Items)
        {
            if (section.Navigation.NavigationStack.Count > 1)
            {
                await section.Navigation.PopToRootAsync(animated: false);
            }

            foreach (var content in section.Items)
            {
                if (content.Content is IResettablePageState resettablePage)
                {
                    resettablePage.ResetPageState();
                }
            }
        }
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
                Color = Color.FromArgb("#5B5CE2"),
                Size = 20,
            },
        ContentTemplate = new DataTemplate(() => services.GetRequiredService<TPage>()),
    };
}
