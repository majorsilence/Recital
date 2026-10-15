using System.Globalization;

namespace Majorsilence.Recital.Storage
{
    /// <summary>
    /// A folder on this device: app data, or a Dropbox / Drive / OneDrive / iCloud desktop-client folder
    /// (plan 2.5). Writes go to a temporary file first and are then moved over the target, so a reader (or
    /// the sync client) never sees half a file.
    /// </summary>
    /// <remarks>
    /// There is no change feed: <see cref="GetChangesAsync"/> lists every file and reports those written at
    /// or after the cursor's time, less a margin for coarse file-system clocks. A file can therefore be
    /// reported more than once, which callers must tolerate. ETags are derived from the write time and size.
    /// </remarks>
    public sealed class LocalFolderStorageProvider : IStorageProvider
    {
        private const string TempSuffix = ".recital-tmp";
        private static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds (2);

        private readonly string root;

        public LocalFolderStorageProvider (string rootDirectory)
        {
            root = Path.GetFullPath (rootDirectory);
        }

        public string Id => "local";

        public string RootDirectory => root;

        public StorageCapabilities Capabilities => StorageCapabilities.ETags | StorageCapabilities.Browse;

        public Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            return Task.Run<IReadOnlyList<StorageEntry>> (() => {
                var directory = new DirectoryInfo (Physical (folder));
                if (!directory.Exists)
                    return [];

                return directory.EnumerateFileSystemInfos ()
                    .Where (info => !IsTemporary (info))
                    .Select (info => Entry (StoragePath.Combine (folder, info.Name), info))
                    .OrderBy (e => e.Path, StringComparer.Ordinal)
                    .ToList ();
            }, ct);
        }

        public Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            var physical = Physical (StoragePath.Normalize (path));
            // Opening is cheap; FileStream with useAsync makes the reads themselves asynchronous.
            if (!File.Exists (physical))
                throw new FileNotFoundException ($"No file '{path}'.", path);
            return Task.FromResult<Stream> (new FileStream (physical, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
        }

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0)
                throw new ArgumentException ("Cannot write to the root.", nameof (path));

            var physical = Physical (path);
            Directory.CreateDirectory (Path.GetDirectoryName (physical)!);

            var temp = Path.Combine (Path.GetDirectoryName (physical)!, "." + Path.GetFileName (physical) + "." + Guid.NewGuid ().ToString ("N") + TempSuffix);
            try {
                await using (var target = new FileStream (temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    await content.CopyToAsync (target, ct).ConfigureAwait (false);

                // Checked as late as possible; another process can still write in between (no file system
                // offers compare-and-swap), which is why each device only ever writes its own files.
                if (ifMatchEtag is not null && (await StatAsync (path, ct).ConfigureAwait (false))?.ETag != ifMatchEtag)
                    throw new StoragePreconditionFailedException (path);

                File.Move (temp, physical, overwrite: true);
            } finally {
                if (File.Exists (temp))
                    File.Delete (temp);
            }

            return Entry (path, new FileInfo (physical));
        }

        public Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            return Task.Run (() => {
                var physical = Physical (path);
                FileSystemInfo info = File.Exists (physical) ? new FileInfo (physical) : new DirectoryInfo (physical);
                return info.Exists ? Entry (path, info) : null;
            }, ct);
        }

        public Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            var since = cursor is null ? DateTimeOffset.MinValue
                : new DateTimeOffset (long.Parse (cursor, NumberStyles.None, CultureInfo.InvariantCulture), TimeSpan.Zero) - ClockMargin;

            return Task.Run (() => {
                // The next cursor is the scan's start, so a write landing during the scan is reported next time.
                var scanStart = DateTimeOffset.UtcNow;
                var directory = new DirectoryInfo (root);
                var all = directory.Exists
                    ? directory.EnumerateFiles ("*", SearchOption.AllDirectories).Where (f => !IsTemporary (f)).ToList ()
                    : [];

                var changed = all.Where (f => f.LastWriteTimeUtc >= since.UtcDateTime)
                    .Select (f => Entry (Logical (f.FullName), f))
                    .OrderBy (e => e.Modified)
                    .ThenBy (e => e.Path, StringComparer.Ordinal)
                    .ToList ();

                return new ChangeSet (changed, scanStart.UtcTicks.ToString (CultureInfo.InvariantCulture), cursor is null);
            }, ct);
        }

        private string Physical (string logical)
            => logical.Length == 0 ? root : Path.Combine (root, logical.Replace ('/', Path.DirectorySeparatorChar));

        private string Logical (string physical)
            => StoragePath.Normalize (Path.GetRelativePath (root, physical));

        private static bool IsTemporary (FileSystemInfo info) => info.Name.EndsWith (TempSuffix, StringComparison.Ordinal);

        private static StorageEntry Entry (string path, FileSystemInfo info)
        {
            if (info is FileInfo file) {
                var modified = new DateTimeOffset (file.LastWriteTimeUtc, TimeSpan.Zero);
                return new StorageEntry (path, false, file.Length, modified,
                    string.Create (CultureInfo.InvariantCulture, $"{modified.UtcTicks:x}-{file.Length:x}"));
            }

            return new StorageEntry (path, true, 0, new DateTimeOffset (info.LastWriteTimeUtc, TimeSpan.Zero), null);
        }
    }
}
