using Microsoft.Extensions.Logging;
using PassCrate.App.Services;
using PassCrate.App.ViewModels;
using PassCrate.App.Views;
using PassCrate.Core.Interfaces;
using PassCrate.Infrastructure.Database;
using PassCrate.Infrastructure.Encryption;
using PassCrate.Infrastructure.KeyManagement;
using PassCrate.Infrastructure.Services;
using PassCrate.Infrastructure.Sync;

namespace PassCrate.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<ILocalDataPathProvider, LocalDataPathProvider>();
        builder.Services.AddSingleton<IVaultRepository>(services =>
        {
            var paths = services.GetRequiredService<ILocalDataPathProvider>();
            paths.ReapplyBackupExclusion();
            return new SqliteVaultRepository(
                paths.DatabasePath,
                services.GetRequiredService<IEncryptionService>(),
                services.GetRequiredService<IVaultSession>());
        });
        builder.Services.AddSingleton<ISyncRepository>(services =>
            (SqliteVaultRepository)services.GetRequiredService<IVaultRepository>());
        builder.Services.AddSingleton<IConflictRepository>(services =>
            (SqliteVaultRepository)services.GetRequiredService<IVaultRepository>());
        builder.Services.AddSingleton<IKeyDerivationService, KeyDerivationService>();
        builder.Services.AddSingleton<IKeyDerivationParameterProvider, DefaultKeyDerivationParameterProvider>();
        builder.Services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        builder.Services.AddSingleton<IVaultSession, VaultSession>();
#if ANDROID
        builder.Services.AddSingleton<IDeviceKeyProtectionService, AndroidDeviceKeyProtectionService>();
        builder.Services.AddSingleton<IDeviceCredentialStore, MauiDeviceCredentialStore>();
#elif IOS
        builder.Services.AddSingleton<IDeviceKeyProtectionService, IosDeviceKeyProtectionService>();
        builder.Services.AddSingleton<IDeviceCredentialStore, IosDeviceCredentialStore>();
#endif
        builder.Services.AddSingleton<IKeyManagementService, KeyManagementService>();
        builder.Services.AddSingleton<IVaultService, VaultService>();
        builder.Services.AddSingleton<ISecureStorageService, MauiSecureStorageService>();
        builder.Services.AddSingleton<ISensitiveClipboardService, SensitiveClipboardService>();
        builder.Services.AddSingleton<IUserErrorMessageMapper, UserErrorMessageMapper>();
        builder.Services.AddSingleton<IBiometricUnlockService, BiometricUnlockService>();
        builder.Services.AddSingleton<IAutoLockService, AutoLockService>();
        builder.Services.AddSingleton<IApplicationResetService, ApplicationResetService>();
        builder.Services.AddSingleton<IFirstLaunchSecurityService, FirstLaunchSecurityService>();
        builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(45) });
        builder.Services.AddSingleton(CloudOAuthOptions.FromAssembly());
#if ANDROID
        builder.Services.AddSingleton<IGoogleDrivePlatformAuthorization, AndroidGoogleDriveAuthorizationService>();
#elif IOS
        builder.Services.AddSingleton<IGoogleDrivePlatformAuthorization, IosGoogleDriveAuthorizationService>();
#else
        builder.Services.AddSingleton<IGoogleDrivePlatformAuthorization, PortableGoogleDrivePlatformAuthorization>();
#endif
        builder.Services.AddSingleton<ICloudAuthorizationService, MauiCloudAuthorizationService>();
        builder.Services.AddSingleton<ICloudNetworkPolicy, MauiCloudNetworkPolicy>();
        builder.Services.AddSingleton<ICloudSnapshotService, CloudSnapshotService>();
        builder.Services.AddSingleton<ISyncMergeService, SyncMergeService>();
        builder.Services.AddSingleton<ICloudStorageProvider, GoogleDriveCloudStorageProvider>();
        builder.Services.AddSingleton<ICloudStorageProvider, DropboxCloudStorageProvider>();
        builder.Services.AddSingleton<ICloudStorageProviderFactory, CloudStorageProviderFactory>();
        builder.Services.AddSingleton<ICloudSyncService, CloudSyncService>();
        builder.Services.AddSingleton<CloudSyncCoordinator>();
        builder.Services.AddSingleton<ICloudSyncScheduler>(services => services.GetRequiredService<CloudSyncCoordinator>());

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddTransient<WelcomeViewModel>();
        builder.Services.AddTransient<RegistrationViewModel>();
        builder.Services.AddTransient<UnlockViewModel>();
        builder.Services.AddTransient<DashboardViewModel>();
        builder.Services.AddTransient<GroupDetailsViewModel>();
        builder.Services.AddTransient<GroupEditorViewModel>();
        builder.Services.AddTransient<SecretDetailsViewModel>();
        builder.Services.AddTransient<SecretEditorViewModel>();
        builder.Services.AddTransient<SearchViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<ChangePasswordViewModel>();
        builder.Services.AddTransient<ResetViewModel>();
        builder.Services.AddTransient<CloudSyncViewModel>();
        builder.Services.AddTransient<CloudRestoreViewModel>();
        builder.Services.AddTransient<ConflictReviewViewModel>();

        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<RegistrationPage>();
        builder.Services.AddTransient<UnlockPage>();
        builder.Services.AddTransient<DashboardPage>();
        builder.Services.AddTransient<GroupDetailsPage>();
        builder.Services.AddTransient<GroupEditorPage>();
        builder.Services.AddTransient<SecretDetailsPage>();
        builder.Services.AddTransient<SecretEditorPage>();
        builder.Services.AddTransient<SearchPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<ChangePasswordPage>();
        builder.Services.AddTransient<ResetPage>();
        builder.Services.AddTransient<CloudSyncPage>();
        builder.Services.AddTransient<CloudRestorePage>();
        builder.Services.AddTransient<ConflictReviewPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }

}
