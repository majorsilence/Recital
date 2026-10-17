using System.Text;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.Sync.Tests
{
    public class SyncEngineTests
    {
        private const string Laptop = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Phone = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly ReadingLocator Chapter1 = new ("epubcfi(/6/2!/4/2/1:0)", 0, 0, 0.1);
        private static readonly ReadingLocator Chapter2 = new ("epubcfi(/6/4!/4/2/1:0)", 1, 0, 0.6);

        private readonly InMemoryStorageProvider home = new ();
        private DateTimeOffset now = new (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        private sealed class Device (SyncEngine engine, LibraryService library, InMemoryStorageProvider local) : IDisposable
        {
            public SyncEngine Engine { get; } = engine;
            public LibraryService Library { get; } = library;
            public InMemoryStorageProvider Local { get; } = local;

            public void Dispose ()
            {
                Engine.Dispose ();
                Library.Dispose ();
            }
        }

        private async Task<Device> DeviceAsync (string id, IStorageProvider? remote = null, InMemoryStorageProvider? local = null, TimeSpan skew = default)
        {
            local ??= new InMemoryStorageProvider ();
            var clock = new SimClock (() => now, skew);
            var engine = new SyncEngine (local, remote ?? home, "test", id, clock);
            await engine.LoadAsync (Ct);
            var library = new LibraryService (engine.Library, new DeviceIdentity (id, id[..4], "linux"), clock);
            await library.InitializeAsync (Ct);
            return new Device (engine, library, local);
        }

        private static byte[] Book (string title = "Test Book")
            => TestEpub.Epub3 (metadata: $"""
                <dc:identifier id="uid">urn:uuid:{Guid.NewGuid ()}</dc:identifier>
                <dc:title>{title}</dc:title>
                <dc:language>en</dc:language>
                """).ToStream ().ToArray ();

        [Fact]
        public async Task ABookImportedOnOneDeviceShowsUpOnAnother ()
        {
            using var laptop = await DeviceAsync (Laptop);
            using var phone = await DeviceAsync (Phone);

            var imported = await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct);
            await laptop.Library.SaveProgressAsync (imported.Book.Id, Chapter2, Ct);
            var pushed = await laptop.Engine.SyncOnceAsync (Ct);
            var pulled = await phone.Engine.SyncOnceAsync (Ct);

            Assert.Contains (SyncPaths.Book (imported.Book.Id), pushed.Pushed);
            Assert.Contains (SyncPaths.Meta (imported.Book.Id), pulled.Pulled);
            var book = Assert.Single (await phone.Library.ListAsync (Ct));
            Assert.Equal ("Test Book", book.Title);
            Assert.Equal (Chapter2, book.Progress?.Locator);
            await using var opened = await phone.Library.OpenBookAsync (book.Id, Ct);
            Assert.Equal ("Test Book", opened.Metadata.Title);
        }

        [Fact]
        public async Task ADeletedHighlightStaysDeletedWhenAStaleDeviceReconnects ()
        {
            var tabletLink = new SwitchableStorage (home);
            using var laptop = await DeviceAsync (Laptop);
            using var tablet = await DeviceAsync ("cccccccccccccccccccccccccccccccc", tabletLink);
            var id = (await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct)).Book.Id;
            var highlight = await laptop.Library.SaveAnnotationAsync (id, new Annotation { Id = Guid.NewGuid (), Cfi = "epubcfi(/6/2!/4/2,/1:0,/1:5)", Text = "First", Color = "yellow" }, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);
            await tablet.Engine.SyncOnceAsync (Ct);
            Assert.Single (await tablet.Library.GetAnnotationsAsync (id, Ct));

            // The tablet goes offline and recolours the highlight; meanwhile the laptop deletes it.
            tabletLink.Offline = true;
            now += TimeSpan.FromMinutes (30);
            await tablet.Library.SaveAnnotationAsync (id, highlight! with { Color = "green" }, Ct);
            now -= TimeSpan.FromMinutes (20);
            await laptop.Library.DeleteAnnotationAsync (id, highlight!.Id, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);

            // Days later the tablet reconnects.
            now += TimeSpan.FromDays (3);
            tabletLink.Offline = false;
            await tablet.Engine.SyncOnceAsync (Ct);
            await laptop.Engine.SyncOnceAsync (Ct);

            Assert.Empty (await tablet.Library.GetAnnotationsAsync (id, Ct));
            Assert.Empty (await laptop.Library.GetAnnotationsAsync (id, Ct));
            using var phone = await DeviceAsync (Phone);
            await phone.Engine.SyncOnceAsync (Ct);
            Assert.Empty (await phone.Library.GetAnnotationsAsync (id, Ct));
        }

        [Fact]
        public async Task NothingMovesWhenNothingChanged ()
        {
            using var laptop = await DeviceAsync (Laptop);
            using var phone = await DeviceAsync (Phone);
            await laptop.Engine.SyncOnceAsync (Ct);
            await phone.Engine.SyncOnceAsync (Ct);
            await laptop.Engine.SyncOnceAsync (Ct);

            var again = await laptop.Engine.SyncOnceAsync (Ct);
            Assert.Empty (again.Pushed);
            Assert.Empty (again.Pulled);
            Assert.Equal (0, laptop.Engine.PendingCount);
        }

        [Fact]
        public async Task WritesMadeOfflineWaitInTheOutboxAcrossARestart ()
        {
            var offline = new SwitchableStorage (home) { Offline = true };
            var local = new InMemoryStorageProvider ();
            var laptop = await DeviceAsync (Laptop, offline, local);
            var imported = await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct);
            await laptop.Library.SaveProgressAsync (imported.Book.Id, Chapter1, Ct);
            await Assert.ThrowsAsync<IOException> (() => laptop.Engine.SyncOnceAsync (Ct));
            Assert.Equal (SyncState.Offline, laptop.Engine.Status.State);
            Assert.True (laptop.Engine.PendingCount > 0);
            laptop.Dispose ();

            // The app restarts, the network comes back.
            offline.Offline = false;
            using var restarted = await DeviceAsync (Laptop, offline, local);
            Assert.True (restarted.Engine.PendingCount > 0);
            await restarted.Engine.SyncOnceAsync (Ct);
            Assert.Equal (0, restarted.Engine.PendingCount);
            Assert.Equal (SyncState.Idle, restarted.Engine.Status.State);

            using var phone = await DeviceAsync (Phone);
            await phone.Engine.SyncOnceAsync (Ct);
            Assert.Equal (Chapter1, Assert.Single (await phone.Library.ListAsync (Ct)).Progress?.Locator);
        }

        [Fact]
        public async Task TheSameBookImportedOnTwoDevicesKeepsOneMetaFile ()
        {
            var bytes = Book ();
            using var laptop = await DeviceAsync (Laptop);
            now += TimeSpan.FromMinutes (5);
            using var phone = await DeviceAsync (Phone);
            await laptop.Library.ImportAsync (new MemoryStream (bytes), "laptop.epub", Ct);
            await phone.Library.ImportAsync (new MemoryStream (bytes), "phone.epub", Ct);

            await laptop.Engine.SyncOnceAsync (Ct);
            await phone.Engine.SyncOnceAsync (Ct);

            // The laptop got there first: its meta file is the one both devices keep.
            var onLaptop = Assert.Single (await laptop.Library.ListAsync (Ct)).Meta;
            var onPhone = Assert.Single (await phone.Library.ListAsync (Ct)).Meta;
            Assert.Equal ("laptop.epub", onPhone.FileName);
            Assert.Equal (onLaptop with { Authors = [] }, onPhone with { Authors = [] });
        }

        [Fact]
        public async Task TheLatestPageTurnWinsWhateverTheClocksSay ()
        {
            // The phone's clock runs ten minutes slow; the laptop's ten minutes fast.
            using var laptop = await DeviceAsync (Laptop, skew: TimeSpan.FromMinutes (10));
            using var phone = await DeviceAsync (Phone, skew: TimeSpan.FromMinutes (-10));
            var id = (await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct)).Book.Id;
            await laptop.Library.SaveProgressAsync (id, Chapter1, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);
            await phone.Engine.SyncOnceAsync (Ct);

            // Five minutes later, really, the phone reads on.
            now += TimeSpan.FromMinutes (5);
            await phone.Library.SaveProgressAsync (id, Chapter2, Ct);
            await phone.Engine.SyncOnceAsync (Ct);
            await laptop.Engine.SyncOnceAsync (Ct);

            Assert.Equal (Chapter2, (await laptop.Library.GetProgressAsync (id, Ct))?.Locator);
            Assert.Equal (Chapter2, (await phone.Library.GetProgressAsync (id, Ct))?.Locator);
        }

        [Fact]
        public async Task ASaveAfterARestartOutranksTheDevicesOwnEarlierOne ()
        {
            // The phone saw the laptop's fast clock, saved past it, then restarted: its own clock is far behind.
            using var laptop = await DeviceAsync (Laptop, skew: TimeSpan.FromMinutes (10));
            var local = new InMemoryStorageProvider ();
            var phone = await DeviceAsync (Phone, local: local, skew: TimeSpan.FromMinutes (-10));
            var id = (await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct)).Book.Id;
            await laptop.Library.SaveProgressAsync (id, Chapter1, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);
            await phone.Engine.SyncOnceAsync (Ct);
            var first = await phone.Library.SaveProgressAsync (id, Chapter1 with { CharOffset = 5 }, Ct);
            phone.Dispose ();

            now += TimeSpan.FromSeconds (1);
            using var restarted = await DeviceAsync (Phone, local: local, skew: TimeSpan.FromMinutes (-10));
            var second = await restarted.Library.SaveProgressAsync (id, Chapter2, Ct);

            Assert.True (second.Hlc > first.Hlc, $"{second.Hlc} should be after {first.Hlc}");
        }

        [Fact]
        public async Task ConflictedCopiesOfDeviceFilesAreMergedAndOthersIgnored ()
        {
            using var laptop = await DeviceAsync (Laptop);
            var id = (await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct)).Book.Id;
            await laptop.Library.SaveProgressAsync (id, Chapter1, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);

            // A sync client kept a newer laptop write as a conflicted copy, and copied a meta file too.
            using (var newer = await DeviceAsync (Laptop, remote: new InMemoryStorageProvider ())) {
                await newer.Library.ImportAsync (new MemoryStream (await ReadAsync (home, SyncPaths.Book (id))), "book.epub", Ct);
                now += TimeSpan.FromMinutes (1);
                await newer.Library.SaveProgressAsync (id, Chapter2, Ct);
                var state = await ReadAsync (newer.Local, SyncPaths.State (id, Laptop));
                await home.WriteAsync (SyncPaths.StateFolder (id) + $"/{Laptop} (Laptop's conflicted copy).json", new MemoryStream (state), null, Ct);
            }
            await home.WriteAsync (SyncPaths.MetaFolder + $"/{id} (1).json", new MemoryStream (Encoding.UTF8.GetBytes ("{}")), null, Ct);

            using var phone = await DeviceAsync (Phone);
            var round = await phone.Engine.SyncOnceAsync (Ct);

            Assert.Contains (round.Pulled, p => p.Contains ("conflicted copy", StringComparison.Ordinal));
            Assert.DoesNotContain (round.Pulled, p => p.EndsWith (" (1).json", StringComparison.Ordinal));
            Assert.Equal (Chapter2, Assert.Single (await phone.Library.ListAsync (Ct)).Progress?.Locator);
            // Never deleted, never rewritten.
            Assert.NotNull (await home.StatAsync (SyncPaths.MetaFolder + $"/{id} (1).json", Ct));
        }

        [Fact]
        public async Task AttachingAnExistingLibraryUploadsIt ()
        {
            // A library used on this device only, before any sync folder was chosen.
            var local = new InMemoryStorageProvider ();
            string id;
            using (var alone = new LibraryService (local, new DeviceIdentity (Laptop, "Laptop", "linux"))) {
                await alone.InitializeAsync (Ct);
                id = (await alone.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct)).Book.Id;
                await alone.SaveProgressAsync (id, Chapter2, Ct);
            }

            using var laptop = await DeviceAsync (Laptop, local: local);
            await laptop.Engine.SyncOnceAsync (Ct);

            Assert.NotNull (await home.StatAsync (SyncPaths.Book (id), Ct));
            Assert.NotNull (await home.StatAsync (SyncPaths.State (id, Laptop), Ct));
            Assert.NotNull (await home.StatAsync (SyncPaths.Format, Ct));
        }

        [Fact]
        public async Task AnOwnFileTheLocalCopyLostIsRestored ()
        {
            using (var laptop = await DeviceAsync (Laptop)) {
                await laptop.Library.SavePreferencesAsync (new ReaderPreferences { Theme = ThemeKind.Dark }, Ct);
                await laptop.Engine.SyncOnceAsync (Ct);
            }

            // App data wiped but for the device id (a restore from backup, say).
            using var restored = await DeviceAsync (Laptop);
            await restored.Engine.SyncOnceAsync (Ct);
            Assert.Equal (ThemeKind.Dark, (await restored.Library.LoadPreferencesAsync (Ct)).Theme);
        }

        [Fact]
        public async Task ASyncHomeFromANewerVersionIsLeftAlone ()
        {
            await home.WriteAsync (SyncPaths.Format, new MemoryStream (Encoding.UTF8.GetBytes ("""{ "formatVersion": 2 }""")), null, Ct);
            using var laptop = await DeviceAsync (Laptop);
            await laptop.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct);

            await Assert.ThrowsAsync<SyncFormatException> (() => laptop.Engine.SyncOnceAsync (Ct));
            Assert.Equal (SyncState.NewerFormat, laptop.Engine.Status.State);
            Assert.Null (await home.StatAsync (SyncPaths.Device (Laptop), Ct));
            Assert.Empty (await home.ListAsync (SyncPaths.BooksFolder, Ct));
        }

        [Fact]
        public async Task TheChangeFeedIsUsedWhenTheProviderHasOne ()
        {
            var counting = new SwitchableStorage (home);
            using var laptop = await DeviceAsync (Laptop);
            using var phone = await DeviceAsync (Phone, counting);
            await laptop.Engine.SyncOnceAsync (Ct);
            await phone.Engine.SyncOnceAsync (Ct);
            counting.Lists = 0;

            await laptop.Library.SavePreferencesAsync (new ReaderPreferences { FontSize = 24 }, Ct);
            await laptop.Engine.SyncOnceAsync (Ct);
            var round = await phone.Engine.SyncOnceAsync (Ct);

            Assert.Equal ([SyncPaths.Settings (Laptop)], round.Pulled);
            Assert.Equal (0, counting.Lists);
            Assert.Equal (24, (await phone.Library.LoadPreferencesAsync (Ct)).FontSize);
        }

        [Fact]
        public async Task TheLoopSyncsSoonAfterAWrite ()
        {
            var realHome = new InMemoryStorageProvider ();
            var local = new InMemoryStorageProvider ();
            using var engine = new SyncEngine (local, realHome, "loop", Laptop);
            await engine.LoadAsync (Ct);
            using var library = new LibraryService (engine.Library, new DeviceIdentity (Laptop, "Laptop", "linux"));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource (Ct);
            var loop = engine.RunAsync (TimeSpan.FromHours (1), stop.Token);

            await library.SavePreferencesAsync (new ReaderPreferences { Theme = ThemeKind.Sepia }, Ct);
            var deadline = DateTime.UtcNow.AddSeconds (20);
            while (await realHome.StatAsync (SyncPaths.Settings (Laptop), Ct) is null && DateTime.UtcNow < deadline)
                await Task.Delay (50, Ct);

            await stop.CancelAsync ();
            await loop;
            Assert.NotNull (await realHome.StatAsync (SyncPaths.Settings (Laptop), Ct));
        }

        [Theory]
        [InlineData (1, 2)]
        [InlineData (2, 4)]
        [InlineData (5, 32)]
        [InlineData (9, 300)]
        [InlineData (50, 300)]
        public void BackoffDoublesUpToFiveMinutes (int failures, double seconds)
        {
            Assert.Equal (seconds, SyncEngine.BackoffDelay (failures, 1).TotalSeconds, 3);
            Assert.Equal (seconds / 2, SyncEngine.BackoffDelay (failures, 0).TotalSeconds, 3);
        }

        [Theory]
        [InlineData ("Recital/format.json", SyncFileKind.WriteOnce)]
        [InlineData ("Recital/books/0123abcd.epub", SyncFileKind.WriteOnce)]
        [InlineData ("Recital/books/0123abcd (1).epub", SyncFileKind.Ignored)]
        [InlineData ("Recital/meta/0123abcd.json", SyncFileKind.WriteOnce)]
        [InlineData ("Recital/covers/0123abcd.jpg", SyncFileKind.WriteOnce)]
        [InlineData ("Recital/state/0123abcd/" + Laptop + ".json", SyncFileKind.Own)]
        [InlineData ("Recital/state/0123abcd/" + Phone + ".json", SyncFileKind.OtherDevice)]
        [InlineData ("Recital/state/0123abcd/" + Phone + " (conflicted copy).json", SyncFileKind.ConflictCopy)]
        [InlineData ("Recital/state/0123abcd/." + Phone + ".json.0123.recital-tmp", SyncFileKind.Ignored)]
        [InlineData ("Recital/state/not-hex/" + Phone + ".json", SyncFileKind.Ignored)]
        [InlineData ("Recital/settings/" + Laptop + ".json", SyncFileKind.Own)]
        [InlineData ("Recital/devices/" + Phone + " (1).json", SyncFileKind.ConflictCopy)]
        [InlineData ("Recital/notes.txt", SyncFileKind.Ignored)]
        [InlineData ("Other/settings/" + Laptop + ".json", SyncFileKind.Ignored)]
        [InlineData ("sync/abc/outbox.json", SyncFileKind.Ignored)]
        public void ClassifiesSyncHomePaths (string path, SyncFileKind expected)
            => Assert.Equal (expected, SyncPaths.Classify (path, Laptop));

        [Fact]
        public async Task AppServicesSyncsWithTheChosenFolder ()
        {
            var temp = Path.Combine (Path.GetTempPath (), "recital-tests", Guid.NewGuid ().ToString ("N"));
            try {
                var folder = Path.Combine (temp, "Dropbox");
                Directory.CreateDirectory (folder);
                await AppServices.SetSyncFolderAsync (Path.Combine (temp, "laptop"), folder, Ct);
                await AppServices.SetSyncFolderAsync (Path.Combine (temp, "desktop"), folder, Ct);
                await Assert.ThrowsAsync<ArgumentException> (() => AppServices.SetSyncFolderAsync (Path.Combine (temp, "laptop"), Path.Combine (temp, "laptop", "library"), Ct));

                await using (var services = await AppServices.OpenAsync (Path.Combine (temp, "laptop"), Ct)) {
                    Assert.Equal (folder, services.SyncFolder);
                    await services.Library.ImportAsync (new MemoryStream (Book ("Shared")), "shared.epub", Ct);
                    await services.Sync!.SyncOnceAsync (Ct);
                }
                Assert.True (File.Exists (Path.Combine (folder, "Recital", "format.json")));

                await using (var services = await AppServices.OpenAsync (Path.Combine (temp, "desktop"), Ct)) {
                    await services.Sync!.SyncOnceAsync (Ct);
                    Assert.Equal ("Shared", Assert.Single (await services.Library.ListAsync (Ct)).Title);
                }

                // A sync folder on a drive that is not connected: offline, not a new empty library.
                Directory.Delete (folder, recursive: true);
                await using (var services = await AppServices.OpenAsync (Path.Combine (temp, "desktop"), Ct)) {
                    await Assert.ThrowsAsync<DirectoryNotFoundException> (() => services.Sync!.SyncOnceAsync (Ct));
                    Assert.False (Directory.Exists (folder));
                    Assert.Single (await services.Library.ListAsync (Ct));
                }

                await AppServices.SetSyncFolderAsync (Path.Combine (temp, "desktop"), null, Ct);
                await using (var services = await AppServices.OpenAsync (Path.Combine (temp, "desktop"), Ct))
                    Assert.Null (services.Sync);
            } finally {
                if (Directory.Exists (temp))
                    Directory.Delete (temp, recursive: true);
            }
        }

        private static async Task<byte[]> ReadAsync (InMemoryStorageProvider provider, string path)
        {
            await using var stream = await provider.OpenReadAsync (path, Ct);
            var buffer = new MemoryStream ();
            await stream.CopyToAsync (buffer, Ct);
            return buffer.ToArray ();
        }

        /// <summary>A provider that can be switched off, and counts listings.</summary>
        private sealed class SwitchableStorage (IStorageProvider inner) : IStorageProvider
        {
            public bool Offline { get; set; }
            public int Lists { get; set; }

            public string Id => inner.Id;
            public StorageCapabilities Capabilities => inner.Capabilities;

            public Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
            {
                Check ();
                Lists++;
                return inner.ListAsync (folderPath, ct);
            }

            public Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
            {
                Check ();
                return inner.OpenReadAsync (path, ct);
            }

            public Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
            {
                Check ();
                return inner.WriteAsync (path, content, ifMatchEtag, ct);
            }

            public Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
            {
                Check ();
                return inner.StatAsync (path, ct);
            }

            public Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
            {
                Check ();
                return inner.GetChangesAsync (cursor, ct);
            }

            private void Check ()
            {
                if (Offline)
                    throw new IOException ("Offline.");
            }
        }
    }
}
