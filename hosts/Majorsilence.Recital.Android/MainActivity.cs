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
        private static MainActivity? current;
        private static bool asked;

        /// <summary>
        /// Asks once, the first time reading aloud starts, to show notifications (Android 13 and later), so the
        /// reading-aloud notification can be seen. Reading goes on either way.
        /// </summary>
        public static void AskToShowNotifications ()
        {
            if (asked || current is null || !OperatingSystem.IsAndroidVersionAtLeast (33))
                return;
            asked = true;
            if (current.CheckSelfPermission (global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
                current.RequestPermissions ([global::Android.Manifest.Permission.PostNotifications], 0);
        }

        protected override void OnCreate (Bundle? savedInstanceState)
        {
            base.OnCreate (savedInstanceState);
            current = this;
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
