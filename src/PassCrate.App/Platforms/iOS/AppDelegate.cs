using Foundation;
using PassCrate.Core.Interfaces;
using UIKit;
using Google.SignIn;

namespace PassCrate.App;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private UIView? privacyCover;
    private NSObject? captureObserver;
    private IUITraitChangeRegistration? sceneCaptureRegistration;

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
    {
        captureObserver = NSNotificationCenter.DefaultCenter.AddObserver(
            UIScreen.CapturedDidChangeNotification,
            _ => HandleCaptureStateChanged(application));
        return base.FinishedLaunching(application, launchOptions);
    }

    public override void OnResignActivation(UIApplication application)
    {
        base.OnResignActivation(application);
        ShowPrivacyCover(application);
    }

    public override void OnActivated(UIApplication application)
    {
        base.OnActivated(application);
        if (!IsScreenCaptured(application))
        {
            RemovePrivacyCover();
        }

        RegisterSceneCaptureObserver(application);
    }

    public override bool OpenUrl(UIApplication application, NSUrl url, NSDictionary options) =>
        SignIn.SharedInstance.HandleUrl(url) || base.OpenUrl(application, url, options);

    private void ShowPrivacyCover(UIApplication application)
    {
        var window = FindKeyWindow(application);
        if (window is null || privacyCover is not null)
        {
            return;
        }

        privacyCover = new UIView(window.Bounds) { BackgroundColor = UIColor.FromRGB(11, 17, 32) };
        var title = new UILabel(window.Bounds)
        {
            Text = "PassCrate\nVault locked",
            TextColor = UIColor.White,
            TextAlignment = UITextAlignment.Center,
            Lines = 2,
        };
        var boldFont = UIFont.BoldSystemFontOfSize(24);
        if (boldFont is not null)
        {
            title.Font = boldFont;
        }

        privacyCover.AddSubview(title);
        window.AddSubview(privacyCover);
    }

    private void HandleCaptureStateChanged(UIApplication application)
    {
        if (!IsScreenCaptured(application))
        {
            if (application.ApplicationState == UIApplicationState.Active)
            {
                RemovePrivacyCover();
                MainThread.BeginInvokeOnMainThread(async () =>
                    await Shell.Current.GoToAsync("//unlock"));
            }

            return;
        }

        ShowPrivacyCover(application);
        var services = IPlatformApplication.Current?.Services;
        services?.GetService<ICloudSyncScheduler>()?.NotifyVaultLocked();
        services?.GetService<IKeyManagementService>()?.LockVault();
        _ = services?.GetService<ISensitiveClipboardService>()?.ClearAsync();
    }

    private void RemovePrivacyCover()
    {
        privacyCover?.RemoveFromSuperview();
        privacyCover?.Dispose();
        privacyCover = null;
    }

    private void RegisterSceneCaptureObserver(UIApplication application)
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17, 2) || sceneCaptureRegistration is not null)
        {
            return;
        }

        var controller = FindKeyWindow(application)?.RootViewController;
        sceneCaptureRegistration = controller?.RegisterForTraitChanges<UITraitSceneCaptureState>(
            (_, _) => HandleCaptureStateChanged(application));
    }

    private static UIWindow? FindKeyWindow(UIApplication application) =>
        application.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(window => window.IsKeyWindow);

    private static bool IsScreenCaptured(UIApplication application)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17, 2))
        {
            return FindKeyWindow(application)?.WindowScene?.TraitCollection.SceneCaptureState ==
                UISceneCaptureState.Active;
        }

        return UIScreen.MainScreen.Captured;
    }
}
