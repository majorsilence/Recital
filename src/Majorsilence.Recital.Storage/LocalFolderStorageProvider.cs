using System.Globalization;
using System.Security.Cryptography;

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
    /// reported more than once, which callers must tolerate; and a sync client that keeps the original
    /// write time can deliver a file dated before the cursor, so the sync engine diffs full listings by
    /// ETag instead.
    ///
    /// ETags of files up to <see cref="HashedFileLimit"/> (state, settings, meta, covers) are a hash of the
    /// content. A write time is not enough there: file systems stamp writes with a coarse clock (a few
    /// milliseconds on ext4, two seconds on FAT), so two same-sized writes in one tick would share an ETag
    /// and a poller would miss the second. Larger files (books, which are write-once) use write time and
    /// size. Hashes are cached and reused only for files whose write time is well before the hash was
    /// taken, the rule git uses for "racy" index entries.
    /// </remarks>
    public sealed class LocalFolderStorageProvider : IStorageProvider
    {
        /// <summary>Files up to this size get content-hash ETags.</summary>
        public const long HashedFileLimit = 256 * 1024;

        private const string TempSuffix = ".recital-tmp";
        private static readonly TimeSpan ClockMargin = TimeSpan.FromSeconds (2);

        private readonly string root;
        private readonly object hash_gate = new ();
        private readonly Dictionary<string, CachedHash> hashes = new (StringComparer.Ordinal);

        private readonly bool require_root;

        /// <param name="requireRoot">
        /// Treat a missing root folder as unreachable storage (every call throws
        /// <see cref="DirectoryNotFoundException"/>) rather than creating it: for a sync folder on a drive
        /// that may not be mounted, where creating it would start an empty library in the mount point.
        /// </param>
        public LocalFolderStorageProvider (string rootDirectory, bool requireRoot = false)
        {
            root = Path.GetFullPath (rootDirectory);
            require_root = requireRoot;
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

                var entries = new List<StorageEntry> ();
                foreach (var info in directory.EnumerateFileSystemInfos ()) {
                    ct.ThrowIfCancellationRequested ();
                    if (!IsTemporary (info) && TryLogical (info.FullName, out var path))
                        entries.Add (Entry (path, info));
                }
                return entries.OrderBy (e => e.Path, StringComparer.Ordinal).ToList ();
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
            // Hashed on the way through, so the returned ETag costs no second read.
            using var hash = IncrementalHash.CreateHash (HashAlgorithmName.SHA256);
            try {
                await using (var target = new FileStream (temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true)) {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await content.ReadAsync (buffer, ct).ConfigureAwait (false)) > 0) {
                        hash.AppendData (buffer, 0, read);
                        await target.WriteAsync (buffer.AsMemory (0, read), ct).ConfigureAwait (false);
                    }
                }

                // Checked as late as possible; another process can still write in between (no file system
                // offers compare-and-swap), which is why each device only ever writes its own files.
                if (ifMatchEtag is not null && (await StatAsync (path, ct).ConfigureAwait (false))?.ETag != ifMatchEtag)
                    throw new StoragePreconditionFailedException (path);

                var hashedAt = DateTimeOffset.UtcNow.UtcTicks;
                File.Move (temp, physical, overwrite: true);

                var file = new FileInfo (physical);
                var modified = new DateTimeOffset (file.LastWriteTimeUtc, TimeSpan.Zero);
                if (file.Length > HashedFileLimit)
                    return new StorageEntry (path, false, file.Length, modified, TimeAndSize (modified, file.Length));

                var etag = HashETag (hash.GetHashAndReset ());
                lock (hash_gate)
                    hashes[file.FullName] = new CachedHash (modified.UtcTicks, file.Length, hashedAt, etag);
                return new StorageEntry (path, false, file.Length, modified, etag);
            } finally {
                if (File.Exists (temp))
                    File.Delete (temp);
            }
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
                var directory = new DirectoryInfo (Physical (string.Empty));
                var all = directory.Exists
                    ? directory.EnumerateFiles ("*", SearchOption.AllDirectories).Where (f => !IsTemporary (f)).ToList ()
                    : [];

                var changed = new List<StorageEntry> ();
                foreach (var file in all) {
                    ct.ThrowIfCancellationRequested ();
                    if (file.LastWriteTimeUtc >= since.UtcDateTime && TryLogical (file.FullName, out var path))
                        changed.Add (Entry (path, file));
                }

                return new ChangeSet (
                    changed.OrderBy (e => e.Modified).ThenBy (e => e.Path, StringComparer.Ordinal).ToList (),
                    scanStart.UtcTicks.ToString (CultureInfo.InvariantCulture),
                    cursor is null);
            }, ct);
        }

        private string Physical (string logical)
        {
            if (require_root && !Directory.Exists (root))
                throw new DirectoryNotFoundException ($"The folder '{root}' is not there (is its drive connected?).");
            return PhysicalUnchecked (logical);
        }

        private string PhysicalUnchecked (string logical)
            => logical.Length == 0 ? root : Path.Combine (root, logical.Replace ('/', Path.DirectorySeparatorChar));

        /// <summary>The logical path of a file under the root; false for a name no logical path can hold.</summary>
        private bool TryLogical (string physical, out string logical)
        {
            try {
                logical = StoragePath.Normalize (Path.GetRelativePath (root, physical));
                return true;
            } catch (ArgumentException) {
                // A file someone else put here with a name like "a:b.json": not ours, so not listed.
                logical = string.Empty;
                return false;
            }
        }

        private static bool IsTemporary (FileSystemInfo info) => info.Name.EndsWith (TempSuffix, StringComparison.Ordinal);

        private StorageEntry Entry (string path, FileSystemInfo info)
        {
            if (info is FileInfo file) {
                var modified = new DateTimeOffset (file.LastWriteTimeUtc, TimeSpan.Zero);
                return new StorageEntry (path, false, file.Length, modified, ETag (file, modified));
            }

            return new StorageEntry (path, true, 0, new DateTimeOffset (info.LastWriteTimeUtc, TimeSpan.Zero), null);
        }

        private string ETag (FileInfo file, DateTimeOffset modified)
        {
            var timeAndSize = TimeAndSize (modified, file.Length);
            if (file.Length > HashedFileLimit)
                return timeAndSize;

            lock (hash_gate) {
                if (hashes.TryGetValue (file.FullName, out var cached) && cached.Modified == modified.UtcTicks && cached.Size == file.Length
                    && modified.UtcTicks < cached.HashedAt - ClockMargin.Ticks)
                    return cached.ETag;
            }

            var hashedAt = DateTimeOffset.UtcNow.UtcTicks;
            byte[] hash;
            try {
                // Shared with writers: a sync client may be replacing the file; that shows up as a new ETag next poll.
                using var stream = new FileStream (file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                hash = SHA256.HashData (stream);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return timeAndSize;
            }

            var etag = HashETag (hash);
            lock (hash_gate)
                hashes[file.FullName] = new CachedHash (modified.UtcTicks, file.Length, hashedAt, etag);
            return etag;
        }

        private static string TimeAndSize (DateTimeOffset modified, long size)
            => string.Create (CultureInfo.InvariantCulture, $"{modified.UtcTicks:x}-{size:x}");

        private static string HashETag (byte[] hash) => "h" + Convert.ToHexStringLower (hash, 0, 12);

        private readonly record struct CachedHash (long Modified, long Size, long HashedAt, string ETag);
    }
}
