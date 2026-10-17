using System.Text;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// The provider conformance suite (plan Phase 7): the behaviour the sync engine relies on, the same
    /// from every provider. Each test starts from an empty provider.
    /// </summary>
    public abstract class StorageProviderTests : IDisposable
    {
        protected abstract IStorageProvider Provider { get; }

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static MemoryStream Text (string text) => new (Encoding.UTF8.GetBytes (text));

        private async Task<string> ReadAsync (string path)
        {
            await using var stream = await Provider.OpenReadAsync (path, Ct);
            using var reader = new StreamReader (stream);
            return await reader.ReadToEndAsync (Ct);
        }

        [Fact]
        public async Task WritesReadsAndStats ()
        {
            var written = await Provider.WriteAsync ("Recital/books/a.json", Text ("hello"), null, Ct);

            Assert.Equal ("Recital/books/a.json", written.Path);
            Assert.Equal (5, written.Size);
            Assert.Equal ("hello", await ReadAsync ("Recital/books/a.json"));

            var stat = await Provider.StatAsync ("Recital/books/a.json", Ct);
            Assert.NotNull (stat);
            Assert.False (stat.IsFolder);
            Assert.Equal (written.ETag, stat.ETag);

            Assert.True ((await Provider.StatAsync ("Recital/books", Ct))?.IsFolder);
            Assert.Null (await Provider.StatAsync ("Recital/missing.json", Ct));
        }

        [Fact]
        public async Task ListsImmediateChildren ()
        {
            await Provider.WriteAsync ("Recital/state/b1/d1.json", Text ("1"), null, Ct);
            await Provider.WriteAsync ("Recital/state/b1/d2.json", Text ("2"), null, Ct);
            await Provider.WriteAsync ("Recital/state/b2/d1.json", Text ("3"), null, Ct);

            var state = await Provider.ListAsync ("Recital/state", Ct);
            Assert.Equal (["Recital/state/b1", "Recital/state/b2"], state.Select (e => e.Path));
            Assert.All (state, e => Assert.True (e.IsFolder));

            var b1 = await Provider.ListAsync ("Recital/state/b1/", Ct);
            Assert.Equal (["d1.json", "d2.json"], b1.Select (e => e.Name));

            Assert.Empty (await Provider.ListAsync ("Recital/nothing", Ct));
        }

        [Fact]
        public async Task OverwritesAndChecksETags ()
        {
            var first = await Provider.WriteAsync ("f.json", Text ("one"), null, Ct);
            var second = await Provider.WriteAsync ("f.json", Text ("two, longer"), first.ETag, Ct);

            Assert.NotEqual (first.ETag, second.ETag);
            Assert.Equal ("two, longer", await ReadAsync ("f.json"));

            await Assert.ThrowsAsync<StoragePreconditionFailedException> (() => Provider.WriteAsync ("f.json", Text ("three"), first.ETag, Ct));
            Assert.Equal ("two, longer", await ReadAsync ("f.json"));
        }

        [Fact]
        public async Task MissingFilesThrowFileNotFound ()
            => await Assert.ThrowsAsync<FileNotFoundException> (() => Provider.OpenReadAsync ("nope.json", Ct));

        [Theory]
        [InlineData ("../outside.json")]
        [InlineData ("Recital/../../x.json")]
        [InlineData ("a/./b.json")]
        public async Task PathsCannotLeaveTheRoot (string path)
            => await Assert.ThrowsAsync<ArgumentException> (() => Provider.WriteAsync (path, Text ("x"), null, Ct));

        [Fact]
        public async Task ChangesReportWritesAfterTheCursor ()
        {
            await Provider.WriteAsync ("a.json", Text ("a"), null, Ct);
            var all = await Provider.GetChangesAsync (null, Ct);
            Assert.True (all.IsFullListing);
            Assert.Contains (all.Changed, e => e.Path == "a.json");

            await Provider.WriteAsync ("dir/b.json", Text ("b"), null, Ct);
            var changes = await Provider.GetChangesAsync (all.Cursor, Ct);
            // A real change feed reports only the changes; without one a provider may list everything.
            if (Provider.Capabilities.HasFlag (StorageCapabilities.ChangeFeed))
                Assert.False (changes.IsFullListing);
            Assert.Contains (changes.Changed, e => e.Path == "dir/b.json");
        }

        [Theory]
        [InlineData ("Recital/state/b1/d1 (1).json")]
        [InlineData ("Recital/state/b1/d1 (Laptop's conflicted copy 2026-10-17).json")]
        [InlineData ("Recital/meta/Ünïcødé & spaces #1 +50%.json")]
        public async Task NamesSyncClientsUseRoundTrip (string path)
        {
            await Provider.WriteAsync (path, Text ("copy"), null, Ct);
            Assert.Equal ("copy", await ReadAsync (path));
            Assert.Contains (await Provider.ListAsync (StoragePath.Parent (path), Ct), e => e.Path == path && !e.IsFolder);
            Assert.Equal (4, (await Provider.StatAsync (path, Ct))?.Size);
        }

        [Fact]
        public async Task ARewriteOfTheSameSizeChangesTheETag ()
        {
            var first = await Provider.WriteAsync ("Recital/settings/d1.json", Text ("{\"p\":1}"), null, Ct);
            var second = await Provider.WriteAsync ("Recital/settings/d1.json", Text ("{\"p\":2}"), null, Ct);
            Assert.NotEqual (first.ETag, second.ETag);

            var listed = Assert.Single (await Provider.ListAsync ("Recital/settings", Ct));
            Assert.Equal (second.ETag, listed.ETag);
            Assert.Equal (second.ETag, (await Provider.StatAsync ("Recital/settings/d1.json", Ct))?.ETag);
            Assert.Equal ("{\"p\":2}", await ReadAsync ("Recital/settings/d1.json"));
        }

        [Fact]
        public async Task DeepFoldersAreCreatedAndListed ()
        {
            await Provider.WriteAsync ("Recital/state/0123abcd/aaaa.json", Text ("{}"), null, Ct);
            Assert.True ((await Provider.StatAsync ("Recital/state/0123abcd", Ct))?.IsFolder);
            Assert.Equal (["Recital/state"], (await Provider.ListAsync ("Recital", Ct)).Select (e => e.Path));
        }

        [Fact]
        public async Task ABookSizedFileRoundTrips ()
        {
            var bytes = new byte[3 * 1024 * 1024 + 17];
            new Random (7).NextBytes (bytes);
            var entry = await Provider.WriteAsync ("Recital/books/big.epub", new MemoryStream (bytes), null, Ct);
            Assert.Equal (bytes.Length, entry.Size);
            await using var stream = await Provider.OpenReadAsync ("Recital/books/big.epub", Ct);
            var copy = new MemoryStream ();
            await stream.CopyToAsync (copy, Ct);
            Assert.True (bytes.AsSpan ().SequenceEqual (copy.ToArray ()));
        }

        [Fact]
        public async Task ACloudProviderNamesItsAccount ()
        {
            Assert.SkipUnless (Provider is IStorageAccountProvider, "Not an account-based provider.");
            var account = await ((IStorageAccountProvider)Provider).GetAccountAsync (Ct);
            Assert.False (string.IsNullOrEmpty (account.Id));
            Assert.False (string.IsNullOrEmpty (account.Name));
        }

        /// <summary>Two devices syncing through the provider see each other's progress, preferences and highlights.</summary>
        [Fact]
        public async Task TwoDevicesSyncThroughIt ()
        {
            const string book = "0123456789abcdef";
            var provider = Provider;
            using var laptopEngine = new SyncEngine (new InMemoryStorageProvider (), provider, "conformance", "aaaa");
            using var phoneEngine = new SyncEngine (new InMemoryStorageProvider (), provider, "conformance", "bbbb");
            using var laptop = new LibraryService (laptopEngine.Library, new DeviceIdentity ("aaaa", "Laptop", "linux"));
            using var phone = new LibraryService (phoneEngine.Library, new DeviceIdentity ("bbbb", "Phone", "linux"));
            await laptop.InitializeAsync (Ct);
            await phone.InitializeAsync (Ct);

            await laptop.SaveProgressAsync (book, new ReadingLocator ("epubcfi(/6/4!/4/2/1:0)", 1, 0, 0.25), Ct);
            await laptop.SavePreferencesAsync (new ReaderPreferences { Theme = ThemeKind.Sepia }, Ct);
            await laptopEngine.SyncOnceAsync (Ct);
            await phoneEngine.SyncOnceAsync (Ct);
            Assert.Equal (0.25, (await phone.GetProgressAsync (book, Ct))?.Locator.Percent);
            Assert.Equal (ThemeKind.Sepia, (await phone.LoadPreferencesAsync (Ct)).Theme);

            await phone.SaveAnnotationAsync (book, new Core.Annotations.Annotation { Id = Guid.NewGuid (), Cfi = "epubcfi(/6/4!/4/2,/1:0,/1:4)", Text = "Call", Color = "pink" }, Ct);
            await phoneEngine.SyncOnceAsync (Ct);
            await laptopEngine.SyncOnceAsync (Ct);
            Assert.Equal ("pink", Assert.Single (await laptop.GetAnnotationsAsync (book, Ct)).Color);

            var quiet = await laptopEngine.SyncOnceAsync (Ct);
            Assert.Empty (quiet.Pushed);
            Assert.Empty (quiet.Pulled);
        }

        public virtual void Dispose () => GC.SuppressFinalize (this);
    }

    public sealed class InMemoryStorageProviderTests : StorageProviderTests
    {
        protected override IStorageProvider Provider { get; } = new InMemoryStorageProvider ();

        [Fact]
        public async Task TheChangeFeedIsExact ()
        {
            await Provider.WriteAsync ("a.json", new MemoryStream ([1]), null, TestContext.Current.CancellationToken);
            var cursor = (await Provider.GetChangesAsync (null, TestContext.Current.CancellationToken)).Cursor;
            Assert.Empty ((await Provider.GetChangesAsync (cursor, TestContext.Current.CancellationToken)).Changed);
        }
    }

    public sealed class LocalFolderStorageProviderTests : StorageProviderTests
    {
        private readonly string root = Path.Combine (Path.GetTempPath (), "recital-tests", Guid.NewGuid ().ToString ("N"));

        public LocalFolderStorageProviderTests ()
        {
            Provider = new LocalFolderStorageProvider (root);
        }

        protected override IStorageProvider Provider { get; }

        [Fact]
        public async Task LeavesNoTemporaryFilesBehind ()
        {
            await Provider.WriteAsync ("x/y.json", new MemoryStream ([1, 2, 3]), null, TestContext.Current.CancellationToken);
            Assert.Equal (["y.json"], Directory.GetFiles (Path.Combine (root, "x")).Select (Path.GetFileName));
        }

        [Fact]
        public async Task SameSizedWritesInOneClockTickGetDifferentETags ()
        {
            var ct = TestContext.Current.CancellationToken;
            var first = await Provider.WriteAsync ("s.json", new MemoryStream ("{\"p\":1}"u8.ToArray ()), null, ct);
            var tick = File.GetLastWriteTimeUtc (Path.Combine (root, "s.json"));

            // Another process (a sync client) replaces it with the same size, and the file system's coarse
            // clock gives it the same time.
            await File.WriteAllBytesAsync (Path.Combine (root, "s.json"), "{\"p\":2}"u8.ToArray (), ct);
            File.SetLastWriteTimeUtc (Path.Combine (root, "s.json"), tick);

            var second = await Provider.StatAsync ("s.json", ct);
            Assert.NotEqual (first.ETag, second?.ETag);
            Assert.Equal (second?.ETag, (await Provider.StatAsync ("s.json", ct))?.ETag);
        }

        [Fact]
        public async Task FilesWithNamesNoLogicalPathCanHoldAreNotListed ()
        {
            if (OperatingSystem.IsWindows ())
                return; // Windows forbids the name anyway.
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory (root);
            await File.WriteAllTextAsync (Path.Combine (root, "a:b.json"), "x", ct);
            await Provider.WriteAsync ("ok.json", new MemoryStream ([1]), null, ct);

            Assert.Equal (["ok.json"], (await Provider.ListAsync ("", ct)).Select (e => e.Name));
            Assert.Equal (["ok.json"], (await Provider.GetChangesAsync (null, ct)).Changed.Select (e => e.Name));
        }

        [Fact]
        public async Task AMissingRequiredRootIsUnreachableNotCreated ()
        {
            var ct = TestContext.Current.CancellationToken;
            var missing = new LocalFolderStorageProvider (Path.Combine (root, "unplugged"), requireRoot: true);
            await Assert.ThrowsAsync<DirectoryNotFoundException> (() => missing.WriteAsync ("Recital/format.json", new MemoryStream ([1]), null, ct));
            await Assert.ThrowsAsync<DirectoryNotFoundException> (() => missing.ListAsync ("Recital", ct));
            Assert.False (Directory.Exists (Path.Combine (root, "unplugged")));
        }

        public override void Dispose ()
        {
            if (Directory.Exists (root))
                Directory.Delete (root, recursive: true);
            base.Dispose ();
        }
    }

    /// <summary>The WebDAV provider against the in-process fake server.</summary>
    public sealed class WebDavStorageProviderTests : StorageProviderTests
    {
        private readonly FakeWebDavServer server = new ();
        private readonly HttpClient http;

        public WebDavStorageProviderTests ()
        {
            http = WebDav.WebDavStorageProvider.CreateClient ("user", "pass", server);
            Provider = new WebDav.WebDavStorageProvider (server.BaseUri, http);
        }

        protected override IStorageProvider Provider { get; }

        [Fact]
        public async Task RefusedCredentialsAndServerErrorsAreTold ()
        {
            var ct = TestContext.Current.CancellationToken;
            server.FailWith = System.Net.HttpStatusCode.Unauthorized;
            await Assert.ThrowsAsync<StorageAuthenticationException> (() => Provider.ListAsync ("Recital", ct));
            server.FailWith = System.Net.HttpStatusCode.ServiceUnavailable;
            await Assert.ThrowsAsync<StorageUnavailableException> (() => Provider.StatAsync ("Recital", ct));
        }

        public override void Dispose ()
        {
            http.Dispose ();
            server.Dispose ();
            base.Dispose ();
        }
    }

    /// <summary>
    /// The WebDAV provider against a real server, when RECITAL_TEST_WEBDAV_URL (and _USER, _PASSWORD) are
    /// set. Each test works in its own new folder under that URL. CI runs Apache mod_dav in a container.
    /// </summary>
    public sealed class RealWebDavStorageProviderTests : StorageProviderTests
    {
        private static readonly string? Url = Environment.GetEnvironmentVariable ("RECITAL_TEST_WEBDAV_URL");
        private readonly HttpClient? http;
        private readonly IStorageProvider? provider;

        public RealWebDavStorageProviderTests ()
        {
            if (string.IsNullOrEmpty (Url))
                return;
            http = WebDav.WebDavStorageProvider.CreateClient (Environment.GetEnvironmentVariable ("RECITAL_TEST_WEBDAV_USER"), Environment.GetEnvironmentVariable ("RECITAL_TEST_WEBDAV_PASSWORD"));
            var root = new Uri (new Uri (Url.EndsWith ('/') ? Url : Url + "/"), "recital-tests/" + Guid.NewGuid ().ToString ("N") + "/");
            provider = new WebDav.WebDavStorageProvider (root, http);
        }

        protected override IStorageProvider Provider {
            get {
                Assert.SkipWhen (provider is null, "Set RECITAL_TEST_WEBDAV_URL to run against a real WebDAV server.");
                return provider!;
            }
        }

        public override void Dispose ()
        {
            http?.Dispose ();
            base.Dispose ();
        }
    }

    /// <summary>The Dropbox provider against the in-process fake API.</summary>
    public sealed class DropboxStorageProviderTests : StorageProviderTests
    {
        private readonly FakeDropboxServer server = new ();
        private readonly HttpClient http;

        public DropboxStorageProviderTests ()
        {
            http = new HttpClient (server);
            Provider = new Dropbox.DropboxStorageProvider (http);
        }

        protected override IStorageProvider Provider { get; }

        [Fact]
        public async Task TheFeedCoversOnlyItsFolder ()
        {
            var ct = TestContext.Current.CancellationToken;
            var provider = new Dropbox.DropboxStorageProvider (http, feedFolder: "Recital");
            var empty = await provider.GetChangesAsync (null, ct);
            Assert.Empty (empty.Changed);

            await provider.WriteAsync ("Photos/cat.jpg", new MemoryStream ([1]), null, ct);
            await provider.WriteAsync ("Recital/format.json", new MemoryStream ([2]), null, ct);
            var first = await provider.GetChangesAsync (empty.Cursor, ct);
            Assert.Equal (["Recital/format.json"], first.Changed.Select (e => e.Path));

            await provider.WriteAsync ("Recital/settings/a.json", new MemoryStream ([3]), null, ct);
            await provider.WriteAsync ("Photos/dog.jpg", new MemoryStream ([4]), null, ct);
            var second = await provider.GetChangesAsync (first.Cursor, ct);
            Assert.Equal (["Recital/settings/a.json"], second.Changed.Select (e => e.Path));
        }

        public override void Dispose ()
        {
            http.Dispose ();
            server.Dispose ();
            base.Dispose ();
        }
    }

    /// <summary>The OneDrive provider against the in-process fake Graph.</summary>
    public sealed class OneDriveStorageProviderTests : StorageProviderTests
    {
        private readonly FakeGraphServer server = new ();
        private readonly HttpClient http;
        private readonly HttpClient upload;

        public OneDriveStorageProviderTests ()
        {
            http = new HttpClient (new BearerHandler (server));
            upload = new HttpClient (server, disposeHandler: false);
            Provider = new OneDrive.OneDriveStorageProvider (http, upload);
        }

        protected override IStorageProvider Provider { get; }

        [Fact]
        public async Task TheFeedRebuildsPathsFromParentIdsAndCoversOnlyItsFolder ()
        {
            var ct = TestContext.Current.CancellationToken;
            var writer = new OneDrive.OneDriveStorageProvider (http, upload);
            var reader = new OneDrive.OneDriveStorageProvider (http, upload, feedFolder: "Recital");
            await writer.WriteAsync ("Recital/format.json", new MemoryStream ([1]), null, ct);
            var start = await reader.GetChangesAsync (null, ct);
            Assert.Equal (["Recital/format.json"], start.Changed.Select (e => e.Path));

            // A reader that has never seen these folders: the paths come from parent ids.
            await writer.WriteAsync ("Recital/state/0123abcd/aaaa.json", new MemoryStream ([2]), null, ct);
            await writer.WriteAsync ("Documents/other.txt", new MemoryStream ([3]), null, ct);
            await writer.WriteAsync ("Recital/settings/aaaa.json", new MemoryStream ([4]), null, ct);
            await writer.WriteAsync ("Recital/settings/bbbb.json", new MemoryStream ([5]), null, ct);
            var changes = await reader.GetChangesAsync (start.Cursor, ct);
            Assert.False (changes.IsFullListing);
            Assert.Equal (["Recital/settings/aaaa.json", "Recital/settings/bbbb.json", "Recital/state/0123abcd/aaaa.json"], changes.Changed.Select (e => e.Path).Order (StringComparer.Ordinal));
            Assert.Empty ((await reader.GetChangesAsync (changes.Cursor, ct)).Changed);
        }

        public override void Dispose ()
        {
            http.Dispose ();
            upload.Dispose ();
            server.Dispose ();
            base.Dispose ();
        }

        /// <summary>Adds a bearer token, as the OAuth handler does, so the fake can check upload URLs do not get one.</summary>
        private sealed class BearerHandler (HttpMessageHandler inner) : DelegatingHandler (inner)
        {
            protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue ("Bearer", "token");
                return base.SendAsync (request, cancellationToken);
            }
        }
    }

    /// <summary>The Google Drive provider against the in-process fake API.</summary>
    public sealed class GoogleDriveStorageProviderTests : StorageProviderTests
    {
        private readonly FakeDriveServer server = new ();
        private readonly HttpClient http;

        public GoogleDriveStorageProviderTests ()
        {
            http = new HttpClient (server);
            Provider = new GoogleDrive.GoogleDriveStorageProvider (http);
        }

        protected override IStorageProvider Provider { get; }

        /// <summary>Two devices created "Recital" at once: both folders' files show, and writes go to the oldest.</summary>
        [Fact]
        public async Task SameNamedFoldersAndFilesFromARaceAreMerged ()
        {
            var ct = TestContext.Current.CancellationToken;
            await Provider.WriteAsync ("Recital/format.json", new MemoryStream ("{}"u8.ToArray ()), null, ct);
            var first = server.IdOf ("root-id", "Recital");
            var second = server.AddDuplicateFolder ("root-id", "Recital");
            server.AddFile (second, "format.json", "{\"later\":1}"u8.ToArray ());
            server.AddFile (second, "devices.json", [1]);

            // A fresh provider (another device) sees one Recital folder holding both folders' files.
            var other = new GoogleDrive.GoogleDriveStorageProvider (http);
            Assert.Equal (["Recital"], (await other.ListAsync ("", ct)).Select (e => e.Path));
            Assert.Equal (["Recital/devices.json", "Recital/format.json"], (await other.ListAsync ("Recital", ct)).Select (e => e.Path));
            using (var reader = new StreamReader (await other.OpenReadAsync ("Recital/format.json", ct)))
                Assert.Equal ("{}", await reader.ReadToEndAsync (ct));

            await other.WriteAsync ("Recital/settings.json", new MemoryStream ([2]), null, ct);
            Assert.Equal (first, server.IdOf ("root-id", "Recital"));
            Assert.NotNull (server.IdOf (first, "settings.json"));
        }

        [Fact]
        public async Task TheFeedFindsFilesInAFolderCreatedByAnotherDevice ()
        {
            var ct = TestContext.Current.CancellationToken;
            var reader = new GoogleDrive.GoogleDriveStorageProvider (http, feedFolder: "Recital");
            await Provider.WriteAsync ("Recital/format.json", new MemoryStream ([1]), null, ct);
            var start = await reader.GetChangesAsync (null, ct);
            Assert.Equal (["Recital/format.json"], start.Changed.Select (e => e.Path));

            var duplicate = server.AddDuplicateFolder ("root-id", "Recital");
            server.AddFile (duplicate, "late.json", [3]);
            await Provider.WriteAsync ("Elsewhere/x.json", new MemoryStream ([4]), null, ct);
            var changes = await reader.GetChangesAsync (start.Cursor, ct);
            Assert.Equal (["Recital/late.json"], changes.Changed.Select (e => e.Path));
        }

        public override void Dispose ()
        {
            http.Dispose ();
            server.Dispose ();
            base.Dispose ();
        }
    }
}
