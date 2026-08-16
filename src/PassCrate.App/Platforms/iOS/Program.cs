using ObjCRuntime;
using UIKit;
using Foundation;

namespace PassCrate.App;

public class Program
{
	// This is the main entry point of the application.
	static void Main(string[] args)
	{
		// if you want to use a different Application Delegate class from "AppDelegate"
		// you can specify it here.
		UIApplication.Main(args, typeof(PassCrateApplication), typeof(AppDelegate));
	}
}

[Register("PassCrateApplication")]
public sealed class PassCrateApplication : UIApplication
{
    public override void SendEvent(UIEvent uievent)
    {
        if (uievent.Type == UIEventType.Touches)
        {
            Services.UserActivityTracker.Notify();
        }

        base.SendEvent(uievent);
    }
}
