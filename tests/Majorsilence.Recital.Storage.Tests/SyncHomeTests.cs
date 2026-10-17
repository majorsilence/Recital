using System.Net;
using System.Text;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Storage.OAuth;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>Choosing a cloud sync home: connecting, where the secrets go, and syncing through it.</summary>
    public sealed class SyncHomeTests : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private readonly string temp = Path.Combine (Path.GetTempPath (), "recital-synchome", Guid.NewGuid ().ToString ("N"));

        private static byte[] Book () => TestEpub.Epub3 ().ToStream ().ToArray ();

        [Fact]
        public async Task WebDavIsCheckedAndThePasswordGoesOnlyToTheSecureStore ()
        {
            var server = new FakeWebDavServer { RequiredCredentials = "reader:s3cret-pass" };
            var secrets = new InMemorySecretStore ();
            var platform = new PlatformServices (secrets, null, CloudClients.None, () => new NonDisposingHandler (server));

            await Assert.ThrowsAsync<StorageAuthenticationException> (() => SyncHome.ConnectWebDavAsync (server.BaseUri, "reader", "wrong", platform, Ct));
            var settings = await SyncHome.ConnectWebDavAsync (server.BaseUri, "reader", "s3cret-pass", platform, Ct);
            Assert.Equal (SyncHomeKind.WebDav, settings.Kind);
            Assert.Equal ("s3cret-pass", await secrets.GetAsync (SyncHome.WebDavSecretKey (settings.Url!, "reader"), Ct));

            var laptop = Path.Combine (temp, "laptop");
            await AppServices.SetSyncHomeAsync (laptop, settings, Ct);
            await using (var services = await AppServices.OpenAsync (laptop, platform, Ct)) {
                Assert.Equal ("WebDAV on webdav.test", services.SyncHome!.DisplayName);
                await services.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct);
                await services.Sync!.SyncOnceAsync (Ct);
            }

            AssertNoSecretIn (laptop, "s3cret-pass");
        }

        [Fact]
        public async Task SigningInToDropboxSyncsTwoDevicesAndKeepsTheTokenSecret ()
        {
            var dropbox = new FakeDropboxServer ();
            var auth = new TokenServer ();
            var router = new Router (dropbox, auth);
            var clients = new CloudClients (DropboxAppKey: "app-key-1");

            var laptopSecrets = new InMemorySecretStore ();
            var laptopPlatform = new PlatformServices (laptopSecrets, _ => new FakeBrowserBroker (), clients, () => new NonDisposingHandler (router));
            var settings = await SyncHome.ConnectCloudAsync (SyncHomeKind.Dropbox, laptopPlatform, Ct);
            Assert.Equal ("dbid:AAA", settings.AccountId);
            Assert.Equal ("Dropbox (reader@example.com)", settings.DisplayName);
            Assert.Equal ("refresh-secret-0", await laptopSecrets.GetAsync (SyncHome.OAuthSecretKey (SyncHomeKind.Dropbox), Ct));

            var laptop = Path.Combine (temp, "laptop");
            await AppServices.SetSyncHomeAsync (laptop, settings, Ct);
            await using (var services = await AppServices.OpenAsync (laptop, laptopPlatform, Ct)) {
                Assert.True (services.CanConnect (SyncHomeKind.Dropbox));
                Assert.False (services.CanConnect (SyncHomeKind.OneDrive));
                await services.Library.ImportAsync (new MemoryStream (Book ()), "book.epub", Ct);
                await services.Sync!.SyncOnceAsync (Ct);
            }

            // The phone signs in on its own (its own refresh token) and finds the book.
            var phoneSecrets = new InMemorySecretStore ();
            var phonePlatform = laptopPlatform with { Secrets = phoneSecrets };
            var phone = Path.Combine (temp, "phone");
            await AppServices.SetSyncHomeAsync (phone, await SyncHome.ConnectCloudAsync (SyncHomeKind.Dropbox, phonePlatform, Ct), Ct);
            await using (var services = await AppServices.OpenAsync (phone, phonePlatform, Ct)) {
                await services.Sync!.SyncOnceAsync (Ct);
                Assert.Equal ("Test Book", Assert.Single (await services.Library.ListAsync (Ct)).Title);
            }

            AssertNoSecretIn (laptop, "refresh-secret");
            AssertNoSecretIn (laptop, "access-secret");
            Assert.DoesNotContain (dropbox.AllFileText (), t => t.Contains ("secret", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ARevokedSignInSaysSoInsteadOfRetryingForever ()
        {
            var dropbox = new FakeDropboxServer ();
            var auth = new TokenServer ();
            var platform = new PlatformServices (new InMemorySecretStore (), _ => new FakeBrowserBroker (), new CloudClients (DropboxAppKey: "app-key-1"), () => new NonDisposingHandler (new Router (dropbox, auth)));
            var laptop = Path.Combine (temp, "laptop");
            await AppServices.SetSyncHomeAsync (laptop, await SyncHome.ConnectCloudAsync (SyncHomeKind.Dropbox, platform, Ct), Ct);

            auth.Revoked = true;
            await using var services = await AppServices.OpenAsync (laptop, platform, Ct);
            await Assert.ThrowsAsync<StorageAuthenticationException> (() => services.Sync!.SyncOnceAsync (Ct));
            Assert.Equal (SyncState.SignInNeeded, services.Sync!.Status.State);
        }

        /// <summary>No secret in local settings or the library copy: the secure store is the only place for it.</summary>
        private static void AssertNoSecretIn (string dataDirectory, string secret)
        {
            foreach (var file in Directory.EnumerateFiles (dataDirectory, "*", SearchOption.AllDirectories)) {
                if (file.EndsWith (".epub", StringComparison.Ordinal) || file.EndsWith (".jpg", StringComparison.Ordinal))
                    continue;
                Assert.DoesNotContain (secret, File.ReadAllText (file), StringComparison.Ordinal);
            }
        }

        public void Dispose ()
        {
            if (Directory.Exists (temp))
                Directory.Delete (temp, recursive: true);
        }

        /// <summary>Dropbox's token endpoint: hands out tokens with "secret" in them, so a leak is easy to spot.</summary>
        private sealed class TokenServer : HttpMessageHandler
        {
            private int issued;
            public bool Revoked { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var form = OAuthClient.ParseQuery (await request.Content!.ReadAsStringAsync (cancellationToken));
                if (Revoked && form["grant_type"] == "refresh_token")
                    return new HttpResponseMessage (HttpStatusCode.BadRequest) { Content = new StringContent ("""{"error":"invalid_grant"}""") };
                var n = issued++;
                var refresh = form["grant_type"] == "authorization_code" ? $",\"refresh_token\":\"refresh-secret-{n}\"" : "";
                return new HttpResponseMessage (HttpStatusCode.OK) { Content = new StringContent ($"{{\"access_token\":\"access-secret-{n}\",\"expires_in\":14400{refresh}}}", Encoding.UTF8, "application/json") };
            }
        }

        /// <summary>The token endpoint to the token server, everything else to the API (dropping the bearer token the fake does not check).</summary>
        private sealed class Router (HttpMessageHandler api, HttpMessageHandler tokens) : HttpMessageHandler
        {
            private readonly HttpMessageInvoker api_invoker = new (api, disposeHandler: false);
            private readonly HttpMessageInvoker token_invoker = new (tokens, disposeHandler: false);

            protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
                => request.RequestUri!.AbsolutePath.EndsWith ("/oauth2/token", StringComparison.Ordinal)
                    ? token_invoker.SendAsync (request, cancellationToken)
                    : api_invoker.SendAsync (request, cancellationToken);
        }

        /// <summary>A sign-in where the user always agrees: answers the redirect with a code and the request's state.</summary>
        private sealed class FakeBrowserBroker : IAuthorizationBroker
        {
            public Task<Uri> StartAsync (CancellationToken ct) => Task.FromResult (new Uri ("http://127.0.0.1:5555/"));

            public Task<Uri> AuthorizeAsync (Uri authorizeUri, CancellationToken ct)
            {
                var query = OAuthClient.ParseQuery (authorizeUri.Query);
                Assert.Equal ("app-key-1", query["client_id"]);
                Assert.Equal ("offline", query["token_access_type"]);
                return Task.FromResult (new Uri ($"http://127.0.0.1:5555/?code=c&state={query["state"]}"));
            }
        }

        /// <summary>Shares one fake service between the many HttpClients the app makes.</summary>
        private sealed class NonDisposingHandler (HttpMessageHandler inner) : DelegatingHandler (new Passthrough (inner))
        {
            private sealed class Passthrough (HttpMessageHandler inner) : HttpMessageHandler
            {
                private readonly HttpMessageInvoker invoker = new (inner, disposeHandler: false);

                protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
                    => invoker.SendAsync (request, cancellationToken);
            }
        }
    }
}
