using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Core
{
    /// <summary>What the app runs on: this device, and the library in its sync home.</summary>
    public sealed class AppServices : IDisposable
    {
        public AppServices (LibraryService library)
        {
            Library = library;
        }

        public LibraryService Library { get; }
        public DeviceIdentity Device => Library.Device;

        public void Dispose () => Library.Dispose ();

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
        /// The local-only setup until a sync home is chosen (Phase 5+): the library in a LocalFolder provider
        /// under app data, or in memory when there is no app data directory.
        /// </summary>
        public static async Task<AppServices> OpenAsync (string? dataDirectory, CancellationToken ct = default)
        {
            if (dataDirectory is null)
                return await InMemoryAsync (ct: ct).ConfigureAwait (false);

            var device = await DeviceIdentity.LoadOrCreateAsync (dataDirectory, ct).ConfigureAwait (false);
            var library = new LibraryService (new LocalFolderStorageProvider (Path.Combine (dataDirectory, "library")), device);
            await library.InitializeAsync (ct).ConfigureAwait (false);
            return new AppServices (library);
        }

        public static async Task<AppServices> InMemoryAsync (IStorageProvider? storage = null, DeviceIdentity? device = null, TimeProvider? time = null, CancellationToken ct = default)
        {
            var library = new LibraryService (storage ?? new InMemoryStorageProvider (time), device ?? DeviceIdentity.CreateNew (), time);
            await library.InitializeAsync (ct).ConfigureAwait (false);
            return new AppServices (library);
        }
    }
}
