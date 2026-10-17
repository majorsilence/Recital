using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Core.Sync
{
    /// <summary>
    /// Keeps this device's library (a local copy of the sync home) and the sync home itself in step
    /// (plan 2.3, 2.4). The app reads and writes only the local copy, through <see cref="Library"/>, so it
    /// works the same with no network. Every write the app makes is recorded in a persistent outbox first;
    /// a sync round pushes the outbox, then pulls whatever other devices wrote.
    /// </summary>
    /// <remarks>
    /// <para>Pushing: this device's own files overwrite the sync home's copy (no other device writes them).
    /// Write-once files are written only when the sync home has none; when it already has one, the sync
    /// home's copy wins and is pulled over the local one, so two devices importing the same book end up
    /// with the same <c>meta/</c> file.</para>
    /// <para>Pulling: every round lists the sync home (or reads its change feed) and copies each file whose
    /// ETag differs from the one last copied: other devices' files, conflicted copies of them, and
    /// write-once files. This device's own files are never pulled, except to restore one the local copy
    /// lost. Nothing is ever deleted on either side.</para>
    /// <para>Merging is not done here: the library service reads every device's files and merges by HLC,
    /// so once two devices hold the same files they show the same state.</para>
    /// </remarks>
    public sealed class SyncEngine : IDisposable
    {
        /// <summary>The longest wait between retries while the sync home cannot be reached.</summary>
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes (5);

        private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds (2);
        private static readonly TimeSpan WriteDebounce = TimeSpan.FromSeconds (2);

        private readonly IStorageProvider local;
        private readonly IStorageProvider remote;
        private readonly string device_id;
        private readonly TimeProvider time;
        private readonly string outbox_path;
        private readonly string index_path;
        private readonly SemaphoreSlim round_gate = new (1, 1);
        private readonly SemaphoreSlim outbox_gate = new (1, 1);
        private readonly object signal_gate = new ();
        private TaskCompletionSource requested = new (TaskCreationOptions.RunContinuationsAsynchronously);

        // Path -> sequence number of the latest local write waiting to be pushed.
        private Dictionary<string, long> outbox = new (StringComparer.Ordinal);
        private long sequence;
        private SyncIndexFile index;
        private bool loaded;

        /// <param name="local">This device's copy of the sync home (in local app data).</param>
        /// <param name="remote">The sync home.</param>
        /// <param name="remoteKey">Names the sync home (provider and folder), so switching to another starts a fresh index.</param>
        public SyncEngine (IStorageProvider local, IStorageProvider remote, string remoteKey, string deviceId, TimeProvider? time = null)
        {
            this.local = local;
            this.remote = remote;
            device_id = deviceId;
            this.time = time ?? TimeProvider.System;
            RemoteKey = remoteKey;

            var key = Convert.ToHexStringLower (SHA256.HashData (Encoding.UTF8.GetBytes (remoteKey)), 0, 8);
            outbox_path = StoragePath.Combine ("sync", key, "outbox.json");
            index_path = StoragePath.Combine ("sync", key, "index.json");
            index = new SyncIndexFile (SyncPaths.FormatVersion, remoteKey, false, null, new Dictionary<string, string> (StringComparer.Ordinal));
            Library = new OutboxStorageProvider (local, this);
        }

        public string RemoteKey { get; }

        /// <summary>The local copy, for the library service: every write through it is queued for the sync home.</summary>
        public IStorageProvider Library { get; }

        public SyncStatus Status { get; private set; } = new (SyncState.Idle, null, null, 0);

        /// <summary>Raised (on a worker thread) after every round, successful or not.</summary>
        public event EventHandler<SyncRoundEventArgs>? RoundCompleted;

        /// <summary>Local writes not yet in the sync home.</summary>
        public int PendingCount {
            get {
                lock (signal_gate)
                    return outbox.Count;
            }
        }

        /// <summary>Reads the outbox and index this device saved for the sync home.</summary>
        public async Task LoadAsync (CancellationToken ct = default)
        {
            if (await ReadAsync (outbox_path, SyncJson.Default.SyncOutboxFile, ct).ConfigureAwait (false) is { } saved) {
                lock (signal_gate) {
                    outbox = new Dictionary<string, long> (saved.Paths, StringComparer.Ordinal);
                    sequence = Math.Max (saved.Sequence, outbox.Values.DefaultIfEmpty ().Max ());
                }
            }
            if (await ReadAsync (index_path, SyncJson.Default.SyncIndexFile, ct).ConfigureAwait (false) is { } savedIndex && savedIndex.Remote == RemoteKey)
                index = savedIndex with { Pulled = new Dictionary<string, string> (savedIndex.Pulled, StringComparer.Ordinal) };
            loaded = true;
            Status = Status with { Pending = PendingCount };
        }

        /// <summary>Asks for a round soon (after a short pause, so a burst of writes goes in one round).</summary>
        public void RequestSync ()
        {
            lock (signal_gate)
                requested.TrySetResult ();
        }

        /// <summary>
        /// One round: push the outbox, then pull what changed. Throws when the sync home cannot be reached
        /// or holds a newer format; whatever was pushed or pulled before that is kept.
        /// </summary>
        /// <exception cref="SyncFormatException">The sync home was written by a newer Recital.</exception>
        public async Task<SyncRound> SyncOnceAsync (CancellationToken ct = default)
        {
            if (!loaded)
                await LoadAsync (ct).ConfigureAwait (false);

            await round_gate.WaitAsync (ct).ConfigureAwait (false);
            var pushed = new List<string> ();
            var pulled = new List<string> ();
            try {
                Status = Status with { State = SyncState.Syncing };
                await CheckFormatAsync (ct).ConfigureAwait (false);

                if (!index.Attached) {
                    await AttachAsync (ct).ConfigureAwait (false);
                    index = index with { Attached = true };
                    await SaveIndexAsync (ct).ConfigureAwait (false);
                }

                try {
                    await PushAsync (pushed, ct).ConfigureAwait (false);
                    await PullAsync (pulled, ct).ConfigureAwait (false);
                } finally {
                    if (pushed.Count > 0 || pulled.Count > 0)
                        await SaveIndexAsync (CancellationToken.None).ConfigureAwait (false);
                }

                var round = new SyncRound (pushed, pulled, null);
                Status = new SyncStatus (SyncState.Idle, time.GetUtcNow (), null, PendingCount);
                RoundCompleted?.Invoke (this, new SyncRoundEventArgs (round));
                return round;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                Status = new SyncStatus (ex is SyncFormatException ? SyncState.NewerFormat : SyncState.Offline, Status.LastSuccess, ex.Message, PendingCount);
                RoundCompleted?.Invoke (this, new SyncRoundEventArgs (new SyncRound (pushed, pulled, ex)));
                throw;
            } finally {
                round_gate.Release ();
            }
        }

        /// <summary>
        /// Syncs until cancelled: every <paramref name="pollInterval"/>, soon after each local write, and,
        /// while the sync home cannot be reached, with exponential backoff.
        /// </summary>
        public async Task RunAsync (TimeSpan pollInterval, CancellationToken ct)
        {
            var failures = 0;
            while (!ct.IsCancellationRequested) {
                TimeSpan wait;
                try {
                    await SyncOnceAsync (ct).ConfigureAwait (false);
                    failures = 0;
                    wait = pollInterval;
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception) {
                    // Offline, a newer format, a damaged file: try again later. The status says why.
                    failures++;
                    wait = BackoffDelay (failures, Random.Shared.NextDouble ());
                }

                try {
                    await WaitAsync (wait, honourRequests: failures == 0, ct).ConfigureAwait (false);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }

        /// <summary>
        /// How long to wait after <paramref name="failures"/> failed rounds in a row: doubling from 2 s to
        /// <see cref="MaxBackoff"/>, then scaled by a jitter (0..1 maps to 50..100%) so devices that lost the
        /// network together do not retry together.
        /// </summary>
        public static TimeSpan BackoffDelay (int failures, double jitter)
        {
            var exponent = Math.Clamp (failures - 1, 0, 30);
            var delay = Math.Min (FirstBackoff.TotalMilliseconds * Math.Pow (2, exponent), MaxBackoff.TotalMilliseconds);
            return TimeSpan.FromMilliseconds (delay * (0.5 + 0.5 * Math.Clamp (jitter, 0, 1)));
        }

        public void Dispose ()
        {
            round_gate.Dispose ();
            outbox_gate.Dispose ();
        }

        /// <summary>Records a local write before it happens (so a crash in between still pushes it).</summary>
        internal async Task EnqueueAsync (string path, CancellationToken ct)
        {
            var kind = SyncPaths.Classify (path, device_id);
            if (kind is not (SyncFileKind.Own or SyncFileKind.WriteOnce))
                return;

            lock (signal_gate)
                outbox[path] = ++sequence;
            await SaveOutboxAsync (ct).ConfigureAwait (false);
        }

        internal void WriteCompleted () => RequestSync ();

        private async Task WaitAsync (TimeSpan wait, bool honourRequests, CancellationToken ct)
        {
            Task signal;
            lock (signal_gate) {
                if (requested.Task.IsCompleted)
                    requested = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
                signal = requested.Task;
            }

            var timer = Task.Delay (wait, time, ct);
            if (!honourRequests) {
                await timer.ConfigureAwait (false);
                return;
            }

            if (await Task.WhenAny (timer, signal).ConfigureAwait (false) == signal && !timer.IsCompleted)
                await Task.Delay (WriteDebounce, time, ct).ConfigureAwait (false);
            ct.ThrowIfCancellationRequested ();
        }

        private async Task CheckFormatAsync (CancellationToken ct)
        {
            if (await remote.StatAsync (SyncPaths.Format, ct).ConfigureAwait (false) is not { IsFolder: false })
                return;
            FormatFile? format;
            try {
                await using var stream = await remote.OpenReadAsync (SyncPaths.Format, ct).ConfigureAwait (false);
                format = await JsonSerializer.DeserializeAsync (stream, SyncJson.Default.FormatFile, ct).ConfigureAwait (false);
            } catch (FileNotFoundException) {
                return;
            } catch (JsonException) {
                // Damaged: the files themselves are still readable one by one, so carry on.
                return;
            }
            if (format?.FormatVersion > SyncPaths.FormatVersion)
                throw new SyncFormatException (format.FormatVersion);
        }

        /// <summary>First round with this sync home: queue everything this device has that belongs there.</summary>
        private async Task AttachAsync (CancellationToken ct)
        {
            var mine = new List<string> ();
            await foreach (var entry in ListFilesAsync (local, SyncPaths.Root, ct).ConfigureAwait (false))
                if (SyncPaths.Classify (entry.Path, device_id) is SyncFileKind.Own or SyncFileKind.WriteOnce)
                    mine.Add (entry.Path);

            if (mine.Count == 0)
                return;
            lock (signal_gate)
                foreach (var path in mine)
                    if (!outbox.ContainsKey (path))
                        outbox[path] = ++sequence;
            await SaveOutboxAsync (ct).ConfigureAwait (false);
        }

        private async Task PushAsync (List<string> pushed, CancellationToken ct)
        {
            List<KeyValuePair<string, long>> pending;
            lock (signal_gate)
                pending = outbox.OrderBy (p => TransferOrder (p.Key)).ThenBy (p => p.Key, StringComparer.Ordinal).ToList ();

            foreach (var (path, seq) in pending) {
                ct.ThrowIfCancellationRequested ();
                var kind = SyncPaths.Classify (path, device_id);

                if (await local.StatAsync (path, ct).ConfigureAwait (false) is { IsFolder: false }) {
                    if (kind == SyncFileKind.Own) {
                        var entry = await CopyAsync (local, remote, path, ct).ConfigureAwait (false);
                        if (entry is not null)
                            pushed.Add (path);
                    } else if (kind == SyncFileKind.WriteOnce && await remote.StatAsync (path, ct).ConfigureAwait (false) is null) {
                        // Another device can write the same path between the stat and the write (no store offers
                        // create-if-absent). Then the last write wins in the sync home, and this device pulls it
                        // next round, because its index holds the ETag it wrote rather than the winner's.
                        if (await CopyAsync (local, remote, path, ct).ConfigureAwait (false) is { } entry) {
                            index.Pulled[path] = RemoteETag (entry);
                            pushed.Add (path);
                        }
                    }
                    // A write-once file the sync home already has: its copy wins, and the pull brings it here.
                }

                var removed = false;
                lock (signal_gate) {
                    if (outbox.TryGetValue (path, out var current) && current == seq) {
                        outbox.Remove (path);
                        removed = true;
                    }
                }
                if (removed)
                    await SaveOutboxAsync (ct).ConfigureAwait (false);
            }
        }

        private async Task PullAsync (List<string> pulled, CancellationToken ct)
        {
            var useFeed = remote.Capabilities.HasFlag (StorageCapabilities.ChangeFeed);
            List<StorageEntry> candidates;
            string? cursor = null;
            if (useFeed) {
                var changes = await remote.GetChangesAsync (index.Cursor, ct).ConfigureAwait (false);
                candidates = changes.Changed.Where (e => !e.IsFolder && StoragePath.IsWithin (e.Path, SyncPaths.Root)).ToList ();
                cursor = changes.Cursor;
            } else {
                candidates = [];
                await foreach (var entry in ListFilesAsync (remote, SyncPaths.Root, ct).ConfigureAwait (false))
                    candidates.Add (entry);
            }

            // Books and covers before the meta files that make them show up in the library, so a round cut short
            // never lists a book this device cannot open.
            foreach (var entry in candidates.OrderBy (e => TransferOrder (e.Path)).ThenBy (e => e.Path, StringComparer.Ordinal)) {
                ct.ThrowIfCancellationRequested ();
                var kind = SyncPaths.Classify (entry.Path, device_id);
                if (kind == SyncFileKind.Ignored)
                    continue;

                var etag = RemoteETag (entry);
                if (index.Pulled.TryGetValue (entry.Path, out var known) && known == etag)
                    continue;

                if (kind == SyncFileKind.Own || kind == SyncFileKind.WriteOnce && entry.Path.StartsWith (SyncPaths.BooksFolder + "/", StringComparison.Ordinal)) {
                    // This device's own file is its own business (only restored if lost); a book is named by its
                    // hash, so a local copy is already the same bytes.
                    if (await local.StatAsync (entry.Path, ct).ConfigureAwait (false) is { IsFolder: false }) {
                        index.Pulled[entry.Path] = etag;
                        continue;
                    }
                }

                if (await CopyAsync (remote, local, entry.Path, ct).ConfigureAwait (false) is null)
                    continue;
                // The ETag listed, not the one read: if the file changed in between, the next round sees a
                // different ETag again and copies it again, rather than missing the change.
                index.Pulled[entry.Path] = etag;
                pulled.Add (entry.Path);
            }

            if (useFeed)
                index = index with { Cursor = cursor };
        }

        /// <summary>Copies one file; null when it vanished first.</summary>
        private static async Task<StorageEntry?> CopyAsync (IStorageProvider from, IStorageProvider to, string path, CancellationToken ct)
        {
            byte[] bytes;
            try {
                await using var stream = await from.OpenReadAsync (path, ct).ConfigureAwait (false);
                var buffer = new MemoryStream ();
                await stream.CopyToAsync (buffer, ct).ConfigureAwait (false);
                bytes = buffer.ToArray ();
            } catch (FileNotFoundException) {
                return null;
            }
            return await to.WriteAsync (path, new MemoryStream (bytes, writable: false), null, ct).ConfigureAwait (false);
        }

        /// <summary>Every file under <paramref name="folder"/>, depth first.</summary>
        private static async IAsyncEnumerable<StorageEntry> ListFilesAsync (IStorageProvider provider, string folder, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var folders = new Stack<string> ();
            folders.Push (folder);
            while (folders.TryPop (out var next)) {
                foreach (var entry in await provider.ListAsync (next, ct).ConfigureAwait (false)) {
                    if (entry.IsFolder)
                        folders.Push (entry.Path);
                    else
                        yield return entry;
                }
            }
        }

        private static int TransferOrder (string path)
        {
            if (path.StartsWith (SyncPaths.BooksFolder + "/", StringComparison.Ordinal))
                return 0;
            if (path.StartsWith (SyncPaths.Root + "/covers/", StringComparison.Ordinal))
                return 1;
            if (path.StartsWith (SyncPaths.MetaFolder + "/", StringComparison.Ordinal))
                return 2;
            return 3;
        }

        private static string RemoteETag (StorageEntry entry)
            => entry.ETag ?? FormattableString.Invariant ($"{entry.Modified.UtcTicks:x}-{entry.Size:x}");

        private async Task SaveOutboxAsync (CancellationToken ct)
        {
            await outbox_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                SyncOutboxFile file;
                lock (signal_gate)
                    file = new SyncOutboxFile (SyncPaths.FormatVersion, sequence, new Dictionary<string, long> (outbox, StringComparer.Ordinal));
                await WriteAsync (outbox_path, file, SyncJson.Default.SyncOutboxFile, ct).ConfigureAwait (false);
            } finally {
                outbox_gate.Release ();
            }
        }

        private Task SaveIndexAsync (CancellationToken ct)
            => WriteAsync (index_path, index, SyncJson.Default.SyncIndexFile, ct);

        private async Task<T?> ReadAsync<T> (string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) where T : class
        {
            try {
                await using var stream = await local.OpenReadAsync (path, ct).ConfigureAwait (false);
                return await JsonSerializer.DeserializeAsync (stream, type, ct).ConfigureAwait (false);
            } catch (FileNotFoundException) {
                return null;
            } catch (JsonException) {
                // Damaged: start over. The next round re-pulls (harmless) and the library's next writes re-queue.
                return null;
            }
        }

        private async Task WriteAsync<T> (string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
            => await local.WriteAsync (path, new MemoryStream (JsonSerializer.SerializeToUtf8Bytes (value, type), writable: false), null, ct).ConfigureAwait (false);
    }

    /// <summary>
    /// The local copy as the library service sees it: each write is recorded in the engine's outbox before
    /// it happens, then the engine is asked for a round.
    /// </summary>
    internal sealed class OutboxStorageProvider : IStorageProvider
    {
        private readonly IStorageProvider inner;
        private readonly SyncEngine engine;

        public OutboxStorageProvider (IStorageProvider inner, SyncEngine engine)
        {
            this.inner = inner;
            this.engine = engine;
        }

        public string Id => inner.Id;
        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default) => inner.ListAsync (folderPath, ct);
        public Task<Stream> OpenReadAsync (string path, CancellationToken ct = default) => inner.OpenReadAsync (path, ct);
        public Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default) => inner.StatAsync (path, ct);
        public Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default) => inner.GetChangesAsync (cursor, ct);

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            await engine.EnqueueAsync (path, ct).ConfigureAwait (false);
            var entry = await inner.WriteAsync (path, content, ifMatchEtag, ct).ConfigureAwait (false);
            engine.WriteCompleted ();
            return entry;
        }
    }

    public enum SyncState
    {
        Idle,
        Syncing,
        /// <summary>The last round could not reach the sync home; it is retried with backoff.</summary>
        Offline,
        /// <summary>The sync home was written by a newer Recital; nothing is written to it.</summary>
        NewerFormat,
    }

    /// <param name="LastSuccess">When a round last completed, by this device's clock.</param>
    /// <param name="LastError">Why the last round failed, or null.</param>
    /// <param name="Pending">Local writes not yet in the sync home.</param>
    public sealed record SyncStatus (SyncState State, DateTimeOffset? LastSuccess, string? LastError, int Pending);

    /// <param name="Pushed">Files written to the sync home.</param>
    /// <param name="Pulled">Files copied from it: other devices' changes.</param>
    /// <param name="Error">Why the round stopped early, or null.</param>
    public sealed record SyncRound (IReadOnlyList<string> Pushed, IReadOnlyList<string> Pulled, Exception? Error);

    public sealed class SyncRoundEventArgs (SyncRound round) : EventArgs
    {
        public SyncRound Round { get; } = round;
    }

    /// <summary>The sync home's <c>format.json</c> names a version this build does not understand.</summary>
    public sealed class SyncFormatException (int version)
        : Exception ($"The sync folder was written by a newer version of Recital (format {version}). Update Recital to sync with it.")
    {
        public int Version { get; } = version;
    }

    /// <summary><c>sync/&lt;key&gt;/outbox.json</c> in local app data: paths waiting to be pushed.</summary>
    public sealed record SyncOutboxFile (int FormatVersion, long Sequence, IReadOnlyDictionary<string, long> Paths);

    /// <summary><c>sync/&lt;key&gt;/index.json</c> in local app data: the sync home's ETag for each file last copied.</summary>
    /// <param name="Attached">This device's existing files have been queued for this sync home.</param>
    /// <param name="Cursor">The change-feed cursor, for providers with one.</param>
    public sealed record SyncIndexFile (int FormatVersion, string Remote, bool Attached, string? Cursor, Dictionary<string, string> Pulled);
}
