using System.Text.Json;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Core
{
    /// <summary>
    /// What the app runs on: this device, the library (a local copy of the sync home, in app data), and,
    /// when a sync home is chosen (a folder, WebDAV, Dropbox, OneDrive or Google Drive), the sync engine
    /// keeping the two in step in the background.
    /// </summary>
    public sealed class AppServices : IDisposable, IAsyncDisposable
    {
        /// <summary>How often the sync home is polled for other devices' changes.</summary>
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds (30);

        private const string SyncHomeFile = "sync-home.json";

        private readonly CancellationTokenSource stop = new ();
        private readonly Task? sync_loop;

        public AppServices (LibraryService library, SyncEngine? sync = null, string? dataDirectory = null, SyncHomeSettings? syncHome = null, PlatformServices? platform = null, IStorageProvider? remote = null)
        {
            Library = library;
            Sync = sync;
            DataDirectory = dataDirectory;
            SyncHome = syncHome is { EffectiveKind: not SyncHomeKind.None } ? syncHome : null;
            Platform = platform ?? PlatformServices.InMemory;
            Remote = remote;
            if (sync is not null)
                sync_loop = sync.RunAsync (PollInterval, stop.Token);
        }

        public LibraryService Library { get; }
        public DeviceIdentity Device => Library.Device;

        /// <summary>The sync engine, or null while the library is on this device only.</summary>
        public SyncEngine? Sync { get; }

        /// <summary>The sync home's provider (for browsing it to import), or null.</summary>
        public IStorageProvider? Remote { get; }

        /// <summary>Local app data, or null where there is none (the browser, tests).</summary>
        public string? DataDirectory { get; }

        /// <summary>Where this device syncs, or null.</summary>
        public SyncHomeSettings? SyncHome { get; }

        public PlatformServices Platform { get; }

        /// <summary>The local folder chosen as the sync home, or null (none, or a cloud one).</summary>
        public string? SyncFolder => SyncHome?.EffectiveKind == SyncHomeKind.LocalFolder ? SyncHome.Folder : null;

        /// <summary>
        /// Whether a local folder can be the sync home here: on the desktop, where Dropbox, Drive, OneDrive
        /// and iCloud have desktop clients that keep such a folder in step.
        /// </summary>
        public bool CanUseSyncFolder => DataDirectory is not null
            && (OperatingSystem.IsWindows () || OperatingSystem.IsMacOS () || OperatingSystem.IsLinux ())
            && !OperatingSystem.IsAndroid ();

        /// <summary>Whether a sync home can be chosen at all: there is local app data to record it in.</summary>
        public bool CanChooseSyncHome => DataDirectory is not null;

        /// <summary>Whether this build and device can sign in to <paramref name="kind"/>.</summary>
        public bool CanConnect (SyncHomeKind kind) => kind switch {
            SyncHomeKind.LocalFolder => CanUseSyncFolder,
            SyncHomeKind.WebDav => CanChooseSyncHome,
            SyncHomeKind.None => true,
            _ => CanChooseSyncHome && Platform.Broker is not null && Platform.Clients.OAuthFor (kind) is not null,
        };

        /// <summary>Stops the sync loop without waiting for it.</summary>
        public void Dispose ()
        {
            stop.Cancel ();
            Library.Dispose ();
        }

        /// <summary>Stops the sync loop and waits for a round in progress to finish.</summary>
        public async ValueTask DisposeAsync ()
        {
            stop.Cancel ();
            if (sync_loop is not null)
                await sync_loop.ConfigureAwait (false);
            Sync?.Dispose ();
            Library.Dispose ();
            stop.Dispose ();
        }

        /// <summary>
        /// Where local app data lives (<c>.../Recital</c> under the OS's local application data), or null
        /// where there is no persistent file system (the browser, until Phase 10 adds one).
        /// </summary>
        public static string? DefaultDataDirectory ()
        {
            if (OperatingSystem.IsBrowser ())
                return null;
            var baseDirectory = Environment.GetFolderPath (Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            return string.IsNullOrEmpty (baseDirectory) ? null : Path.Combine (baseDirectory, "Recital");
        }

        /// <summary>
        /// The library in a LocalFolder provider under app data (in memory when there is no app data
        /// directory), synced with the chosen sync home if there is one.
        /// </summary>
        public static async Task<AppServices> OpenAsync (string? dataDirectory, PlatformServices? platform = null, CancellationToken ct = default)
        {
            platform ??= PlatformServices.InMemory;
            if (dataDirectory is null)
                return await InMemoryAsync (ct: ct).ConfigureAwait (false);

            var device = await DeviceIdentity.LoadOrCreateAsync (dataDirectory, ct).ConfigureAwait (false);
            var local = new LocalFolderStorageProvider (Path.Combine (dataDirectory, "library"));
            var home = await ReadSyncHomeAsync (dataDirectory, ct).ConfigureAwait (false);

            SyncEngine? sync = null;
            IStorageProvider storage = local;
            var remote = home is null ? null : await Core.Sync.SyncHome.OpenAsync (home, platform, ct).ConfigureAwait (false);
            if (remote is not null) {
                sync = new SyncEngine (local, remote, home!.RemoteKey, device.DeviceId);
                await sync.LoadAsync (ct).ConfigureAwait (false);
                storage = sync.Library;
            }

            var library = new LibraryService (storage, device);
            await library.InitializeAsync (ct).ConfigureAwait (false);
            return new AppServices (library, sync, dataDirectory, home, platform, remote);
        }

        public static async Task<AppServices> InMemoryAsync (IStorageProvider? storage = null, DeviceIdentity? device = null, TimeProvider? time = null, CancellationToken ct = default)
        {
            var library = new LibraryService (storage ?? new InMemoryStorageProvider (time), device ?? DeviceIdentity.CreateNew (), time);
            await library.InitializeAsync (ct).ConfigureAwait (false);
            return new AppServices (library);
        }

        /// <summary>The sync home recorded in <paramref name="dataDirectory"/>, or null.</summary>
        public static async Task<SyncHomeSettings?> ReadSyncHomeAsync (string dataDirectory, CancellationToken ct = default)
        {
            var path = Path.Combine (dataDirectory, SyncHomeFile);
            if (!File.Exists (path))
                return null;
            try {
                await using var stream = new FileStream (path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var settings = await JsonSerializer.DeserializeAsync (stream, SyncJson.Default.SyncHomeSettings, ct).ConfigureAwait (false);
                if (settings is null || settings.EffectiveKind == SyncHomeKind.None)
                    return null;
                return settings.EffectiveKind == SyncHomeKind.LocalFolder ? settings with { Folder = Path.GetFullPath (settings.Folder!) } : settings;
            } catch (JsonException) {
                return null;
            }
        }

        /// <summary>
        /// Records <paramref name="settings"/> as this device's sync home (null: none), in local app data, never in
        /// the sync home. Takes effect the next time services are opened.
        /// </summary>
        /// <exception cref="ArgumentException">A local folder inside local app data.</exception>
        public static async Task SetSyncHomeAsync (string dataDirectory, SyncHomeSettings? settings, CancellationToken ct = default)
        {
            if (settings?.EffectiveKind == SyncHomeKind.LocalFolder) {
                var folder = Path.GetFullPath (settings.Folder!);
                // Syncing app data with itself would make the local copy and the sync home the same files.
                if (IsSameOrInside (folder, Path.GetFullPath (dataDirectory)))
                    throw new ArgumentException ("The sync folder cannot be Recital's own app data folder.", nameof (settings));
                settings = settings with { Folder = folder, Kind = SyncHomeKind.LocalFolder };
            }
            settings ??= new SyncHomeSettings (SyncPaths.FormatVersion, null, SyncHomeKind.None);

            Directory.CreateDirectory (dataDirectory);
            await using var stream = new FileStream (Path.Combine (dataDirectory, SyncHomeFile), FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await JsonSerializer.SerializeAsync (stream, settings, SyncJson.Default.SyncHomeSettings, ct).ConfigureAwait (false);
        }

        /// <summary>The local sync folder recorded in <paramref name="dataDirectory"/>, or null.</summary>
        public static async Task<string?> ReadSyncFolderAsync (string dataDirectory, CancellationToken ct = default)
            => (await ReadSyncHomeAsync (dataDirectory, ct).ConfigureAwait (false)) is { EffectiveKind: SyncHomeKind.LocalFolder } home ? home.Folder : null;

        /// <summary>Records a local folder as the sync home (null: none).</summary>
        /// <exception cref="ArgumentException">The folder is inside local app data.</exception>
        public static Task SetSyncFolderAsync (string dataDirectory, string? folder, CancellationToken ct = default)
            => SetSyncHomeAsync (dataDirectory, folder is null ? null : new SyncHomeSettings (SyncPaths.FormatVersion, folder, SyncHomeKind.LocalFolder), ct);

        private static bool IsSameOrInside (string path, string folder)
        {
            var comparison = OperatingSystem.IsLinux () ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var p = Path.TrimEndingDirectorySeparator (path);
            var f = Path.TrimEndingDirectorySeparator (folder);
            return p.Equals (f, comparison) || p.StartsWith (f + Path.DirectorySeparatorChar, comparison);
        }
    }
}
