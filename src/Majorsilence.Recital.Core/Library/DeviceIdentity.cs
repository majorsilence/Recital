using System.Text.Json;
using Majorsilence.Recital.Core.Sync;

namespace Majorsilence.Recital.Core.Library
{
    /// <summary>
    /// This device: a random id made on first run, kept in local app data outside the sync home (a copied
    /// sync folder must not make two devices share an id).
    /// </summary>
    public sealed record DeviceIdentity (string DeviceId, string Name, string Platform)
    {
        public static DeviceIdentity CreateNew ()
            => new (Guid.NewGuid ().ToString ("N"), DefaultName (), CurrentPlatform ());

        /// <summary>Reads <c>device.json</c> from <paramref name="directory"/>, creating it on first run.</summary>
        public static async Task<DeviceIdentity> LoadOrCreateAsync (string directory, CancellationToken ct = default)
        {
            var path = Path.Combine (directory, "device.json");

            if (File.Exists (path)) {
                try {
                    await using var read = new FileStream (path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                    var stored = await JsonSerializer.DeserializeAsync (read, SyncJson.Default.LocalDeviceFile, ct).ConfigureAwait (false);
                    if (stored is not null && SyncPaths.IsValidId (stored.DeviceId))
                        return new DeviceIdentity (stored.DeviceId, stored.Name ?? DefaultName (), CurrentPlatform ());
                } catch (JsonException) {
                    // Unreadable: start again as a new device rather than refuse to run.
                }
            }

            var created = CreateNew ();
            Directory.CreateDirectory (directory);
            await using (var write = new FileStream (path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await JsonSerializer.SerializeAsync (write, new LocalDeviceFile (created.DeviceId, created.Name), SyncJson.Default.LocalDeviceFile, ct).ConfigureAwait (false);
            return created;
        }

        public static string CurrentPlatform ()
            => OperatingSystem.IsBrowser () ? "browser"
            : OperatingSystem.IsAndroid () ? "android"
            : OperatingSystem.IsIOS () ? "ios"
            : OperatingSystem.IsWindows () ? "windows"
            : OperatingSystem.IsMacOS () ? "macos"
            : OperatingSystem.IsLinux () ? "linux"
            : "other";

        private static string DefaultName ()
        {
            try {
                return OperatingSystem.IsBrowser () ? "Browser" : Environment.MachineName;
            } catch (InvalidOperationException) {
                return CurrentPlatform ();
            }
        }
    }

    /// <summary>The local, never-synced <c>device.json</c>.</summary>
    public sealed record LocalDeviceFile (string DeviceId, string? Name);
}
