using System.Diagnostics;

namespace Majorsilence.Recital.Platform
{
    /// <summary>Hands a web address to the system's default browser (or mail client).</summary>
    public static class SystemBrowser
    {
        // Only schemes it is reasonable to hand the OS for a link found in a book: never file:, javascript: or app schemes.
        private static readonly string[] Allowed = ["http", "https", "mailto"];

        public static bool CanOpen (Uri uri) => uri.IsAbsoluteUri && Allowed.Contains (uri.Scheme.ToLowerInvariant ());

        /// <summary>Opens <paramref name="uri"/>; false (never an exception) when it is not allowed or nothing could open it.</summary>
        public static bool Open (Uri uri)
        {
            if (!CanOpen (uri) || OperatingSystem.IsBrowser () || OperatingSystem.IsAndroid () || OperatingSystem.IsIOS ())
                return false;
            try {
                Launch (uri);
                return true;
            } catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException) {
                return false;
            }
        }

        internal static void Launch (Uri uri)
        {
            // UseShellExecute hands the URL to the OS: the default browser on Windows and macOS, xdg-open on Linux.
            if (OperatingSystem.IsLinux ())
                Process.Start (new ProcessStartInfo ("xdg-open", uri.AbsoluteUri) { UseShellExecute = false })?.Dispose ();
            else
                Process.Start (new ProcessStartInfo (uri.AbsoluteUri) { UseShellExecute = true })?.Dispose ();
        }
    }
}
