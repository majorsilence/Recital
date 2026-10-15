using System.Text;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class LibraryServiceTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static LibraryService NewLibrary (IStorageProvider storage, string deviceId = "d1")
            => new (storage, new DeviceIdentity (deviceId, "Test", "linux"));

        [Fact]
        public async Task ImportWritesTheBookMetaAndCoverUnderItsHash ()
        {
            var storage = new InMemoryStorageProvider ();
            using var library = NewLibrary (storage);
            await library.InitializeAsync (Ct);

            await using var file = Corpus.Open (Corpus.SePride);
            var result = await library.ImportAsync (file, Corpus.SePride, Ct);

            Assert.False (result.AlreadyInLibrary);
            var id = result.Book.Id;
            Assert.Equal (64, id.Length);
            Assert.Equal ("Pride and Prejudice", result.Book.Title);
            Assert.Equal ("Jane Austen", result.Book.Authors);
            Assert.True (result.Book.Meta.HasCover);

            Assert.NotNull (await storage.StatAsync (SyncPaths.Book (id), Ct));
            Assert.NotNull (await storage.StatAsync (SyncPaths.Meta (id), Ct));
            var cover = await library.ReadCoverAsync (id, Ct);
            Assert.NotNull (cover);
            Assert.Equal ([0xFF, 0xD8], cover[..2]); // JPEG
            Assert.NotNull (await storage.StatAsync (SyncPaths.Format, Ct));
            Assert.NotNull (await storage.StatAsync (SyncPaths.Device ("d1"), Ct));
        }

        [Fact]
        public async Task ImportingTheSameFileAgainFindsTheExistingBook ()
        {
            var storage = new InMemoryStorageProvider ();
            using var library = NewLibrary (storage);

            await using (var file = Corpus.Open (Corpus.Alice))
                await library.ImportAsync (file, null, Ct);
            await using (var again = Corpus.Open (Corpus.Alice)) {
                var result = await library.ImportAsync (again, null, Ct);
                Assert.True (result.AlreadyInLibrary);
            }

            Assert.Single (await library.ListAsync (Ct));
        }

        [Fact]
        public async Task NonEpubsAndDrmBooksAreRefused ()
        {
            using var library = NewLibrary (new InMemoryStorageProvider ());

            await Assert.ThrowsAsync<BookImportException> (() => library.ImportAsync (new MemoryStream (Encoding.UTF8.GetBytes ("not a zip")), "x.epub", Ct));

            var drm = TestEpub.Epub3 ().With ("META-INF/encryption.xml", """
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
                  <enc:CipherData><enc:CipherReference URI="OEBPS/text/ch1.xhtml"/></enc:CipherData></enc:EncryptedData>
                </encryption>
                """);
            var ex = await Assert.ThrowsAsync<BookImportException> (() => library.ImportAsync (drm.ToStream (), "drm.epub", Ct));
            Assert.Contains ("DRM", ex.Message, StringComparison.Ordinal);
            Assert.Empty (await library.ListAsync (Ct));
        }

        [Fact]
        public async Task ProgressIsSavedPerDeviceAndTheNewestWins ()
        {
            var storage = new InMemoryStorageProvider ();
            using var phone = NewLibrary (storage, "aa");
            using var laptop = NewLibrary (storage, "bb");

            string id;
            await using (var file = Corpus.Open (Corpus.Alice))
                id = (await phone.ImportAsync (file, null, Ct)).Book.Id;

            await phone.SaveProgressAsync (id, new ReadingLocator ("epubcfi(/6/4!/4/2/1:0)", 1, 0, 0.1), Ct);
            // The laptop has seen the phone's progress, so its later save orders after it whatever its clock says.
            Assert.Equal (0.1, (await laptop.GetProgressAsync (id, Ct))?.Locator.Percent);
            await laptop.SaveProgressAsync (id, new ReadingLocator ("epubcfi(/6/8!/4/2/1:0)", 3, 0, 0.3), Ct);

            Assert.Equal (0.3, (await phone.GetProgressAsync (id, Ct))?.Locator.Percent);
            Assert.Equal (2, (await storage.ListAsync (SyncPaths.StateFolder (id), Ct)).Count);

            var listed = Assert.Single (await phone.ListAsync (Ct));
            Assert.Equal (0.3, listed.Percent);
        }

        [Fact]
        public async Task AConflictedCopyIsReadAsAnotherInput ()
        {
            var storage = new InMemoryStorageProvider ();
            using var library = NewLibrary (storage, "aa");
            string id;
            await using (var file = Corpus.Open (Corpus.Alice))
                id = (await library.ImportAsync (file, null, Ct)).Book.Id;
            await library.SaveProgressAsync (id, new ReadingLocator (null, 1, 0, 0.2), Ct);

            // A sync client kept a newer copy aside rather than overwrite.
            var newer = $$"""
                { "formatVersion": 1, "bookId": "{{id}}", "deviceId": "bb",
                  "progress": { "locator": { "spineIndex": 5, "charOffset": 10, "percent": 0.6 }, "hlc": "{{new Hlc (DateTimeOffset.UtcNow.AddMinutes (1).ToUnixTimeMilliseconds (), 0, "bb")}}", "updatedAt": "2026-10-14T00:00:00Z" },
                  "annotations": [] }
                """;
            await storage.WriteAsync (SyncPaths.StateFolder (id) + "/bb (conflicted copy).json", new MemoryStream (Encoding.UTF8.GetBytes (newer)), null, Ct);

            Assert.Equal (0.6, (await library.GetProgressAsync (id, Ct))?.Locator.Percent);
        }

        [Fact]
        public async Task PreferencesMergePerKeyAcrossDevices ()
        {
            var storage = new InMemoryStorageProvider ();
            using var phone = NewLibrary (storage, "aa");
            using var laptop = NewLibrary (storage, "bb");

            await phone.SavePreferencesAsync (new ReaderPreferences { Theme = ThemeKind.Dark }, Ct);
            var seen = await laptop.LoadPreferencesAsync (Ct);
            Assert.Equal (ThemeKind.Dark, seen.Theme);

            // The laptop changes only the font size; the phone's theme is kept.
            await laptop.SavePreferencesAsync (seen with { FontSize = 24 }, Ct);
            var merged = await phone.LoadPreferencesAsync (Ct);
            Assert.Equal (ThemeKind.Dark, merged.Theme);
            Assert.Equal (24, merged.FontSize);
        }

        [Fact]
        public async Task NothingIsWrittenOutsideTheRecitalFolder ()
        {
            var storage = new InMemoryStorageProvider ();
            using var library = NewLibrary (storage);
            await library.InitializeAsync (Ct);
            await using (var file = Corpus.Open (Corpus.Alice)) {
                var id = (await library.ImportAsync (file, null, Ct)).Book.Id;
                await library.SaveProgressAsync (id, new ReadingLocator (null, 0, 0, 0), Ct);
            }
            await library.SavePreferencesAsync (new ReaderPreferences { FontSize = 20 }, Ct);

            var all = await storage.GetChangesAsync (null, Ct);
            Assert.All (all.Changed, e => Assert.StartsWith ("Recital/", e.Path, StringComparison.Ordinal));
        }

        [Fact]
        public async Task TheLocalFolderSetupKeepsTheDeviceIdOutsideTheLibrary ()
        {
            var root = Path.Combine (Path.GetTempPath (), "recital-tests", Guid.NewGuid ().ToString ("N"));
            try {
                string first;
                using (var services = await AppServices.OpenAsync (root, Ct))
                    first = services.Device.DeviceId;
                using (var again = await AppServices.OpenAsync (root, Ct))
                    Assert.Equal (first, again.Device.DeviceId);

                Assert.True (File.Exists (Path.Combine (root, "device.json")));
                Assert.True (File.Exists (Path.Combine (root, "library", "Recital", "format.json")));
                Assert.True (File.Exists (Path.Combine (root, "library", "Recital", "devices", first + ".json")));
            } finally {
                Directory.Delete (root, recursive: true);
            }
        }
    }
}
