using Majorsilence.Recital.Storage;
using Majorsilence.Recital.Storage.Dropbox;
using Majorsilence.Recital.Storage.GoogleDrive;
using Majorsilence.Recital.Storage.OAuth;
using Majorsilence.Recital.Storage.OneDrive;
using Majorsilence.Recital.Storage.WebDav;

namespace Majorsilence.Recital.Core.Sync
{
    [System.Text.Json.Serialization.JsonConverter (typeof (CamelCaseEnumConverter<SyncHomeKind>))]
    public enum SyncHomeKind
    {
        None,
        LocalFolder,
        WebDav,
        Dropbox,
        OneDrive,
        GoogleDrive,
    }

    /// <summary>
    /// <c>sync-home.json</c> in local app data: where this device syncs. No secrets: passwords and refresh
    /// tokens are in the <see cref="ISecretStore"/>.
    /// </summary>
    /// <param name="Folder">A local folder (<see cref="SyncHomeKind.LocalFolder"/>). Files from before Phase 7 have only this.</param>
    /// <param name="Url">A WebDAV folder URL.</param>
    /// <param name="User">The WebDAV user name.</param>
    /// <param name="AccountId">The cloud account (so another account's sync home starts afresh).</param>
    /// <param name="AccountName">The cloud account, for display.</param>
    public sealed record SyncHomeSettings (int FormatVersion, string? Folder, SyncHomeKind? Kind = null, string? Url = null, string? User = null, string? AccountId = null, string? AccountName = null)
    {
        public SyncHomeKind EffectiveKind => Kind ?? (string.IsNullOrEmpty (Folder) ? SyncHomeKind.None : SyncHomeKind.LocalFolder);

        /// <summary>Names the sync home for the engine's outbox and index (see <see cref="SyncEngine.RemoteKey"/>).</summary>
        public string RemoteKey => EffectiveKind switch {
            SyncHomeKind.LocalFolder => "local:" + Folder,
            SyncHomeKind.WebDav => "webdav:" + Url + "|" + User,
            SyncHomeKind.None => "",
            var kind => kind.ToString ().ToLowerInvariant () + ":" + AccountId,
        };

        /// <summary>For the status bar and settings: "Dropbox (me@example.com)", "WebDAV on cloud.example.com", a folder.</summary>
        public string DisplayName => EffectiveKind switch {
            SyncHomeKind.LocalFolder => Folder!,
            SyncHomeKind.WebDav => "WebDAV on " + (Uri.TryCreate (Url, UriKind.Absolute, out var uri) ? uri.Host : Url),
            SyncHomeKind.None => "",
            var kind => SyncHome.Label (kind) + (AccountName is { Length: > 0 } name ? $" ({name})" : ""),
        };
    }

    /// <summary>
    /// This build's registrations with the cloud services (plan 2.5). They are public identifiers of the app,
    /// not secrets, set at build time (<c>-p:RecitalDropboxAppKey=...</c>) or by the environment
    /// (<c>RECITAL_DROPBOX_APP_KEY</c>); a service without one cannot be chosen.
    /// </summary>
    public sealed record CloudClients (string? GoogleClientId = null, string? GoogleClientSecret = null, string? DropboxAppKey = null, string? OneDriveClientId = null)
    {
        public static CloudClients None { get; } = new ();

        /// <summary>The build's values, each overridden by its environment variable when that is set.</summary>
        public CloudClients WithEnvironment () => new (
            Env ("RECITAL_GOOGLE_CLIENT_ID") ?? GoogleClientId,
            Env ("RECITAL_GOOGLE_CLIENT_SECRET") ?? GoogleClientSecret,
            Env ("RECITAL_DROPBOX_APP_KEY") ?? DropboxAppKey,
            Env ("RECITAL_ONEDRIVE_CLIENT_ID") ?? OneDriveClientId);

