using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
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

    // Android 15 and older send Back as a key, which the web view would use for its own page history. Send it
    // the way Android 16 does instead, so that MainPage.OnBackButtonPressed decides on every version.
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.KeyCode == Keycode.Back)
        {
            if (e.Action == KeyEventActions.Up && !e.IsCanceled)
            {
#pragma warning disable CS0618, CA1422 // MAUI handles Back by overriding this method
                OnBackPressed();
#pragma warning restore CS0618, CA1422
            }

            return true;
        }

        return base.DispatchKeyEvent(e);
    }
}
