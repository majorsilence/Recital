using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Avalonia.Android;
using Majorsilence.Forms.Backends;

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
    // "Open with Recital" for an EPUB, and Recital in the share sheet for one or several.
    [IntentFilter ([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataMimeType = "application/epub+zip")]
    [IntentFilter ([Intent.ActionSend, Intent.ActionSendMultiple], Categories = [Intent.CategoryDefault], DataMimeType = "application/epub+zip")]
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
            OpenBooks (Intent);
        }

        protected override void OnNewIntent (Intent? intent)
        {
            base.OnNewIntent (intent);
            MSFormsBackends.AvaloniaPlatformBackend.ReportAndroidIntent (intent);
            OpenBooks (intent);
        }

        // Books handed over by another app: read through the content resolver (the URI is all there is), named as
        // that app names them.
        private void OpenBooks (Intent? intent)
        {
            var uris = new List<global::Android.Net.Uri> ();
            if (intent?.Action == Intent.ActionView && intent.Data is { } data)
                uris.Add (data);
            else if (intent?.Action is Intent.ActionSend or Intent.ActionSendMultiple) {
                // A share carries its files in ClipData too, which is what read access is granted on; the extra
                // alone (as `am` sends it) comes without that grant.
                for (var i = 0; i < (intent.ClipData?.ItemCount ?? 0); i++)
                    if (intent.ClipData!.GetItemAt (i)?.Uri is { } clip)
                        uris.Add (clip);
                if (uris.Count == 0 && intent.Action == Intent.ActionSend && Stream (intent) is { } one)
                    uris.Add (one);
                if (uris.Count == 0 && intent.Action == Intent.ActionSendMultiple)
                    uris.AddRange (Streams (intent));
            }
            if (uris.Count == 0)
                return;
            var resolver = ContentResolver!;
            OpenedBooks.Deliver ([.. uris.Select (uri => new PickedFile (NameOf (uri), uri.ToString () ?? "",
                _ => Task.FromResult (resolver.OpenInputStream (uri) ?? throw new IOException ("The book could not be read."))))]);
        }

        private static global::Android.Net.Uri? Stream (Intent intent)
            => OperatingSystem.IsAndroidVersionAtLeast (33)
                ? intent.GetParcelableExtra (Intent.ExtraStream, Java.Lang.Class.FromType (typeof (global::Android.Net.Uri))) as global::Android.Net.Uri
#pragma warning disable CA1422 // the typed overload is API 33+
                : intent.GetParcelableExtra (Intent.ExtraStream) as global::Android.Net.Uri;
#pragma warning restore CA1422

        private static IEnumerable<global::Android.Net.Uri> Streams (Intent intent)
            => (OperatingSystem.IsAndroidVersionAtLeast (33)
                ? intent.GetParcelableArrayListExtra (Intent.ExtraStream, Java.Lang.Class.FromType (typeof (global::Android.Net.Uri)))
#pragma warning disable CA1422
                : intent.GetParcelableArrayListExtra (Intent.ExtraStream))?.OfType<global::Android.Net.Uri> () ?? [];
#pragma warning restore CA1422

        private string NameOf (global::Android.Net.Uri uri)
        {
            using var cursor = ContentResolver?.Query (uri, [IOpenableColumns.DisplayName], null, null, null);
            return cursor is not null && cursor.MoveToFirst () && cursor.GetString (0) is { Length: > 0 } name ? name : "book.epub";
        }
    }
}
