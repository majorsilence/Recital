using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Core.Library
{
    /// <summary>
    /// The library in the sync home (docs/SYNC_FORMAT.md): importing books, listing them, and reading and
    /// saving this device's progress and preferences. It writes only write-once content-addressed files
    /// (<c>books/</c>, <c>meta/</c>, <c>covers/</c>) and this device's own files (plan 2.3).
    /// </summary>
    public sealed class LibraryService : IDisposable
    {
        private readonly IStorageProvider storage;
        private readonly HlcClock clock;
        private readonly TimeProvider time;
        private readonly SemaphoreSlim write_gate = new (1, 1);

        // Parsed files by path, reused while the file's ETag is unchanged.
        private readonly Dictionary<string, (string? ETag, object? Value)> cache = new (StringComparer.Ordinal);
        private readonly object cache_gate = new ();

        public LibraryService (IStorageProvider storage, DeviceIdentity device, TimeProvider? time = null)
        {
            this.storage = storage;
            Device = device;
            this.time = time ?? TimeProvider.System;
            clock = new HlcClock (device.DeviceId, this.time);
        }

        public DeviceIdentity Device { get; }
        public IStorageProvider Storage => storage;

        /// <summary>Writes <c>format.json</c> if the sync home is new, and this device's <c>devices/</c> file.</summary>
        public async Task InitializeAsync (CancellationToken ct = default)
        {
            if (await storage.StatAsync (SyncPaths.Format, ct).ConfigureAwait (false) is null)
                await WriteJsonAsync (SyncPaths.Format, new FormatFile (SyncPaths.FormatVersion), SyncJson.Default.FormatFile, ct).ConfigureAwait (false);

            await WriteJsonAsync (SyncPaths.Device (Device.DeviceId),
                new DeviceFile (SyncPaths.FormatVersion, Device.DeviceId, Device.Name, Device.Platform, time.GetUtcNow ()),
                SyncJson.Default.DeviceFile, ct).ConfigureAwait (false);
        }

        /// <summary>Every book in the library, with its newest progress.</summary>
        public async Task<IReadOnlyList<LibraryBook>> ListAsync (CancellationToken ct = default)
        {
            var books = new List<LibraryBook> ();
            foreach (var entry in await storage.ListAsync (SyncPaths.MetaFolder, ct).ConfigureAwait (false)) {
                if (entry.IsFolder || !entry.Name.EndsWith (".json", StringComparison.Ordinal))
                    continue;
                // Conflicted copies of a write-once file say the same thing; only the canonical name counts.
                var id = entry.Name[..^5];
                if (!SyncPaths.IsValidId (id))
                    continue;

                var meta = await ReadJsonAsync (entry, SyncJson.Default.BookMeta, ct).ConfigureAwait (false);
                if (meta is null || meta.BookId != id)
                    continue;

                books.Add (new LibraryBook (meta, await GetProgressAsync (id, ct).ConfigureAwait (false)));
            }
            return books;
        }

        /// <summary>
        /// Adds a book. The id is the SHA-256 of the file, so importing the same file again (here or on
        /// another device) finds the existing entry rather than making a second.
        /// </summary>
        /// <exception cref="BookImportException">Not an EPUB, or DRM-protected.</exception>
        public async Task<ImportResult> ImportAsync (Stream epub, string? fileName = null, CancellationToken ct = default)
        {
            var buffer = new MemoryStream ();
            await epub.CopyToAsync (buffer, ct).ConfigureAwait (false);
            var bytes = buffer.ToArray ();
            var id = Convert.ToHexStringLower (SHA256.HashData (bytes));

            if (await ReadMetaAsync (id, ct).ConfigureAwait (false) is { } existing)
                return new ImportResult (new LibraryBook (existing, await GetProgressAsync (id, ct).ConfigureAwait (false)), AlreadyInLibrary: true);

            EpubBook book;
            try {
                book = await EpubBook.OpenAsync (new MemoryStream (bytes, writable: false), leaveOpen: false, ct).ConfigureAwait (false);
            } catch (EpubException ex) {
                throw new BookImportException ($"{fileName ?? "The file"} is not a readable EPUB: {ex.Message}", ex);
            }

            await using (book.ConfigureAwait (false)) {
                if (book.IsDrmProtected)
                    throw new BookImportException ($"{book.Metadata.Title} is DRM-protected, and Recital cannot open DRM-protected books.");

                byte[]? cover = null;
                if (book.Cover is { } coverItem) {
                    try {
                        cover = CoverThumbnail.Create (await book.ReadResourceAsync (coverItem, ct).ConfigureAwait (false));
                    } catch (Exception ex) when (ex is FileNotFoundException or EpubException) {
                        cover = null;
                    }
                }

                var title = book.Metadata.Title is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension (fileName) ?? "Untitled";
                var meta = new BookMeta {
                    BookId = id,
                    Title = title.Trim (),
                    Authors = book.Metadata.Authors.Select (a => a.Name.Trim ()).Where (n => n.Length > 0).ToList (),
                    Language = book.Metadata.Language,
                    Identifier = book.Metadata.UniqueIdentifier,
                    FileSize = bytes.Length,
                    FileName = fileName is null ? null : Path.GetFileName (fileName),
                    HasCover = cover is not null,
                    AddedAt = time.GetUtcNow (),
                    AddedByDevice = Device.DeviceId,
                };

                // The book and cover first, the meta file last: a library listing only shows books whose
                // meta exists, so an interrupted import is never half-visible.
                if (await storage.StatAsync (SyncPaths.Book (id), ct).ConfigureAwait (false) is null)
                    await storage.WriteAsync (SyncPaths.Book (id), new MemoryStream (bytes, writable: false), null, ct).ConfigureAwait (false);
                if (cover is not null)
                    await storage.WriteAsync (SyncPaths.Cover (id), new MemoryStream (cover, writable: false), null, ct).ConfigureAwait (false);
                await WriteJsonAsync (SyncPaths.Meta (id), meta, SyncJson.Default.BookMeta, ct).ConfigureAwait (false);

                return new ImportResult (new LibraryBook (meta, null), AlreadyInLibrary: false);
            }
        }

        /// <summary>Opens a library book for reading. The caller owns (disposes) the result.</summary>
        public async Task<EpubBook> OpenBookAsync (string bookId, CancellationToken ct = default)
        {
            var stream = await storage.OpenReadAsync (SyncPaths.Book (bookId), ct).ConfigureAwait (false);
            return await EpubBook.OpenAsync (stream, leaveOpen: false, ct).ConfigureAwait (false);
        }

        /// <summary>The cover thumbnail JPEG, or null when the book has none.</summary>
        public async Task<byte[]?> ReadCoverAsync (string bookId, CancellationToken ct = default)
        {
            try {
                await using var stream = await storage.OpenReadAsync (SyncPaths.Cover (bookId), ct).ConfigureAwait (false);
                var buffer = new MemoryStream ();
                await stream.CopyToAsync (buffer, ct).ConfigureAwait (false);
                return buffer.ToArray ();
            } catch (FileNotFoundException) {
                return null;
            }
        }

        /// <summary>
        /// The newest progress across every device's state file for the book (highest HLC wins), including
        /// any conflicted copies a sync client left beside them.
        /// </summary>
        public async Task<ReadingProgress?> GetProgressAsync (string bookId, CancellationToken ct = default)
        {
            ReadingProgress? best = null;
            foreach (var entry in await storage.ListAsync (SyncPaths.StateFolder (bookId), ct).ConfigureAwait (false)) {
                if (entry.IsFolder || !entry.Name.EndsWith (".json", StringComparison.Ordinal))
                    continue;
                var state = await ReadJsonAsync (entry, SyncJson.Default.BookStateFile, ct).ConfigureAwait (false);
                if (state?.BookId != bookId || state.Progress is not { } progress)
                    continue;
                clock.Observe (progress.Hlc);
                if (best is null || progress.Hlc > best.Hlc)
                    best = progress;
            }
            return best;
        }

        /// <summary>
        /// Records this device's position in a book. It outranks (by HLC) every position for the book this
        /// device has seen, whatever the clocks say: the clock observes them all first, this device's own
        /// earlier saves included, which matters after a restart on a device whose clock is behind.
        /// </summary>
        public async Task<ReadingProgress> SaveProgressAsync (string bookId, ReadingLocator locator, CancellationToken ct = default)
        {
            await write_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                await GetProgressAsync (bookId, ct).ConfigureAwait (false);
                var path = SyncPaths.State (bookId, Device.DeviceId);
                var current = await StatAndReadAsync (path, SyncJson.Default.BookStateFile, ct).ConfigureAwait (false);
                var progress = new ReadingProgress (locator, clock.Now (), time.GetUtcNow ());
                var state = new BookStateFile (SyncPaths.FormatVersion, bookId, Device.DeviceId, progress, current?.Annotations ?? []);
                await WriteJsonAsync (path, state, SyncJson.Default.BookStateFile, ct).ConfigureAwait (false);
                return progress;
            } finally {
                write_gate.Release ();
            }
        }

        /// <summary>Preferences merged across devices: for each key, the value with the highest HLC.</summary>
        public async Task<ReaderPreferences> LoadPreferencesAsync (CancellationToken ct = default)
        {
            var merged = await MergedSettingsAsync (ct).ConfigureAwait (false);
            return ReaderPreferences.FromValues (merged.ToDictionary (kv => kv.Key, kv => kv.Value.Value));
        }

        /// <summary>Saves the keys that differ from the merged view, stamped with a new HLC.</summary>
        public async Task SavePreferencesAsync (ReaderPreferences preferences, CancellationToken ct = default)
        {
            await write_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                var merged = await MergedSettingsAsync (ct).ConfigureAwait (false);
                var path = SyncPaths.Settings (Device.DeviceId);
                var own = (await StatAndReadAsync (path, SyncJson.Default.SettingsFile, ct).ConfigureAwait (false))?.Values
                    ?? new Dictionary<string, SettingValue> ();

                var values = new Dictionary<string, SettingValue> (own, StringComparer.Ordinal);
                var defaults = new ReaderPreferences ().ToValues ();
                var changed = false;
                foreach (var (key, value) in preferences.ToValues ()) {
                    // Unchanged, or still the default nobody has set: nothing to record.
                    var current = merged.TryGetValue (key, out var existing) ? existing.Value : defaults[key];
                    if (current == value)
                        continue;
                    values[key] = new SettingValue (value, clock.Now ());
                    changed = true;
                }

                if (changed)
                    await WriteJsonAsync (path, new SettingsFile (SyncPaths.FormatVersion, Device.DeviceId, values), SyncJson.Default.SettingsFile, ct).ConfigureAwait (false);
            } finally {
                write_gate.Release ();
            }
        }

        public void Dispose () => write_gate.Dispose ();

        private async Task<Dictionary<string, SettingValue>> MergedSettingsAsync (CancellationToken ct)
        {
            var merged = new Dictionary<string, SettingValue> (StringComparer.Ordinal);
            foreach (var entry in await storage.ListAsync (SyncPaths.SettingsFolder, ct).ConfigureAwait (false)) {
                if (entry.IsFolder || !entry.Name.EndsWith (".json", StringComparison.Ordinal))
                    continue;
                if (await ReadJsonAsync (entry, SyncJson.Default.SettingsFile, ct).ConfigureAwait (false) is not { } file)
                    continue;
                foreach (var (key, value) in file.Values) {
                    clock.Observe (value.Hlc);
                    if (!merged.TryGetValue (key, out var best) || value.Hlc > best.Hlc)
                        merged[key] = value;
                }
            }
            return merged;
        }

        private async Task<BookMeta?> ReadMetaAsync (string bookId, CancellationToken ct)
            => await StatAndReadAsync (SyncPaths.Meta (bookId), SyncJson.Default.BookMeta, ct).ConfigureAwait (false);

        private async Task<T?> StatAndReadAsync<T> (string path, JsonTypeInfo<T> type, CancellationToken ct) where T : class
            => await storage.StatAsync (path, ct).ConfigureAwait (false) is { IsFolder: false } entry
                ? await ReadJsonAsync (entry, type, ct).ConfigureAwait (false)
                : null;

        /// <summary>Reads and parses a JSON file, or null when it is missing or not valid (another build's, or damaged).</summary>
        private async Task<T?> ReadJsonAsync<T> (StorageEntry entry, JsonTypeInfo<T> type, CancellationToken ct) where T : class
        {
            lock (cache_gate) {
                if (entry.ETag is not null && cache.TryGetValue (entry.Path, out var hit) && hit.ETag == entry.ETag)
                    return hit.Value as T;
            }

            T? value;
            try {
                await using var stream = await storage.OpenReadAsync (entry.Path, ct).ConfigureAwait (false);
                value = await JsonSerializer.DeserializeAsync (stream, type, ct).ConfigureAwait (false);
            } catch (FileNotFoundException) {
                return null;
            } catch (JsonException) {
                value = null;
            }

            lock (cache_gate)
                cache[entry.Path] = (entry.ETag, value);
            return value;
        }

        private async Task WriteJsonAsync<T> (string path, T value, JsonTypeInfo<T> type, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes (value, type);
            var entry = await storage.WriteAsync (path, new MemoryStream (bytes, writable: false), null, ct).ConfigureAwait (false);
            lock (cache_gate)
                cache[path] = (entry.ETag, value);
        }
    }
}
