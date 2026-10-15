using System.Text;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    /// <summary>The same behaviour from every provider: the start of the Phase 7 conformance suite.</summary>
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
            Assert.False (changes.IsFullListing);
            Assert.Contains (changes.Changed, e => e.Path == "dir/b.json");
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

        public override void Dispose ()
        {
            if (Directory.Exists (root))
                Directory.Delete (root, recursive: true);
            base.Dispose ();
        }
    }
}
