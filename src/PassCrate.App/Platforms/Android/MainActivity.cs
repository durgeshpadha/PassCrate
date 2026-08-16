using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace PassCrate.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
	}

	public override bool DispatchTouchEvent(MotionEvent? ev)
	{
		if (ev?.ActionMasked is MotionEventActions.Down or MotionEventActions.Move)
		{
			Services.UserActivityTracker.Notify();
		}

		return base.DispatchTouchEvent(ev);
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Android.Content.Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		if (requestCode == GoogleAuthorizationActivityBroker.RequestCode)
		{
			GoogleAuthorizationActivityBroker.Complete(resultCode, data);
		}
	}
}
