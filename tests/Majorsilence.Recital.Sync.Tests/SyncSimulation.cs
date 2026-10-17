using System.Text;
using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Sync.Tests
{
    /// <summary>
    /// Three devices sharing a sync home in a real local folder (plan Phase 5): one seeded random run of
    /// imports, page turns, preference changes and sync rounds, in random order, with devices going
    /// offline, flaky networks (including writes that land but report failure), restarts, clocks skewed
    /// by up to ±10 minutes, and a sync client leaving conflicted copies (current and stale) beside device
    /// files, sometimes with the stale version put back under the real name. Then every device comes
    /// online and syncs, and all three must show the same library, positions and preferences: the newest
    /// of everything any device saved.
    /// </summary>
    internal sealed class SyncSimulation : IDisposable
    {
        public const int DeviceCount = 3;

        private static readonly Lazy<byte[][]> BookPool = new (() => Enumerable.Range (1, 4).Select (MakeBook).ToArray ());
        private static readonly string[] Themes = ["light", "sepia", "dark", "black"];

        private readonly int seed;
        private readonly Random random;
        private readonly string root;
        private readonly LocalFolderStorageProvider shared;
        private readonly SimHistory history = new ();
        private readonly List<Device> devices = [];
        private readonly HashSet<string> imported = new (StringComparer.Ordinal);
        private readonly Dictionary<string, ReadingProgress> newest = new (StringComparer.Ordinal);
        private readonly Dictionary<Guid, (string Book, Annotation Version)> annotation_versions = [];
        private readonly HashSet<Guid> deleted = [];
        private readonly StringBuilder log = new ();
        private DateTimeOffset now = new (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        private int conflict_counter;

        public SyncSimulation (int seed)
        {
            this.seed = seed;
            random = new Random (seed);
            root = Path.Combine (Path.GetTempPath (), "recital-sync-sim", Guid.NewGuid ().ToString ("N"));
            shared = new LocalFolderStorageProvider (root);
            for (var i = 0; i < DeviceCount; i++) {
                var id = Convert.ToHexStringLower (BitConverter.GetBytes (random.NextInt64 ())) + Convert.ToHexStringLower (BitConverter.GetBytes (random.NextInt64 ()));
                var skew = TimeSpan.FromMilliseconds (random.Next (-600_000, 600_001));
                devices.Add (new Device (id, "D" + (i + 1), new SimClock (() => now, skew), new InMemoryStorageProvider (), new DeviceStorage (new LocalFolderStorageProvider (root), id, random, history, Coarsen)));
            }
        }

        /// <summary>Runs <paramref name="steps"/> random steps, converges, and returns a description of what went wrong, or null.</summary>
        public async Task<string?> RunAsync (int steps, CancellationToken ct)
        {
            foreach (var device in devices)
                await device.StartAsync (ct);

            for (var step = 0; step < steps; step++) {
                now += TimeSpan.FromMilliseconds (random.Next (0, 3000));
                var device = devices[random.Next (devices.Count)];
                var roll = random.NextDouble ();
                if (roll < 0.08)
                    await ImportAsync (device, ct);
                else if (roll < 0.25)
                    await TurnPageAsync (device, ct);
                else if (roll < 0.35)
                    await AnnotateAsync (device, ct);
                else if (roll < 0.47)
                    await ChangePreferenceAsync (device, ct);
                else if (roll < 0.75)
                    await SyncAsync (device, ct);
                else if (roll < 0.81)
                    Log (device, (device.Remote.Offline = !device.Remote.Offline) ? "goes offline" : "comes online");
                else if (roll < 0.85)
                    Log (device, (device.Remote.FailureRate = device.Remote.FailureRate > 0 ? 0 : 0.15) > 0 ? "gets a flaky network" : "network steady");
                else if (roll < 0.89)
                    await RestartAsync (device, ct);
                else if (roll < 0.95)
                    await LeaveConflictedCopyAsync (ct);
                else
                    now += TimeSpan.FromMinutes (random.Next (1, 30));
            }

            // Everyone online, then enough rounds for every write to reach every device: the first pushes
            // everything, the second pulls it everywhere. A third shows nothing more moves.
            foreach (var device in devices) {
                device.Remote.Offline = false;
                device.Remote.FailureRate = 0;
            }
            for (var round = 0; round < 3; round++) {
                now += TimeSpan.FromSeconds (30);
                foreach (var device in devices) {
                    var result = await device.Engine.SyncOnceAsync (ct);
                    if (round == 2 && (result.Pushed.Count > 0 || result.Pulled.Count > 0))
                        return Failure ($"{device.Name} still moving files in the last round: pushed [{string.Join (", ", result.Pushed)}], pulled [{string.Join (", ", result.Pulled)}]");
                }
            }

            return await CheckAsync (ct);
        }

        private async Task<string?> CheckAsync (CancellationToken ct)
        {
            var violations = devices.SelectMany (d => d.Remote.Violations).ToList ();
            if (violations.Count > 0)
                return Failure ("Wrote files it should not have:\n" + string.Join ("\n", violations));

            foreach (var device in devices)
                if (device.Engine.PendingCount > 0)
                    return Failure ($"{device.Name} still has {device.Engine.PendingCount} writes in its outbox");

            var expected = await ExpectedViewAsync (ct);
            foreach (var device in devices) {
                var view = await ViewAsync (device, ct);
                if (view != expected)
                    return Failure ($"{device.Name} did not converge.\nExpected:\n{expected}\nActual:\n{view}");
            }
            return null;
        }

        /// <summary>What every device should show: each imported book, the newest position saved anywhere, the newest value of each preference.</summary>
        private async Task<string> ExpectedViewAsync (CancellationToken ct)
        {
            // Write-once files: the sync home's copy is the one everyone keeps.
            var text = new StringBuilder ();
            foreach (var id in imported.Order (StringComparer.Ordinal)) {
                var meta = await ReadAsync<BookMeta> (shared, SyncPaths.Meta (id), ct);
                newest.TryGetValue (id, out var progress);
                text.AppendLine (Describe (meta!, progress));
                var live = annotation_versions.Where (v => v.Value.Book == id && !deleted.Contains (v.Key)).Select (v => v.Value.Version);
                text.Append (Describe (AnnotationMerge.Live (live)));
            }

            // Each device's own settings file (in its local copy, the authority) holds its newest value per key.
            var values = new Dictionary<string, SettingValue> (StringComparer.Ordinal);
            foreach (var device in devices) {
                var own = await ReadAsync<SettingsFile> (device.Local, SyncPaths.Settings (device.Id), ct);
                foreach (var (key, value) in own?.Values ?? new Dictionary<string, SettingValue> ())
                    if (!values.TryGetValue (key, out var best) || value.Hlc > best.Hlc)
                        values[key] = value;
            }
            text.AppendLine (Describe (ReaderPreferences.FromValues (values.ToDictionary (kv => kv.Key, kv => kv.Value.Value))));
            return text.ToString ();
        }

        private static async Task<string> ViewAsync (Device device, CancellationToken ct)
        {
            var text = new StringBuilder ();
            foreach (var book in (await device.Library.ListAsync (ct)).OrderBy (b => b.Id, StringComparer.Ordinal)) {
                text.AppendLine (Describe (book.Meta, book.Progress));
                text.Append (Describe (await device.Library.GetAnnotationsAsync (book.Id, ct)));
            }
            text.AppendLine (Describe (await device.Library.LoadPreferencesAsync (ct)));
            return text.ToString ();
        }

        private static string Describe (BookMeta meta, ReadingProgress? progress)
            => $"{meta.BookId[..8]} {meta.Title} added {meta.AddedAt:O} by {meta.AddedByDevice?[..6]}: "
                + (progress is null ? "unread" : $"{progress.Locator.Cfi} {progress.Locator.Percent} at {progress.Hlc}");

        private static string Describe (IEnumerable<Annotation> annotations)
            => string.Concat (annotations.Select (a => $"  {a.Id.ToString ()[..8]} {a.Kind} {a.Color} \"{a.Note}\" at {a.Hlc}\n"));

        private static string Describe (ReaderPreferences preferences)
            => "preferences " + string.Join (" ", preferences.ToValues ().OrderBy (kv => kv.Key, StringComparer.Ordinal).Select (kv => $"{kv.Key}={kv.Value}"));

        private async Task ImportAsync (Device device, CancellationToken ct)
        {
            var bytes = BookPool.Value[random.Next (BookPool.Value.Length)];
            var result = await device.Library.ImportAsync (new MemoryStream (bytes), "book.epub", ct);
            imported.Add (result.Book.Id);
            Log (device, $"imports {result.Book.Id[..8]}{(result.AlreadyInLibrary ? " (already there)" : "")}");
        }

        private async Task TurnPageAsync (Device device, CancellationToken ct)
        {
            var books = await device.Library.ListAsync (ct);
            if (books.Count == 0)
                return;
            var book = books[random.Next (books.Count)];
            var spine = random.Next (0, 2);
            var offset = random.Next (0, 200);
            var locator = new ReadingLocator ($"epubcfi(/6/{2 * spine + 2}!/4/2/1:{offset})", spine, offset, Math.Round (random.NextDouble (), 3));
            var progress = await device.Library.SaveProgressAsync (book.Id, locator, ct);
            if (!newest.TryGetValue (book.Id, out var best) || progress.Hlc > best.Hlc)
                newest[book.Id] = progress;
            Log (device, $"reads {book.Id[..8]} to {locator.Percent} at {progress.Hlc}");
        }

        /// <summary>Adds a highlight or bookmark, or changes or deletes one this device can see (or, stale, one it saw once).</summary>
        private async Task AnnotateAsync (Device device, CancellationToken ct)
        {
            var books = await device.Library.ListAsync (ct);
            if (books.Count == 0)
                return;
            var book = books[random.Next (books.Count)];
            var visible = await device.Library.GetAnnotationsAsync (book.Id, ct);
            var roll = random.NextDouble ();

            if (visible.Count == 0 || roll < 0.4) {
                var kind = random.Next (4) == 0 ? AnnotationKind.Bookmark : AnnotationKind.Highlight;
                var offset = random.Next (0, 200);
                var annotation = new Annotation {
                    Id = new Guid (BitConverter.GetBytes (random.NextInt64 ()).Concat (BitConverter.GetBytes (random.NextInt64 ())).ToArray ()),
                    Kind = kind,
                    Cfi = kind == AnnotationKind.Bookmark ? $"epubcfi(/6/2!/4/2/1:{offset})" : $"epubcfi(/6/2!/4/2,/1:{offset},/1:{offset + 20})",
                    CharOffset = offset,
                    Text = "words",
                    Color = kind == AnnotationKind.Highlight ? HighlightColors.All[random.Next (HighlightColors.All.Count)].Name : null,
                };
                Record (book.Id, await device.Library.SaveAnnotationAsync (book.Id, annotation, ct));
                Log (device, $"adds {kind} {annotation.Id.ToString ()[..8]} to {book.Id[..8]}");
                return;
            }

            // Mostly ones it can see; sometimes one it saw earlier and may since have been deleted elsewhere.
            var known = annotation_versions.Where (v => v.Value.Book == book.Id).Select (v => v.Value.Version).ToList ();
            var target = roll < 0.85 || known.Count == 0 ? visible[random.Next (visible.Count)] : known[random.Next (known.Count)];
            if (roll < 0.7) {
                var changed = random.Next (2) == 0
                    ? target with { Color = HighlightColors.All[random.Next (HighlightColors.All.Count)].Name }
                    : target with { Note = "note " + random.Next (1000) };
                var saved = await device.Library.SaveAnnotationAsync (book.Id, changed, ct);
                Record (book.Id, saved);
                Log (device, $"changes {target.Id.ToString ()[..8]}{(saved is null ? " (already deleted)" : "")}");
            } else {
                await device.Library.DeleteAnnotationAsync (book.Id, target.Id, ct);
                deleted.Add (target.Id);
                Log (device, $"deletes {target.Id.ToString ()[..8]}");
            }
        }

        private void Record (string book, Annotation? saved)
        {
            if (saved is null)
                return;
            if (!annotation_versions.TryGetValue (saved.Id, out var best) || saved.Hlc > best.Version.Hlc)
                annotation_versions[saved.Id] = (book, saved);
        }

        private async Task ChangePreferenceAsync (Device device, CancellationToken ct)
        {
            var current = await device.Library.LoadPreferencesAsync (ct);
            var next = random.Next (3) switch {
                0 => current with { Theme = Enum.Parse<ThemeKind> (Themes[random.Next (Themes.Length)], ignoreCase: true) },
                1 => current with { FontSize = random.Next (12, 30) },
                _ => current with { Margins = (MarginSize)random.Next (3) },
            };
            await device.Library.SavePreferencesAsync (next, ct);
            Log (device, "sets " + Describe (next));
        }

        private async Task SyncAsync (Device device, CancellationToken ct)
        {
            try {
                var round = await device.Engine.SyncOnceAsync (ct);
                Log (device, $"syncs: pushed {round.Pushed.Count}, pulled {round.Pulled.Count}");
            } catch (IOException ex) {
                Log (device, "sync fails: " + ex.Message);
            }
        }

        private async Task RestartAsync (Device device, CancellationToken ct)
        {
            device.Stop ();
            await device.StartAsync (ct);
            Log (device, "restarts");
        }

        /// <summary>What Dropbox and friends do when two writes race: keep both, one under another name.</summary>
        private async Task LeaveConflictedCopyAsync (CancellationToken ct)
        {
            var files = new List<StorageEntry> ();
            var folders = new Stack<string> ([SyncPaths.Root]);
            while (folders.TryPop (out var folder))
                foreach (var entry in await shared.ListAsync (folder, ct))
                    if (entry.IsFolder)
                        folders.Push (entry.Path);
                    else if (!entry.Name.Contains (' ', StringComparison.Ordinal))
                        files.Add (entry);
            if (files.Count == 0)
                return;

            var file = files[random.Next (files.Count)];
            var current = await ReadBytesAsync (shared, file.Path, ct);
            var versions = history.Versions (file.Path);
            var stale = versions.Count >= 2 ? versions[random.Next (versions.Count - 1)] : null;

            var name = Path.GetFileNameWithoutExtension (file.Name);
            var extension = Path.GetExtension (file.Name);
            var copy = StoragePath.Combine (StoragePath.Parent (file.Path), (++conflict_counter % 3) switch {
                0 => $"{name} ({conflict_counter}){extension}",
                1 => $"{name} (conflicted copy {conflict_counter}){extension}",
                _ => $"{name} (Laptop's conflicted copy 2026-10-17 {conflict_counter}){extension}",
            });

            var roll = random.NextDouble ();
            if (stale is null || roll < 0.4) {
                await WriteBackdatedAsync (copy, current, ct);
                Log (null, $"sync client copies {file.Path} to {StoragePath.Name (copy)}");
            } else if (roll < 0.7) {
                await WriteBackdatedAsync (copy, stale, ct);
                Log (null, $"sync client leaves a stale {StoragePath.Name (copy)}");
            } else {
                // The newer write lost the race for the real name.
                await WriteBackdatedAsync (copy, current, ct);
                await WriteBackdatedAsync (file.Path, stale, ct);
                Log (null, $"sync client puts a stale {file.Path} back, the current one in {StoragePath.Name (copy)}");
            }
        }

        /// <summary>
        /// Rounds a written file's time down to 2 s, as FAT, exFAT and some network shares store it, so two
        /// writes in a row can share a time (and, often, a size). This machine's file system may be finer.
        /// </summary>
        private void Coarsen (string path)
        {
            var physical = Path.Combine (root, path.Replace ('/', Path.DirectorySeparatorChar));
            var time = File.GetLastWriteTimeUtc (physical);
            File.SetLastWriteTimeUtc (physical, new DateTime (time.Ticks - time.Ticks % (2 * TimeSpan.TicksPerSecond), DateTimeKind.Utc));
        }

        /// <summary>A file written the way a sync client delivers it: with the writing machine's (old) time.</summary>
        private async Task WriteBackdatedAsync (string path, byte[] bytes, CancellationToken ct)
        {
            await shared.WriteAsync (path, new MemoryStream (bytes), null, ct);
            var physical = Path.Combine (root, path.Replace ('/', Path.DirectorySeparatorChar));
            File.SetLastWriteTimeUtc (physical, DateTime.UtcNow - TimeSpan.FromMinutes (random.Next (1, 60)));
        }

        private static async Task<byte[]> ReadBytesAsync (IStorageProvider provider, string path, CancellationToken ct)
        {
            await using var stream = await provider.OpenReadAsync (path, ct);
            var buffer = new MemoryStream ();
            await stream.CopyToAsync (buffer, ct);
            return buffer.ToArray ();
        }

        private static async Task<T?> ReadAsync<T> (IStorageProvider provider, string path, CancellationToken ct) where T : class
        {
            try {
                var bytes = await ReadBytesAsync (provider, path, ct);
                return System.Text.Json.JsonSerializer.Deserialize<T> (bytes, SyncJsonForTests.Options);
            } catch (FileNotFoundException) {
                return null;
            }
        }

        private void Log (Device? device, string message)
            => log.Append (now.ToString ("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append (' ')
                .Append (device?.Name ?? "  ").Append (' ').AppendLine (message);

        private string Failure (string message)
        {
            var tail = log.ToString ().Split ('\n');
            return $"Seed {seed}: {message}\n\nLast steps:\n{string.Join ('\n', tail.TakeLast (60))}"
                + "\nDevices: " + string.Join (", ", devices.Select (d => $"{d.Name}={d.Id[..6]} skew {d.Clock.Skew.TotalSeconds:+0;-0}s"));
        }

        private static byte[] MakeBook (int n)
        {
            var epub = TestEpub.Epub3 (metadata: $"""
                <dc:identifier id="uid">urn:uuid:00000000-0000-0000-0000-00000000000{n}</dc:identifier>
                <dc:title>Book {n}</dc:title>
                <dc:creator>Author {n}</dc:creator>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">2026-10-14T00:00:00Z</meta>
                """);
            return epub.ToStream ().ToArray ();
        }

        public void Dispose ()
        {
            foreach (var device in devices)
                device.Stop ();
            try {
                if (Directory.Exists (root))
                    Directory.Delete (root, recursive: true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Best effort.
            }
        }

        /// <summary>One simulated device. Its local copy outlives restarts, as app data does.</summary>
        private sealed class Device (string id, string name, SimClock clock, InMemoryStorageProvider local, DeviceStorage remote)
        {
            public string Id { get; } = id;
            public string Name { get; } = name;
            public SimClock Clock { get; } = clock;
            public InMemoryStorageProvider Local { get; } = local;
            public DeviceStorage Remote { get; } = remote;
            public SyncEngine Engine { get; private set; } = null!;
            public LibraryService Library { get; private set; } = null!;

            public async Task StartAsync (CancellationToken ct)
            {
                Engine = new SyncEngine (Local, Remote, "sim", Id, Clock);
                await Engine.LoadAsync (ct);
                Library = new LibraryService (Engine.Library, new DeviceIdentity (Id, Name, "linux"), Clock);
                await Library.InitializeAsync (ct);
            }

            public void Stop ()
            {
                // Null when the run failed before this device started.
                Engine?.Dispose ();
                Library?.Dispose ();
            }
        }
    }
}
