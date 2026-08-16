using Android.App;
using Android.Content;
using Android.Content.PM;
using Microsoft.Maui.Authentication;

namespace PassCrate.App;

[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "passcrate",
    DataHost = "oauth2redirect")]
public sealed class WebAuthenticationCallbackActivity : WebAuthenticatorCallbackActivity;
