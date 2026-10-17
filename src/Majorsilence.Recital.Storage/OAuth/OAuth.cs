using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Majorsilence.Recital.Storage.OAuth
{
    /// <summary>One OAuth 2.0 authorization server and this app's registration with it (public client, PKCE).</summary>
    /// <param name="ClientSecret">Only for servers that insist on one for installed apps (Google's desktop clients); it is not secret there and is never used in the browser build.</param>
    /// <param name="ExtraAuthorizeParameters">Provider specifics: Google's <c>access_type=offline</c>, Dropbox's <c>token_access_type=offline</c>.</param>
    public sealed record OAuthConfig (
        string Name,
        Uri AuthorizeEndpoint,
        Uri TokenEndpoint,
        string ClientId,
        string Scope,
        string? ClientSecret = null,
        IReadOnlyDictionary<string, string>? ExtraAuthorizeParameters = null);

    /// <param name="ExpiresAt">When the access token stops working (a little early, to allow for clock skew).</param>
    public sealed record OAuthTokens (string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Gets the user's consent: opens <paramref name="authorizeUri"/> (the system browser on the desktop) and
    /// returns the URI the server redirected back to, carrying <c>code</c> and <c>state</c>.
    /// </summary>
    public interface IAuthorizationBroker
    {
        /// <summary>The redirect URI to register and send (a loopback URL on the desktop).</summary>
        Task<Uri> StartAsync (CancellationToken ct);

        Task<Uri> AuthorizeAsync (Uri authorizeUri, CancellationToken ct);
    }

    /// <summary>The user declined, or the server refused the authorization or a refresh (the refresh token is no longer valid).</summary>
    public sealed class OAuthException (string message, string? error = null) : Exception (message)
    {
        /// <summary>The server's <c>error</c> code (<c>invalid_grant</c>, <c>access_denied</c>), when it gave one.</summary>
        public string? Error { get; } = error;
    }

    /// <summary>The authorization-code flow with PKCE (RFC 7636), and refreshing.</summary>
    public sealed class OAuthClient (OAuthConfig config, HttpClient http, TimeProvider? time = null)
    {
        private readonly TimeProvider time = time ?? TimeProvider.System;

        public OAuthConfig Config => config;

        /// <summary>Signs in through <paramref name="broker"/>: consent in the browser, then the code exchanged for tokens.</summary>
        public async Task<OAuthTokens> SignInAsync (IAuthorizationBroker broker, CancellationToken ct = default)
        {
            var verifier = Base64Url (RandomNumberGenerator.GetBytes (32));
            var challenge = Base64Url (SHA256.HashData (Encoding.ASCII.GetBytes (verifier)));
            var state = Base64Url (RandomNumberGenerator.GetBytes (16));
            var redirect = await broker.StartAsync (ct).ConfigureAwait (false);

            var query = new Dictionary<string, string> {
                ["response_type"] = "code",
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["scope"] = config.Scope,
                ["state"] = state,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
            };
            foreach (var (key, value) in config.ExtraAuthorizeParameters ?? new Dictionary<string, string> ())
                query[key] = value;
            var authorize = new UriBuilder (config.AuthorizeEndpoint) { Query = Encode (query) }.Uri;

            var callback = await broker.AuthorizeAsync (authorize, ct).ConfigureAwait (false);
            var answer = ParseQuery (callback.Query);
            if (answer.TryGetValue ("error", out var error))
                throw new OAuthException (error == "access_denied" ? $"{config.Name} sign-in was cancelled." : $"{config.Name} refused the sign-in: {error}.", error);
            if (!answer.TryGetValue ("state", out var returned) || returned != state)
                throw new OAuthException ($"{config.Name} sign-in answer did not match the request (state).");
            if (!answer.TryGetValue ("code", out var code))
                throw new OAuthException ($"{config.Name} sign-in answer had no code.");

            return await TokenRequestAsync (new Dictionary<string, string> {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirect.AbsoluteUri,
                ["code_verifier"] = verifier,
            }, null, ct).ConfigureAwait (false);
        }

        /// <summary>A new access token for a refresh token. A server that rotates refresh tokens returns a new one.</summary>
        /// <exception cref="OAuthException">The refresh token is no longer valid; sign in again.</exception>
        public Task<OAuthTokens> RefreshAsync (string refreshToken, CancellationToken ct = default)
            => TokenRequestAsync (new Dictionary<string, string> {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
            }, refreshToken, ct);

        private async Task<OAuthTokens> TokenRequestAsync (Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
        {
            form["client_id"] = config.ClientId;
            if (config.ClientSecret is { Length: > 0 } secret && !OperatingSystem.IsBrowser ())
                form["client_secret"] = secret;

            HttpResponseMessage response;
            try {
                response = await http.PostAsync (config.TokenEndpoint, new FormUrlEncodedContent (form), ct).ConfigureAwait (false);
            } catch (HttpRequestException ex) {
                throw new StorageUnavailableException ($"Could not reach {config.Name}: {ex.Message}", ex);
            }
            using (response) {
                var body = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
                JsonElement json;
                try {
                    json = JsonDocument.Parse (body).RootElement;
                } catch (JsonException) {
                    throw (int)response.StatusCode >= 500
                        ? new StorageUnavailableException ($"{config.Name} sign-in service failed ({(int)response.StatusCode}).")
                        : new OAuthException ($"{config.Name} gave an unreadable answer ({(int)response.StatusCode}).");
                }
                if (!response.IsSuccessStatusCode || !json.TryGetProperty ("access_token", out var access)) {
                    if ((int)response.StatusCode >= 500)
                        throw new StorageUnavailableException ($"{config.Name} sign-in service failed ({(int)response.StatusCode}).");
                    var error = json.TryGetProperty ("error", out var e) ? e.GetString () : null;
                    throw new OAuthException ($"{config.Name} refused: {error ?? response.StatusCode.ToString ()}. Sign in again.", error);
                }
                var lifetime = json.TryGetProperty ("expires_in", out var expires) && expires.TryGetInt32 (out var seconds) ? seconds : 3600;
                var refresh = json.TryGetProperty ("refresh_token", out var r) ? r.GetString () : previousRefresh;
                return new OAuthTokens (access.GetString ()!, refresh, time.GetUtcNow ().AddSeconds (Math.Max (60, lifetime - 60)));
            }
        }

        private static string Base64Url (byte[] bytes) => Convert.ToBase64String (bytes).TrimEnd ('=').Replace ('+', '-').Replace ('/', '_');

        private static string Encode (Dictionary<string, string> values)
            => string.Join ('&', values.Select (kv => Uri.EscapeDataString (kv.Key) + "=" + Uri.EscapeDataString (kv.Value)));

        public static Dictionary<string, string> ParseQuery (string query)
        {
            var result = new Dictionary<string, string> (StringComparer.Ordinal);
            foreach (var part in query.TrimStart ('?').Split ('&', StringSplitOptions.RemoveEmptyEntries)) {
                var equals = part.IndexOf ('=');
                var key = WebUtility.UrlDecode (equals < 0 ? part : part[..equals]);
                result[key] = equals < 0 ? "" : WebUtility.UrlDecode (part[(equals + 1)..]);
            }
            return result;
        }
    }

    /// <summary>
    /// Adds the access token to every request, refreshing it when it expires or a request comes back 401,
    /// and keeps the refresh token in the <see cref="ISecretStore"/> (never anywhere else).
    /// </summary>
    public sealed class OAuthHandler : DelegatingHandler
    {
        private readonly OAuthClient client;
        private readonly ISecretStore secrets;
        private readonly string secret_key;
        private readonly TimeProvider time;
        private readonly SemaphoreSlim gate = new (1, 1);
        private OAuthTokens? tokens;

        /// <param name="secretKey">Where the refresh token is kept, e.g. <c>oauth:gdrive</c>.</param>
        public OAuthHandler (OAuthClient client, ISecretStore secrets, string secretKey, HttpMessageHandler inner, TimeProvider? time = null)
            : base (inner)
        {
            this.client = client;
            this.secrets = secrets;
            secret_key = secretKey;
            this.time = time ?? TimeProvider.System;
        }

        /// <summary>Starts from tokens just obtained by signing in, and stores the refresh token.</summary>
        public async Task UseAsync (OAuthTokens signedIn, CancellationToken ct = default)
        {
            tokens = signedIn;
            if (signedIn.RefreshToken is { } refresh)
                await secrets.SetAsync (secret_key, refresh, ct).ConfigureAwait (false);
        }

        /// <summary>Forgets the tokens here and in the secret store (signing out).</summary>
        public async Task ForgetAsync (CancellationToken ct = default)
        {
            tokens = null;
            await secrets.SetAsync (secret_key, null, ct).ConfigureAwait (false);
        }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await AccessTokenAsync (force: false, cancellationToken).ConfigureAwait (false);
            request.Headers.Authorization = new AuthenticationHeaderValue ("Bearer", token);
            var response = await base.SendAsync (request, cancellationToken).ConfigureAwait (false);
            if (response.StatusCode != HttpStatusCode.Unauthorized || request.Content is StreamContent)
                return response;

            // Revoked early or the clock is off: refresh once and retry (content that can be re-sent only).
            response.Dispose ();
            var fresh = await AccessTokenAsync (force: true, cancellationToken).ConfigureAwait (false);
            using var retry = await CloneAsync (request, cancellationToken).ConfigureAwait (false);
            retry.Headers.Authorization = new AuthenticationHeaderValue ("Bearer", fresh);
            var second = await base.SendAsync (retry, cancellationToken).ConfigureAwait (false);
            if (second.StatusCode == HttpStatusCode.Unauthorized) {
                second.Dispose ();
                throw new StorageAuthenticationException ($"{client.Config.Name} no longer accepts this sign-in. Sign in again.");
            }
            return second;
        }

        private async Task<string> AccessTokenAsync (bool force, CancellationToken ct)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                if (!force && tokens is { } current && current.ExpiresAt > time.GetUtcNow ())
                    return current.AccessToken;
                var refresh = tokens?.RefreshToken ?? await secrets.GetAsync (secret_key, ct).ConfigureAwait (false);
                if (refresh is null)
                    throw new StorageAuthenticationException ($"Not signed in to {client.Config.Name}.");
                try {
                    tokens = await client.RefreshAsync (refresh, ct).ConfigureAwait (false);
                } catch (OAuthException ex) {
                    throw new StorageAuthenticationException (ex.Message, ex);
                }
                if (tokens.RefreshToken is { } rotated && rotated != refresh)
                    await secrets.SetAsync (secret_key, rotated, ct).ConfigureAwait (false);
                return tokens.AccessToken;
            } finally {
                gate.Release ();
            }
        }

        private static async Task<HttpRequestMessage> CloneAsync (HttpRequestMessage request, CancellationToken ct)
        {
            var clone = new HttpRequestMessage (request.Method, request.RequestUri) { Version = request.Version };
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation (header.Key, header.Value);
            if (request.Content is { } content) {
                var bytes = await content.ReadAsByteArrayAsync (ct).ConfigureAwait (false);
                clone.Content = new ByteArrayContent (bytes);
                foreach (var header in content.Headers)
                    clone.Content.Headers.TryAddWithoutValidation (header.Key, header.Value);
            }
            return clone;
        }

        protected override void Dispose (bool disposing)
        {
            if (disposing)
                gate.Dispose ();
            base.Dispose (disposing);
        }
    }
}
