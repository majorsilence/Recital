using System.Globalization;

namespace Majorsilence.Recital.Storage
{
    /// <summary>
    /// Files held in memory. Used by tests, by the sync simulations, and on the browser until it gets a
    /// persistent store. Its change feed is exact: every write gets the next sequence number.
    /// </summary>
    public sealed class InMemoryStorageProvider : IStorageProvider
    {
        private readonly object gate = new ();
        private readonly Dictionary<string, Item> files = new (StringComparer.Ordinal);
        private readonly TimeProvider time;
        private long sequence;

        public InMemoryStorageProvider (TimeProvider? time = null)
        {
            this.time = time ?? TimeProvider.System;
        }

        public string Id => "memory";

        public StorageCapabilities Capabilities => StorageCapabilities.ChangeFeed | StorageCapabilities.ETags | StorageCapabilities.Browse;

        public Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            var entries = new Dictionary<string, StorageEntry> (StringComparer.Ordinal);

            lock (gate) {
                foreach (var (path, item) in files) {
                    if (path == folder || !StoragePath.IsWithin (path, folder))
                        continue;

                    var rest = folder.Length == 0 ? path : path[(folder.Length + 1)..];
                    var slash = rest.IndexOf ('/');
                    if (slash < 0) {
                        entries[path] = item.Entry (path);
                    } else {
                        var child = folder.Length == 0 ? rest[..slash] : folder + "/" + rest[..slash];
                        entries.TryAdd (child, new StorageEntry (child, true, 0, item.Modified, null));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<StorageEntry>> (entries.Values.OrderBy (e => e.Path, StringComparer.Ordinal).ToList ());
        }

        public Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            lock (gate) {
                if (!files.TryGetValue (path, out var item))
                    throw new FileNotFoundException ($"No file '{path}'.", path);
                return Task.FromResult<Stream> (new MemoryStream (item.Data, writable: false));
            }
        }

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0)
                throw new ArgumentException ("Cannot write to the root.", nameof (path));

            var buffer = new MemoryStream ();
            await content.CopyToAsync (buffer, ct).ConfigureAwait (false);

            lock (gate) {
                if (ifMatchEtag is not null && (!files.TryGetValue (path, out var current) || current.ETag != ifMatchEtag))
                    throw new StoragePreconditionFailedException (path);

                if (files.Keys.Any (p => StoragePath.IsWithin (p, path) && p != path) || IsFile (StoragePath.Parent (path)))
                    throw new IOException ($"'{path}' clashes with an existing file or folder.");

                var item = new Item (buffer.ToArray (), time.GetUtcNow (), ++sequence);
                files[path] = item;
                return item.Entry (path);
            }
        }

        public Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            lock (gate) {
                if (files.TryGetValue (path, out var item))
                    return Task.FromResult<StorageEntry?> (item.Entry (path));

                var inside = files.Where (f => StoragePath.IsWithin (f.Key, path)).ToList ();
                return Task.FromResult (inside.Count == 0 ? null : new StorageEntry (path, true, 0, inside.Max (f => f.Value.Modified), null));
            }
        }

        public Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            var since = cursor is null ? 0 : long.Parse (cursor, NumberStyles.None, CultureInfo.InvariantCulture);
            lock (gate) {
                var changed = files.Where (f => f.Value.Sequence > since)
                    .OrderBy (f => f.Value.Sequence)
                    .Select (f => f.Value.Entry (f.Key))
                    .ToList ();
                return Task.FromResult (new ChangeSet (changed, sequence.ToString (CultureInfo.InvariantCulture), cursor is null));
            }
        }

        private bool IsFile (string path)
        {
            for (; path.Length > 0; path = StoragePath.Parent (path))
                if (files.ContainsKey (path))
                    return true;
            return false;
        }

        private sealed record Item (byte[] Data, DateTimeOffset Modified, long Sequence)
        {
            public string ETag => Sequence.ToString (CultureInfo.InvariantCulture);

            public StorageEntry Entry (string path) => new (path, false, Data.Length, Modified, ETag);
        }
    }
}
