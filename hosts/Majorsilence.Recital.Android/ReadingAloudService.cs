using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Majorsilence.Recital.Android
{
    /// <summary>
    /// A media-playback foreground service while Recital reads aloud: its notification says what is being read,
    /// and while it runs Android keeps the app going with the screen off or another app in front, as it does for a
    /// music player. It does nothing else; the reading itself goes on in the app.
    /// </summary>
    [Service (Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
    public sealed class ReadingAloudService : Service
    {
        private const int NotificationId = 1;
        private const string Channel = "reading-aloud";
        private const string TitleExtra = "title";

        public static void Start (Context context, string title)
        {
            var intent = new Intent (context, typeof (ReadingAloudService)).PutExtra (TitleExtra, title);
            if (OperatingSystem.IsAndroidVersionAtLeast (26))
                context.StartForegroundService (intent);
            else
                context.StartService (intent);
        }

        public static void Stop (Context context) => context.StopService (new Intent (context, typeof (ReadingAloudService)));

        public override IBinder? OnBind (Intent? intent) => null;

        public override StartCommandResult OnStartCommand (Intent? intent, StartCommandFlags flags, int startId)
        {
            var title = intent?.GetStringExtra (TitleExtra) ?? "Recital";
            var manager = (NotificationManager) GetSystemService (NotificationService)!;
            if (OperatingSystem.IsAndroidVersionAtLeast (26))
                manager.CreateNotificationChannel (new NotificationChannel (Channel, "Reading aloud", NotificationImportance.Low) {
                    Description = "Shown while Recital reads a book aloud",
                });

            // Tapping it brings Recital back.
            var open = PackageManager?.GetLaunchIntentForPackage (PackageName!) is { } launch
                ? PendingIntent.GetActivity (this, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)
                : null;
            var builder = OperatingSystem.IsAndroidVersionAtLeast (26) ? new Notification.Builder (this, Channel) : new Notification.Builder (this);
            var notification = builder
                .SetContentTitle ("Reading aloud")!
                .SetContentText (title)!
                .SetSmallIcon (global::Android.Resource.Drawable.IcMediaPlay)!
                .SetOngoing (true)!
                .SetContentIntent (open)!
                .Build ()!;

            if (OperatingSystem.IsAndroidVersionAtLeast (29))
                StartForeground (NotificationId, notification, ForegroundService.TypeMediaPlayback);
            else
                StartForeground (NotificationId, notification);
            return StartCommandResult.NotSticky;
        }
    }
}
