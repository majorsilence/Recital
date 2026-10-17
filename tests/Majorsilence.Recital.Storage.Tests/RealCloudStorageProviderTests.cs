using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage.OAuth;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// The conformance suite against the real Dropbox, OneDrive and Google Drive, when this build's
    /// registration (RECITAL_DROPBOX_APP_KEY, RECITAL_ONEDRIVE_CLIENT_ID, RECITAL_GOOGLE_CLIENT_ID and
    /// _SECRET) and a test account's refresh token (RECITAL_TEST_DROPBOX_REFRESH_TOKEN, ..._ONEDRIVE_...,
    /// ..._GDRIVE_...) are set. docs/CLOUD_SETUP.md says how to get them. Each test works in its own new
    /// folder under <c>recital-tests/</c> in the account.
    /// </summary>
    public abstract class RealCloudStorageProviderTests : StorageProviderTests
    {
        private readonly IStorageProvider? provider;
        private readonly string? skip;

        protected RealCloudStorageProviderTests (SyncHomeKind kind, string tokenVariable)
        {
            var refresh = Environment.GetEnvironmentVariable (tokenVariable);
            var config = CloudClients.None.WithEnvironment ().OAuthFor (kind);
            if (string.IsNullOrEmpty (refresh) || config is null) {
                skip = $"Set {tokenVariable} and this service's client id to run against the real {SyncHome.Label (kind)}.";
                return;
            }
            var secrets = new InMemorySecretStore ();
            secrets.SetAsync ("oauth:test", refresh).GetAwaiter ().GetResult ();
            var handler = new OAuthHandler (new OAuthClient (config, new HttpClient ()), secrets, "oauth:test", new SocketsHttpHandler ());
            var http = new HttpClient (handler);
            var root = "recital-tests/" + Guid.NewGuid ().ToString ("N");
            provider = kind switch {
                SyncHomeKind.Dropbox => new Dropbox.DropboxStorageProvider (http, root),
                SyncHomeKind.OneDrive => new PrefixedProvider (new OneDrive.OneDriveStorageProvider (http, feedFolder: root), root),
                _ => new PrefixedProvider (new GoogleDrive.GoogleDriveStorageProvider (http, root), root),
            };
        }

        protected override IStorageProvider Provider {
            get {
                Assert.SkipWhen (skip is not null, skip ?? "");
                return provider!;
            }
        }
    }

    public sealed class RealDropboxStorageProviderTests () : RealCloudStorageProviderTests (SyncHomeKind.Dropbox, "RECITAL_TEST_DROPBOX_REFRESH_TOKEN");

    public sealed class RealOneDriveStorageProviderTests () : RealCloudStorageProviderTests (SyncHomeKind.OneDrive, "RECITAL_TEST_ONEDRIVE_REFRESH_TOKEN");

    public sealed class RealGoogleDriveStorageProviderTests () : RealCloudStorageProviderTests (SyncHomeKind.GoogleDrive, "RECITAL_TEST_GDRIVE_REFRESH_TOKEN");

    /// <summary>A provider seen from a subfolder: for real-service tests that must not touch the account's own files.</summary>
    internal sealed class PrefixedProvider (IStorageProvider inner, string prefix) : IStorageProvider, IStorageAccountProvider
    {
        public string Id => inner.Id;
        public StorageCapabilities Capabilities => inner.Capabilities;

        private string In (string path) => StoragePath.Combine (prefix, StoragePath.Normalize (path));

        private StorageEntry Out (StorageEntry entry) => entry with { Path = entry.Path.Length <= prefix.Length ? "" : entry.Path[(prefix.Length + 1)..] };

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
            => (await inner.ListAsync (In (folderPath), ct)).Select (Out).ToList ();

        public Task<Stream> OpenReadAsync (string path, CancellationToken ct = default) => inner.OpenReadAsync (In (path), ct);

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
            => StoragePath.Normalize (path).Length == 0 ? throw new ArgumentException ("Cannot write to the root.", nameof (path)) : Out (await inner.WriteAsync (In (path), content, ifMatchEtag, ct));

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
            => await inner.StatAsync (In (path), ct) is { } entry ? Out (entry) : null;

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            var changes = await inner.GetChangesAsync (cursor, ct);
            return changes with { Changed = changes.Changed.Where (e => StoragePath.IsWithin (e.Path, prefix)).Select (Out).ToList () };
        }

        public Task<StorageAccount> GetAccountAsync (CancellationToken ct = default) => ((IStorageAccountProvider)inner).GetAccountAsync (ct);
    }
}
