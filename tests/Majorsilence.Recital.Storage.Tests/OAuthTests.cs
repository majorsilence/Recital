using System.Net;
using System.Security.Cryptography;
using System.Text;
using Majorsilence.Recital.Platform;
using Majorsilence.Recital.Storage.OAuth;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    public class OAuthTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly OAuthConfig Config = new ("Test Cloud", new Uri ("https://auth.test/authorize"), new Uri ("https://auth.test/token"), "client-1", "files offline",
            ExtraAuthorizeParameters: new Dictionary<string, string> { ["access_type"] = "offline" });

        /// <summary>A token endpoint: checks PKCE, issues tokens, rotates refresh tokens, and can be told a refresh token was revoked.</summary>
        private sealed class FakeAuthServer : HttpMessageHandler
        {
            public string? Challenge;
            public string? RedirectUri;
            public int Refreshes;
            public bool Revoked;

            protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var form = OAuthClient.ParseQuery (await request.Content!.ReadAsStringAsync (cancellationToken));
                if (form["client_id"] != "client-1")
                    return Json (HttpStatusCode.BadRequest, """{"error":"invalid_client"}""");
                if (form["grant_type"] == "authorization_code") {
                    var expected = Convert.ToBase64String (SHA256.HashData (Encoding.ASCII.GetBytes (form["code_verifier"]))).TrimEnd ('=').Replace ('+', '-').Replace ('/', '_');
                    if (form["code"] != "the-code" || expected != Challenge || form["redirect_uri"] != RedirectUri)
                        return Json (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
                    return Json (HttpStatusCode.OK, """{"access_token":"access-0","refresh_token":"refresh-0","expires_in":3600}""");
                }
                if (Revoked)
                    return Json (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
                Refreshes++;
                return Json (HttpStatusCode.OK, $$"""{"access_token":"access-{{Refreshes}}","refresh_token":"refresh-{{Refreshes}}","expires_in":3600}""");
            }

            private static HttpResponseMessage Json (HttpStatusCode code, string json)
                => new (code) { Content = new StringContent (json, Encoding.UTF8, "application/json") };
        }

        /// <summary>The "browser": reads the authorize URL and follows the redirect back to the loopback listener.</summary>
        private static Action<Uri> Browser (FakeAuthServer server, string? error = null, string? wrongState = null)
            => authorize => {
                var query = OAuthClient.ParseQuery (authorize.Query);
                Assert.Equal ("S256", query["code_challenge_method"]);
                Assert.Equal ("offline", query["access_type"]);
                server.Challenge = query["code_challenge"];
                server.RedirectUri = query["redirect_uri"];
                var answer = error is not null ? $"?error={error}&state={query["state"]}" : $"?code=the-code&state={wrongState ?? query["state"]}";
                _ = Task.Run (async () => {
                    using var http = new HttpClient ();
                    await http.GetAsync (new Uri (new Uri (server.RedirectUri), "favicon.ico"));
                    await http.GetAsync (new Uri (new Uri (server.RedirectUri), answer));
                });
            };

        [Fact]
        public async Task SignsInThroughTheBrowserWithPkce ()
        {
            var server = new FakeAuthServer ();
            using var http = new HttpClient (server);
            using var broker = new LoopbackAuthorizationBroker (Browser (server));
            var tokens = await new OAuthClient (Config, http).SignInAsync (broker, Ct);

            Assert.Equal ("access-0", tokens.AccessToken);
            Assert.Equal ("refresh-0", tokens.RefreshToken);
            Assert.StartsWith ("http://127.0.0.1:", server.RedirectUri, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ACancelledOrForgedAnswerIsRefused ()
        {
            var server = new FakeAuthServer ();
            using var http = new HttpClient (server);
            using (var broker = new LoopbackAuthorizationBroker (Browser (server, error: "access_denied"))) {
                var ex = await Assert.ThrowsAsync<OAuthException> (() => new OAuthClient (Config, http).SignInAsync (broker, Ct));
                Assert.Equal ("access_denied", ex.Error);
            }
            using (var broker = new LoopbackAuthorizationBroker (Browser (server, wrongState: "forged")))
                await Assert.ThrowsAsync<OAuthException> (() => new OAuthClient (Config, http).SignInAsync (broker, Ct));
        }

        /// <summary>An API that accepts only the newest access token.</summary>
        private sealed class FakeApi (FakeAuthServer auth) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult (new HttpResponseMessage (request.Headers.Authorization?.Parameter == $"access-{auth.Refreshes}" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }

        [Fact]
        public async Task TheHandlerRefreshesKeepsTheRefreshTokenSecretAndGivesUpWhenRevoked ()
        {
            var auth = new FakeAuthServer ();
            using var authHttp = new HttpClient (auth);
            var secrets = new InMemorySecretStore ();
            await secrets.SetAsync ("oauth:test", "refresh-0", Ct);
            var now = new DateTimeOffset (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            var clock = new ManualClock (() => now);
            var client = new OAuthClient (Config, authHttp, clock);
            using var api = new HttpClient (new OAuthHandler (client, secrets, "oauth:test", new FakeApi (auth), clock));

            // No access token yet: refreshed from the stored refresh token; the rotated one is stored.
            Assert.Equal (HttpStatusCode.OK, (await api.GetAsync ("https://api.test/files", Ct)).StatusCode);
            Assert.Equal ("refresh-1", await secrets.GetAsync ("oauth:test", Ct));

            // Still valid: no refresh. Expired: refreshed.
            await api.GetAsync ("https://api.test/files", Ct);
            Assert.Equal (1, auth.Refreshes);
            now += TimeSpan.FromHours (2);
            Assert.Equal (HttpStatusCode.OK, (await api.GetAsync ("https://api.test/files", Ct)).StatusCode);
            Assert.Equal (2, auth.Refreshes);

            // Revoked on the server: the next refresh fails with "sign in again".
            auth.Revoked = true;
            now += TimeSpan.FromHours (2);
            await Assert.ThrowsAsync<StorageAuthenticationException> (() => api.GetAsync ("https://api.test/files", Ct));
        }

        private sealed class ManualClock (Func<DateTimeOffset> now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow () => now ();
        }
    }
}