        public OAuthConfig? OAuthFor (SyncHomeKind kind) => kind switch {
            SyncHomeKind.Dropbox when !string.IsNullOrEmpty (DropboxAppKey) => DropboxStorageProvider.OAuth (DropboxAppKey),
            SyncHomeKind.OneDrive when !string.IsNullOrEmpty (OneDriveClientId) => OneDriveStorageProvider.OAuth (OneDriveClientId),
            SyncHomeKind.GoogleDrive when !string.IsNullOrEmpty (GoogleClientId) => GoogleDriveStorageProvider.OAuth (GoogleClientId, GoogleClientSecret),
            _ => null,
        };

        private static string? Env (string name) => Environment.GetEnvironmentVariable (name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>What the app needs from the platform to reach cloud storage: the secure store and a way to sign in.</summary>
    /// <param name="Broker">Makes the sign-in broker for a service (the system browser on the desktop); null where sign-in is not available yet.</param>
    /// <param name="Handler">The HTTP stack (tests put fake services here); null for the default.</param>
    public sealed record PlatformServices (ISecretStore Secrets, Func<SyncHomeKind, IAuthorizationBroker>? Broker, CloudClients Clients, Func<HttpMessageHandler>? Handler = null)
    {
        public static PlatformServices InMemory { get; } = new (new InMemorySecretStore (), null, CloudClients.None);

        internal HttpMessageHandler NewHandler () => Handler?.Invoke () ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes (5) };
    }

    /// <summary>Connecting a sync home and opening its provider.</summary>
    public static class SyncHome
    {
        /// <summary>The folder (logical) synced inside every provider.</summary>
        private static string FeedFolder => SyncPaths.Root;

        public static string Label (SyncHomeKind kind) => kind switch {
            SyncHomeKind.LocalFolder => "Local folder",
            SyncHomeKind.WebDav => "WebDAV",
            SyncHomeKind.Dropbox => "Dropbox",
            SyncHomeKind.OneDrive => "OneDrive",
            SyncHomeKind.GoogleDrive => "Google Drive",
            _ => "None",
        };

        /// <summary>
        /// The loopback port a service's redirect must use. Google and Microsoft accept any port on a loopback
        /// redirect; Dropbox wants the exact URI registered, so it gets a fixed one (docs/CLOUD_SETUP.md).
        /// </summary>
        public static (string Host, int Port) LoopbackRedirect (SyncHomeKind kind) => kind switch {
            SyncHomeKind.Dropbox => ("localhost", 47612),
            SyncHomeKind.OneDrive => ("localhost", 0),
            _ => ("127.0.0.1", 0),
        };

        public static string WebDavSecretKey (string url, string user) => $"webdav:{url}|{user}";

        public static string OAuthSecretKey (SyncHomeKind kind) => "oauth:" + kind.ToString ().ToLowerInvariant ();

        /// <summary>The provider for <paramref name="settings"/>; null for none.</summary>
        public static async Task<IStorageProvider?> OpenAsync (SyncHomeSettings settings, PlatformServices platform, CancellationToken ct = default)
        {
            switch (settings.EffectiveKind) {
            case SyncHomeKind.LocalFolder:
                return new LocalFolderStorageProvider (settings.Folder!, requireRoot: true);
            case SyncHomeKind.WebDav: {
                var password = await platform.Secrets.GetAsync (WebDavSecretKey (settings.Url!, settings.User ?? ""), ct).ConfigureAwait (false);
                return new WebDavStorageProvider (new Uri (settings.Url!), WebDavStorageProvider.CreateClient (settings.User, password, platform.NewHandler ()));
            }
            case SyncHomeKind.Dropbox or SyncHomeKind.OneDrive or SyncHomeKind.GoogleDrive:
                return CloudProvider (settings.EffectiveKind, platform, null).Provider;
            default:
                return null;
            }
        }

