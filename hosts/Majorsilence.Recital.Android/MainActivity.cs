using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

using MSFormsBackends = Majorsilence.Forms.Backends;

namespace Majorsilence.Recital.Android
{
    // The theme must descend from Theme.AppCompat: AvaloniaMainActivity is an AppCompatActivity.
    [Activity (
        Label = "Recital",
        Theme = "@style/RecitalTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public class MainActivity : AvaloniaMainActivity
    {
        protected override void OnCreate (Bundle? savedInstanceState)
        {
            base.OnCreate (savedInstanceState);
            BackRequested += (_, e) => e.Handled = MSFormsBackends.AvaloniaPlatformBackend.RaiseBackRequested ();
            MSFormsBackends.AvaloniaPlatformBackend.RegisterAndroidActivity (this);
            MSFormsBackends.AvaloniaPlatformBackend.ReportAndroidIntent (Intent);
        }

        protected override void OnNewIntent (Intent? intent)
        {
            base.OnNewIntent (intent);
            MSFormsBackends.AvaloniaPlatformBackend.ReportAndroidIntent (intent);
        }
    }
}
