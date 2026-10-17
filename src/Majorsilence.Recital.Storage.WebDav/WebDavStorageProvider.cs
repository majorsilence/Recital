using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace Majorsilence.Recital.Storage.WebDav
{
    /// <summary>
    /// A folder on a WebDAV server (RFC 4918): Nextcloud, ownCloud, Apache mod_dav, a NAS. Logical paths are
    /// relative to <see cref="BaseUri"/>. Folders are created as needed; ETags come from the server
    /// (<c>getetag</c>) and conditional writes use <c>If-Match</c>.
    /// </summary>
    /// <remarks>
    /// WebDAV has no change feed, so <see cref="GetChangesAsync"/> is a recursive listing (Depth: 1 at a time;
    /// many servers refuse Depth: infinity). The <see cref="HttpClient"/> carries the authentication (see
    /// <see cref="CreateClient"/>); a 401 or 403 becomes <see cref="StorageAuthenticationException"/> and a
    /// network failure or 5xx <see cref="StorageUnavailableException"/>.
    /// </remarks>
    public sealed class WebDavStorageProvider : IStorageProvider
    {
        private static readonly XNamespace Dav = "DAV:";
        private static readonly HttpMethod Propfind = new ("PROPFIND");
        private static readonly HttpMethod Mkcol = new ("MKCOL");

        private const string PropfindBody = """
            <?xml version="1.0" encoding="utf-8"?>
            <d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/></d:prop></d:propfind>
            """;

        private readonly HttpClient http;
        private readonly HashSet<string> known_folders = new (StringComparer.Ordinal);

        /// <param name="baseUri">The folder that is this provider's root, e.g. <c>https://cloud.example.com/remote.php/dav/files/me/</c>.</param>
        /// <param name="http">Sends the requests; it carries the credentials.</param>
        public WebDavStorageProvider (Uri baseUri, HttpClient http)
        {
            var text = baseUri.AbsoluteUri;
            BaseUri = new Uri (text.EndsWith ('/') ? text : text + "/");
            this.http = http;
        }

        public Uri BaseUri { get; }

        public string Id => "webdav";

        public StorageCapabilities Capabilities => StorageCapabilities.ETags | StorageCapabilities.Browse;

        /// <summary>An <see cref="HttpClient"/> that sends HTTP Basic credentials (over HTTPS, or to a server on this machine or network).</summary>
        public static HttpClient CreateClient (string? userName, string? password, HttpMessageHandler? handler = null)
        {
            var client = handler is null ? new HttpClient () : new HttpClient (handler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds (100);
            if (!string.IsNullOrEmpty (userName))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue ("Basic",
                    Convert.ToBase64String (Encoding.UTF8.GetBytes (userName + ":" + (password ?? ""))));
            return client;
        }

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            var entries = await PropfindAsync (folder, isFolder: true, depth: "1", ct).ConfigureAwait (false);
            if (entries is null)
                return [];
            return entries.Where (e => e.Path != folder).OrderBy (e => e.Path, StringComparer.Ordinal).ToList ();
        }

        public async Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Get, UriOf (path, false)), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait (false);
            if (response.StatusCode == HttpStatusCode.NotFound) {
                response.Dispose ();
                throw new FileNotFoundException ($"No file '{path}'.", path);
            }
            await EnsureSuccessAsync (response, path).ConfigureAwait (false);
            return new ResponseStream (await response.Content.ReadAsStreamAsync (ct).ConfigureAwait (false), response);
        }

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0)
                throw new ArgumentException ("Cannot write to the root.", nameof (path));

            await EnsureFolderAsync (StoragePath.Parent (path), ct).ConfigureAwait (false);

            // Buffered: a retry after a dropped connection needs the content again.
            var buffer = new MemoryStream ();
            await content.CopyToAsync (buffer, ct).ConfigureAwait (false);
            var bytes = buffer.ToArray ();

            // A weak ETag (Apache gives one for a second after each write) never satisfies If-Match, so it is
            // compared here instead; like a local folder, that leaves a moment for another writer.
            var sendIfMatch = ifMatchEtag;
            if (ifMatchEtag is not null && ifMatchEtag.StartsWith ("W/", StringComparison.Ordinal)) {
                if ((await StatAsync (path, ct).ConfigureAwait (false))?.ETag != ifMatchEtag)
                    throw new StoragePreconditionFailedException (path);
                sendIfMatch = null;
            }

            using var response = await SendAsync (() => {
                var request = new HttpRequestMessage (HttpMethod.Put, UriOf (path, false)) {
                    Content = new ByteArrayContent (bytes),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue ("application/octet-stream");
                if (sendIfMatch is not null)
                    request.Headers.TryAddWithoutValidation ("If-Match", "\"" + sendIfMatch + "\"");
                return request;
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);

            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new StoragePreconditionFailedException (path);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new IOException ($"'{path}' clashes with an existing file or folder.");
            await EnsureSuccessAsync (response, path).ConfigureAwait (false);

            // The PUT response's ETag is not reliable across servers (some send a weak one, some none): ask.
            return await StatAsync (path, ct).ConfigureAwait (false)
                ?? new StorageEntry (path, false, bytes.Length, DateTimeOffset.UtcNow, Unquote (response.Headers.ETag?.Tag));
        }

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            var entries = await PropfindAsync (path, isFolder: path.Length == 0, depth: "0", ct).ConfigureAwait (false);
            return entries?.FirstOrDefault ();
        }

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            var files = new List<StorageEntry> ();
            var folders = new Stack<string> ([""]);
            while (folders.TryPop (out var folder))
                foreach (var entry in await ListAsync (folder, ct).ConfigureAwait (false))
                    if (entry.IsFolder)
                        folders.Push (entry.Path);
                    else
                        files.Add (entry);
            return new ChangeSet (files, DateTimeOffset.UtcNow.UtcTicks.ToString (CultureInfo.InvariantCulture), true);
        }

        private async Task EnsureFolderAsync (string folder, CancellationToken ct)
        {
            if (known_folders.Contains (folder))
                return;
            if (folder.Length == 0) {
                // The configured folder itself: created (with any missing parents) the first time it is needed.
                await MakeCollectionAsync (BaseUri, ct).ConfigureAwait (false);
            } else {
                await EnsureFolderAsync (StoragePath.Parent (folder), ct).ConfigureAwait (false);
                using var response = await SendAsync (() => new HttpRequestMessage (Mkcol, UriOf (folder, true)), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                // 405: it already exists.
                if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
                    await EnsureSuccessAsync (response, folder).ConfigureAwait (false);
            }
            lock (known_folders)
                known_folders.Add (folder);
        }

        /// <summary>MKCOL <paramref name="uri"/>, first creating its parents when the server says they are missing (409).</summary>
        private async Task MakeCollectionAsync (Uri uri, CancellationToken ct)
        {
            using var response = await SendAsync (() => new HttpRequestMessage (Mkcol, uri), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed || response.IsSuccessStatusCode)
                return;
            if (response.StatusCode == HttpStatusCode.Conflict && uri.AbsolutePath.TrimEnd ('/').LastIndexOf ('/') is > 0 and var slash) {
                await MakeCollectionAsync (new Uri (uri, uri.AbsolutePath[..(slash + 1)]), ct).ConfigureAwait (false);
                using var retry = await SendAsync (() => new HttpRequestMessage (Mkcol, uri), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                if (retry.StatusCode != HttpStatusCode.MethodNotAllowed)
                    await EnsureSuccessAsync (retry, "").ConfigureAwait (false);
                return;
            }
            await EnsureSuccessAsync (response, "").ConfigureAwait (false);
        }

        /// <summary>The entries a PROPFIND returns, or null when the resource does not exist.</summary>
        private async Task<List<StorageEntry>?> PropfindAsync (string path, bool isFolder, string depth, CancellationToken ct)
        {
            var uri = UriOf (path, isFolder);
            using var response = await SendAsync (() => {
                var request = new HttpRequestMessage (Propfind, uri) {
                    Content = new StringContent (PropfindBody, Encoding.UTF8, "application/xml"),
                };
                request.Headers.Add ("Depth", depth);
                return request;
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync (response, path).ConfigureAwait (false);

            var xml = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
            XDocument document;
            try {
                document = XDocument.Parse (xml);
            } catch (System.Xml.XmlException ex) {
                throw new StorageUnavailableException ($"The server's answer for '{path}' was not WebDAV XML.", ex);
            }

            var entries = new List<StorageEntry> ();
            foreach (var item in document.Root?.Elements (Dav + "response") ?? []) {
                var href = item.Element (Dav + "href")?.Value;
                if (href is null || LogicalPath (href) is not { } logical)
                    continue;

                // The properties in the 200 propstat; a server lists the ones it lacks under 404.
                var props = item.Elements (Dav + "propstat")
                    .Where (ps => ps.Element (Dav + "status")?.Value.Contains (" 200 ", StringComparison.Ordinal) != false)
                    .Select (ps => ps.Element (Dav + "prop"))
                    .FirstOrDefault (p => p is not null);
                if (props is null)
                    continue;

                var folder = props.Element (Dav + "resourcetype")?.Element (Dav + "collection") is not null;
                long.TryParse (props.Element (Dav + "getcontentlength")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size);
                var modified = DateTimeOffset.TryParse (props.Element (Dav + "getlastmodified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
                    ? m.ToUniversalTime () : DateTimeOffset.MinValue;
                var etag = Unquote (props.Element (Dav + "getetag")?.Value);
                entries.Add (new StorageEntry (logical, folder, folder ? 0 : size, modified, folder ? null : etag ?? FormattableString.Invariant ($"{modified.UtcTicks:x}-{size:x}")));
            }
            return entries;
        }

        /// <summary>The logical path of an href in a PROPFIND answer (absolute URL or absolute path), or null when it is outside the root.</summary>
        private string? LogicalPath (string href)
        {
            var uri = Uri.TryCreate (href, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith ("http", StringComparison.Ordinal)
                ? absolute
                : new Uri (BaseUri, href);
            var basePath = BaseUri.AbsolutePath;
            var path = uri.AbsolutePath;
            if (!path.StartsWith (basePath, StringComparison.Ordinal)) {
                // Some servers encode differently from us (%20 vs +, case of hex digits): compare decoded.
                var decodedBase = Uri.UnescapeDataString (basePath);
                var decoded = Uri.UnescapeDataString (path);
                if (!decoded.StartsWith (decodedBase, StringComparison.Ordinal))
                    return path.TrimEnd ('/') == basePath.TrimEnd ('/') ? "" : null;
                path = decoded[decodedBase.Length..];
            } else {
                path = Uri.UnescapeDataString (path[basePath.Length..]);
            }
            try {
                return StoragePath.Normalize (path);
            } catch (ArgumentException) {
                return null;
            }
        }

        private Uri UriOf (string logical, bool isFolder)
        {
            if (logical.Length == 0)
                return BaseUri;
            var escaped = string.Join ('/', logical.Split ('/').Select (Uri.EscapeDataString));
            return new Uri (BaseUri, escaped + (isFolder ? "/" : ""));
        }

        /// <summary>Sends a request, once more after a dropped connection or a 502/503/504.</summary>
        private async Task<HttpResponseMessage> SendAsync (Func<HttpRequestMessage> create, HttpCompletionOption completion, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++) {
                using var request = create ();
                HttpResponseMessage response;
                try {
                    response = await http.SendAsync (request, completion, ct).ConfigureAwait (false);
                } catch (HttpRequestException ex) {
                    if (attempt == 0)
                        continue;
                    throw new StorageUnavailableException ($"Could not reach {BaseUri.Host}: {ex.Message}", ex);
                } catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
                    throw new StorageUnavailableException ($"{BaseUri.Host} did not answer in time.", ex);
                }
                if (attempt == 0 && (int)response.StatusCode is 502 or 503 or 504) {
                    response.Dispose ();
                    await Task.Delay (500, ct).ConfigureAwait (false);
                    continue;
                }
                return response;
            }
        }

        private async Task EnsureSuccessAsync (HttpResponseMessage response, string path)
        {
            if (response.IsSuccessStatusCode)
                return;
            var code = (int)response.StatusCode;
            if (code is 401 or 403)
                throw new StorageAuthenticationException ($"{BaseUri.Host} refused the user name or password ({code}).");
            var detail = await ReadSnippetAsync (response).ConfigureAwait (false);
            if (code >= 500)
                throw new StorageUnavailableException ($"{BaseUri.Host} failed on '{path}' ({code}){detail}");
            throw new IOException ($"{BaseUri.Host} refused '{path}' ({code}){detail}");
        }

        private static async Task<string> ReadSnippetAsync (HttpResponseMessage response)
        {
            try {
                var text = await response.Content.ReadAsStringAsync ().ConfigureAwait (false);
                text = text.Trim ();
                return text.Length == 0 ? "" : ": " + (text.Length > 200 ? text[..200] : text);
            } catch (Exception ex) when (ex is HttpRequestException or IOException) {
                return "";
            }
        }

        /// <summary>An ETag without its quotes; a weak one keeps its <c>W/</c> marker (it cannot be used with If-Match).</summary>
        private static string? Unquote (string? etag)
        {
            if (string.IsNullOrWhiteSpace (etag))
                return null;
            etag = etag.Trim ();
            return etag.StartsWith ("W/", StringComparison.Ordinal) ? "W/" + etag[2..].Trim ('"') : etag.Trim ('"');
        }

        /// <summary>A response body that disposes its response with it.</summary>
        private sealed class ResponseStream (Stream inner, HttpResponseMessage response) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException ();
            public override long Position { get => throw new NotSupportedException (); set => throw new NotSupportedException (); }
            public override void Flush () { }
            public override int Read (byte[] buffer, int offset, int count) => inner.Read (buffer, offset, count);
            public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync (buffer, cancellationToken);
            public override Task<int> ReadAsync (byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync (buffer, offset, count, cancellationToken);
            public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
            public override void SetLength (long value) => throw new NotSupportedException ();
            public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

            protected override void Dispose (bool disposing)
            {
                if (disposing) {
                    inner.Dispose ();
                    response.Dispose ();
                }
                base.Dispose (disposing);
            }
        }
    }
}