        /// <summary>
        /// Checks the WebDAV folder answers to these credentials, then stores the password in the secure store.
        /// Returns the settings to save.
        /// </summary>
        /// <exception cref="StorageAuthenticationException">The server refused the user name or password.</exception>
        /// <exception cref="StorageUnavailableException">The server could not be reached.</exception>
        public static async Task<SyncHomeSettings> ConnectWebDavAsync (Uri url, string user, string password, PlatformServices platform, CancellationToken ct = default)
        {
            if (url.Scheme is not ("https" or "http"))
                throw new ArgumentException ("A WebDAV address starts with https://.", nameof (url));
            using var http = WebDavStorageProvider.CreateClient (user, password, platform.NewHandler ());
            var provider = new WebDavStorageProvider (url, http);
            // A listing proves the folder exists and the credentials work (a missing folder lists as empty and is
            // created on the first write).
            await provider.ListAsync ("", ct).ConfigureAwait (false);
            var normalized = provider.BaseUri.AbsoluteUri;
            await platform.Secrets.SetAsync (WebDavSecretKey (normalized, user), password, ct).ConfigureAwait (false);
            return new SyncHomeSettings (SyncPaths.FormatVersion, null, SyncHomeKind.WebDav, normalized, user);
        }

        /// <summary>
        /// Signs in to a cloud service in the browser, keeps the refresh token in the secure store, and returns
        /// the settings to save.
        /// </summary>
        /// <exception cref="InvalidOperationException">This build has no registration for the service, or the platform cannot sign in.</exception>
        /// <exception cref="OAuthException">The user cancelled, or the service refused.</exception>
        public static async Task<SyncHomeSettings> ConnectCloudAsync (SyncHomeKind kind, PlatformServices platform, CancellationToken ct = default)
        {
            var config = platform.Clients.OAuthFor (kind) ?? throw new InvalidOperationException ($"This build of Recital is not registered with {Label (kind)}.");
            var brokerFactory = platform.Broker ?? throw new InvalidOperationException ("Signing in is not available on this device yet.");
            using var tokenHttp = new HttpClient (platform.NewHandler ());
            var broker = brokerFactory (kind);
            OAuthTokens tokens;
            try {
                tokens = await new OAuthClient (config, tokenHttp).SignInAsync (broker, ct).ConfigureAwait (false);
            } finally {
                (broker as IDisposable)?.Dispose ();
            }
            var (provider, handler) = CloudProvider (kind, platform, config);
            await handler.UseAsync (tokens, ct).ConfigureAwait (false);
            var account = await ((IStorageAccountProvider)provider).GetAccountAsync (ct).ConfigureAwait (false);
            return new SyncHomeSettings (SyncPaths.FormatVersion, null, kind, AccountId: account.Id, AccountName: account.Name);
        }

        /// <summary>Forgets the stored password or refresh token for <paramref name="settings"/>.</summary>
        public static Task ForgetSecretsAsync (SyncHomeSettings settings, PlatformServices platform, CancellationToken ct = default)
            => settings.EffectiveKind switch {
                SyncHomeKind.WebDav => platform.Secrets.SetAsync (WebDavSecretKey (settings.Url!, settings.User ?? ""), null, ct),
                SyncHomeKind.Dropbox or SyncHomeKind.OneDrive or SyncHomeKind.GoogleDrive => platform.Secrets.SetAsync (OAuthSecretKey (settings.EffectiveKind), null, ct),
                _ => Task.CompletedTask,
            };

        private static (IStorageProvider Provider, OAuthHandler Handler) CloudProvider (SyncHomeKind kind, PlatformServices platform, OAuthConfig? config)
        {
            config ??= platform.Clients.OAuthFor (kind);
            // Without a registration the provider still opens, and says "sign in again" when used.
            config ??= new OAuthConfig (Label (kind), new Uri ("https://invalid/"), new Uri ("https://invalid/"), "", "");
            var tokenHttp = new HttpClient (platform.NewHandler ());
            var handler = new OAuthHandler (new OAuthClient (config, tokenHttp), platform.Secrets, OAuthSecretKey (kind), platform.NewHandler ());
            var http = new HttpClient (handler);
            IStorageProvider provider = kind switch {
                SyncHomeKind.Dropbox => new DropboxStorageProvider (http, feedFolder: FeedFolder),
                SyncHomeKind.OneDrive => new OneDriveStorageProvider (http, new HttpClient (platform.NewHandler ()), FeedFolder),
                _ => new GoogleDriveStorageProvider (http, FeedFolder),
            };
            return (provider, handler);
        }
    }
}
