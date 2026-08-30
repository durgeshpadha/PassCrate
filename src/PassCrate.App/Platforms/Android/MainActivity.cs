using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;
using Google.Android.Material.BottomNavigation;
using AndroidView = Android.Views.View;

namespace PassCrate.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private BottomNavigationView? _bottomNavigationView;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
		ApplySystemBarTheme(Resources?.Configuration);
	}

	public override void OnConfigurationChanged(Configuration newConfig)
	{
		base.OnConfigurationChanged(newConfig);
		ApplySystemBarTheme(newConfig);
	}

	protected override void OnResume()
	{
		base.OnResume();
		ApplySystemBarTheme(Resources?.Configuration);
		Window?.DecorView.Post(WireBottomNavigationReselection);
	}

	public override void OnWindowFocusChanged(bool hasFocus)
	{
		base.OnWindowFocusChanged(hasFocus);
		if (hasFocus)
		{
			WireBottomNavigationReselection();
		}
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

	private void ApplySystemBarTheme(Configuration? configuration)
	{
		if (Window is null || configuration is null)
		{
			return;
		}

		var isDark = (configuration.UiMode & UiMode.NightMask) == UiMode.NightYes;
		var surfaceColor = Android.Graphics.Color.ParseColor(isDark ? "#121A29" : "#FFFFFF");

#pragma warning disable CA1422 // Required on Android 13 and earlier; Android 15 draws edge-to-edge.
		Window.SetStatusBarColor(surfaceColor);
		Window.SetNavigationBarColor(surfaceColor);
#pragma warning restore CA1422

		var insetsController = WindowCompat.GetInsetsController(Window, Window.DecorView);
		if (insetsController is not null)
		{
			insetsController.AppearanceLightStatusBars = !isDark;
			insetsController.AppearanceLightNavigationBars = !isDark;
		}
	}

	private void WireBottomNavigationReselection()
	{
		if (Window?.DecorView is not AndroidView root)
		{
			return;
		}

		var bottomNavigationView = FindDescendant<BottomNavigationView>(root);
		if (bottomNavigationView is null || ReferenceEquals(bottomNavigationView, _bottomNavigationView))
		{
			return;
		}

		// MAUI replaces this native view during Shell navigation. The old view may
		// already be disposed by the time focus returns after locking the device,
		// so do not touch it here. The handler is static and does not retain this
		// activity; the disposed view releases its own listener.
		_bottomNavigationView = bottomNavigationView;
		_bottomNavigationView.ItemReselected += OnBottomNavigationItemReselected;
	}

	private static async void OnBottomNavigationItemReselected(object? sender, EventArgs eventArgs)
	{
		if (Shell.Current is AppShell shell)
		{
			await shell.NavigateToSelectedTabRootAsync();
		}
	}

	private static TView? FindDescendant<TView>(AndroidView view)
		where TView : AndroidView
	{
		if (view is TView match)
		{
			return match;
		}

		if (view is not ViewGroup group)
		{
			return null;
		}

		for (var index = 0; index < group.ChildCount; index++)
		{
			var child = group.GetChildAt(index);
			if (child is null)
			{
				continue;
			}

			var descendant = FindDescendant<TView>(child);
			if (descendant is not null)
			{
				return descendant;
			}
		}

		return null;
	}
}
