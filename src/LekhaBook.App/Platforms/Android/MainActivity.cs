using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace LekhaBook.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // The system bars sit on the indigo page background, so use white status-bar
        // and navigation-bar icons.
        if (Window is { } window && WindowCompat.GetInsetsController(window, window.DecorView) is { } insets)
        {
            insets.AppearanceLightStatusBars = false;
            insets.AppearanceLightNavigationBars = false;
        }
    }
}
