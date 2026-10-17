using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Majorsilence.Recital.Storage.OAuth;

namespace Majorsilence.Recital.Storage.OneDrive
{
    /// <summary>
    /// The user's OneDrive through Microsoft Graph. Logical paths are relative to the drive root; items are
    /// addressed by path (<c>root:/a/b:</c>). ETags are Graph's <c>eTag</c>; conditional writes send
    /// <c>If-Match</c>. Files over 4 MB go through an upload session.
    /// </summary>
    /// <remarks>
    /// The change feed is <c>delta</c> from the drive root (OneDrive for Business supports delta only there),
    /// started at <c>token=latest</c> after listing <see cref="FeedFolder"/> recursively, and filtered to it.
    /// Delta items on personal drives carry no parent path, only the parent's id, so paths are rebuilt from
    /// an id-to-path map (asking Graph for unknown parents).
    /// </remarks>
    public sealed class OneDriveStorageProvider : IStorageProvider, IStorageAccountProvider
    {
        public static readonly Uri GraphDrive = new ("https://graph.microsoft.com/v1.0/me/drive/");
        private const string Select = "$select=id,name,size,lastModifiedDateTime,eTag,file,folder,parentReference,deleted";
        private const int SimpleUploadLimit = 4 * 1024 * 1024;
        private const int ChunkSize = 10 * 320 * 1024;

        /// <summary>The OAuth registration for an Entra ID (Azure AD) public client: personal and work accounts.</summary>
        public static OAuthConfig OAuth (string clientId) => new ("OneDrive",
            new Uri ("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
            new Uri ("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
            clientId, "Files.ReadWrite offline_access User.Read");

        private readonly HttpClient http;
        private readonly HttpClient upload_http;
        private readonly Uri drive;
        private readonly Dictionary<string, string> paths_by_id = new (StringComparer.Ordinal);
        private readonly object paths_gate = new ();
        private string? root_id;

        /// <param name="http">Authenticated (an <see cref="OAuthHandler"/>).</param>
        /// <param name="uploadHttp">Plain, for upload-session URLs, which are pre-authenticated and refuse a bearer token.</param>
        /// <param name="feedFolder">The folder (logical) the change feed covers.</param>
        public OneDriveStorageProvider (HttpClient http, HttpClient? uploadHttp = null, string feedFolder = "", Uri? driveBase = null)
        {
            this.http = http;
            upload_http = uploadHttp ?? new HttpClient ();
            FeedFolder = StoragePath.Normalize (feedFolder);
            drive = driveBase ?? GraphDrive;
        }

        public string FeedFolder { get; }

        public string Id => "onedrive";

        public StorageCapabilities Capabilities => StorageCapabilities.ChangeFeed | StorageCapabilities.ETags | StorageCapabilities.Browse | StorageCapabilities.LargeUpload;

        public async Task<StorageAccount> GetAccountAsync (CancellationToken ct = default)
        {
            // The drive's owner: "../" from /me/drive/ is /me.
            var me = await GetJsonAsync (new Uri (drive, "../?$select=id,displayName,userPrincipalName"), ct).ConfigureAwait (false);
            var id = me!["id"]!.GetValue<string> ();
            return new StorageAccount (id, me["userPrincipalName"]?.GetValue<string> () ?? me["displayName"]?.GetValue<string> () ?? id);
        }

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            var entries = new List<StorageEntry> ();
            var next = At (Item (folder, ":/children") + "?" + Select + "&$top=200");
            while (next is not null) {
                var page = await GetJsonAsync (next, ct, notFoundIsNull: true).ConfigureAwait (false);
                if (page is null)
                    return [];
                foreach (var item in page["value"]?.AsArray () ?? []) {
                    var name = item!["name"]!.GetValue<string> ();
                    if (TryCombine (folder, name) is { } path && Entry (item, path) is { } entry) {
                        Remember (item, path);
                        entries.Add (entry);
                    }
                }
                next = page["@odata.nextLink"]?.GetValue<string> () is { } link ? new Uri (link) : null;
            }
            return entries.OrderBy (e => e.Path, StringComparer.Ordinal).ToList ();
        }

        public async Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            // Graph answers with a redirect to a pre-authenticated download URL; HttpClient follows it and drops
            // the Authorization header for the other host.
            var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Get, At (Item (path, ":/content"))), http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait (false);
            if (response.StatusCode == HttpStatusCode.NotFound) {
                response.Dispose ();
                throw new FileNotFoundException ($"No file '{path}'.", path);
            }
            await EnsureSuccessAsync (response, path, ct).ConfigureAwait (false);
            return await response.Content.ReadAsStreamAsync (ct).ConfigureAwait (false);
        }

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0)
                throw new ArgumentException ("Cannot write to the root.", nameof (path));
            var buffer = new MemoryStream ();
            await content.CopyToAsync (buffer, ct).ConfigureAwait (false);
            var bytes = buffer.ToArray ();

            JsonNode? item;
            if (bytes.Length <= SimpleUploadLimit) {
                using var response = await SendAsync (() => {
                    var request = new HttpRequestMessage (HttpMethod.Put, At (Item (path, ":/content"))) { Content = new ByteArrayContent (bytes) };
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue ("application/octet-stream");
                    if (ifMatchEtag is not null)
                        request.Headers.TryAddWithoutValidation ("If-Match", ifMatchEtag);
                    return request;
                }, http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                item = await ReadWriteAnswerAsync (response, path, ct).ConfigureAwait (false);
            } else {
                item = await UploadSessionAsync (path, bytes, ifMatchEtag, ct).ConfigureAwait (false);
            }
            Remember (item!, path);
            return Entry (item, path) ?? throw new IOException ($"OneDrive gave no metadata for '{path}'.");
        }

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            var item = await GetJsonAsync (At (Item (path, "") + "?" + Select), ct, notFoundIsNull: true).ConfigureAwait (false);
            if (item is null)
                return null;
            Remember (item, path);
            return Entry (item, path);
        }

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            if (cursor is null) {
                // Where "now" is in the feed first, then everything there is: nothing written in between is missed.
                var latest = await GetJsonAsync (At ("root/delta?token=latest"), ct).ConfigureAwait (false);
                var deltaLink = latest!["@odata.deltaLink"]!.GetValue<string> ();
                var all = new List<StorageEntry> ();
                var folders = new Stack<string> ([FeedFolder]);
                while (folders.TryPop (out var folder))
                    foreach (var entry in await ListAsync (folder, ct).ConfigureAwait (false))
                        if (entry.IsFolder)
                            folders.Push (entry.Path);
                        else
                            all.Add (entry);
                return new ChangeSet (all, deltaLink, true);
            }

            var changed = new List<StorageEntry> ();
            var next = new Uri (cursor);
            while (true) {
                var page = await GetJsonAsync (next, ct, goneIsNull: true).ConfigureAwait (false);
                if (page is null)
                    return await GetChangesAsync (null, ct).ConfigureAwait (false); // The token expired (410): start again.
                foreach (var item in page["value"]?.AsArray () ?? []) {
                    if (item?["deleted"] is not null || item?["file"] is null)
                        continue;
                    var path = await PathOfAsync (item, ct).ConfigureAwait (false);
                    if (path is null || !StoragePath.IsWithin (path, FeedFolder))
                        continue;
                    if (Entry (item, path) is { } entry)
                        changed.Add (entry);
                }
                if (page["@odata.nextLink"]?.GetValue<string> () is { } nextLink) {
                    next = new Uri (nextLink);
                    continue;
                }
                return new ChangeSet (changed, page["@odata.deltaLink"]?.GetValue<string> () ?? cursor, false);
            }
        }

        private async Task<JsonNode?> UploadSessionAsync (string path, byte[] bytes, string? ifMatchEtag, CancellationToken ct)
        {
            var body = new JsonObject { ["item"] = new JsonObject { ["@microsoft.graph.conflictBehavior"] = "replace" } }.ToJsonString ();
            using var start = await SendAsync (() => {
                var request = new HttpRequestMessage (HttpMethod.Post, At (Item (path, ":/createUploadSession"))) {
                    Content = new StringContent (body, Encoding.UTF8, "application/json"),
                };
                if (ifMatchEtag is not null)
                    request.Headers.TryAddWithoutValidation ("If-Match", ifMatchEtag);
                return request;
            }, http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            var session = await ReadWriteAnswerAsync (start, path, ct).ConfigureAwait (false);
            var url = new Uri (session!["uploadUrl"]!.GetValue<string> ());

            JsonNode? item = null;
            for (var offset = 0; offset < bytes.Length; offset += ChunkSize) {
                var length = Math.Min (ChunkSize, bytes.Length - offset);
                var first = offset;
                using var response = await SendAsync (() => {
                    var request = new HttpRequestMessage (HttpMethod.Put, url) { Content = new ByteArrayContent (bytes, first, length) };
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue (first, first + length - 1, bytes.Length);
                    return request;
                }, upload_http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                    item = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
                else if (response.StatusCode != HttpStatusCode.Accepted)
                    await EnsureSuccessAsync (response, path, ct).ConfigureAwait (false);
            }
            return item ?? throw new IOException ($"OneDrive did not finish the upload of '{path}'.");
        }

        private static async Task<JsonNode?> ReadWriteAnswerAsync (HttpResponseMessage response, string path, CancellationToken ct)
        {
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new StoragePreconditionFailedException (path);
            if (response.StatusCode == HttpStatusCode.Conflict)
                throw new IOException ($"'{path}' clashes with an existing file or folder.");
            await EnsureSuccessAsync (response, path, ct).ConfigureAwait (false);
            return JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
        }

        /// <summary>The path of a delta item: its parent path when Graph gives one, else rebuilt from the parent's id.</summary>
        private async Task<string?> PathOfAsync (JsonNode item, CancellationToken ct)
        {
            var name = item["name"]?.GetValue<string> ();
            if (name is null)
                return null;
            var parent = item["parentReference"];
            if (parent?["path"]?.GetValue<string> () is { } parentPath)
                return ParentPath (parentPath) is { } p ? TryCombine (p, name) : null;
            if (parent?["id"]?.GetValue<string> () is not { } parentId)
                return null;
            var folder = await PathOfIdAsync (parentId, 0, ct).ConfigureAwait (false);
            return folder is null ? null : TryCombine (folder, name);
        }

        private async Task<string?> PathOfIdAsync (string id, int depth, CancellationToken ct)
        {
            root_id ??= (await GetJsonAsync (At ("root?$select=id"), ct).ConfigureAwait (false))?["id"]?.GetValue<string> ();
            if (id == root_id)
                return "";
            lock (paths_gate) {
                if (paths_by_id.TryGetValue (id, out var known))
                    return known;
            }
            if (depth > 64)
                return null;
            var item = await GetJsonAsync (At ("items/" + Uri.EscapeDataString (id) + "?$select=id,name,parentReference"), ct, notFoundIsNull: true).ConfigureAwait (false);
            if (item?["name"]?.GetValue<string> () is not { } name || item["parentReference"]?["id"]?.GetValue<string> () is not { } parentId)
                return null;
            var parent = await PathOfIdAsync (parentId, depth + 1, ct).ConfigureAwait (false);
            var path = parent is null ? null : TryCombine (parent, name);
            if (path is not null)
                lock (paths_gate)
                    paths_by_id[id] = path;
            return path;
        }

        private void Remember (JsonNode item, string path)
        {
            if (item["id"]?.GetValue<string> () is { } id)
                lock (paths_gate)
                    paths_by_id[id] = path;
        }

        /// <summary>"/drive/root:/Recital/state" (escaped or not) as "Recital/state"; null outside the root.</summary>
        private static string? ParentPath (string parentPath)
        {
            var marker = parentPath.IndexOf ("root:", StringComparison.Ordinal);
            if (marker < 0)
                return null;
            try {
                return StoragePath.Normalize (Uri.UnescapeDataString (parentPath[(marker + 5)..]));
            } catch (ArgumentException) {
                return null;
            }
        }

        private static string? TryCombine (string folder, string name)
        {
            try {
                return StoragePath.Combine (folder, name);
            } catch (ArgumentException) {
                return null;
            }
        }

        private static StorageEntry? Entry (JsonNode? item, string path)
        {
            if (item is null)
                return null;
            var modified = DateTimeOffset.TryParse (item["lastModifiedDateTime"]?.GetValue<string> (), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : DateTimeOffset.MinValue;
            if (item["folder"] is not null)
                return new StorageEntry (path, true, 0, modified, null);
            return new StorageEntry (path, false, item["size"]?.GetValue<long> () ?? 0, modified, item["eTag"]?.GetValue<string> ());
        }

        /// <summary>
        /// An absolute URL under the drive. Joined as text: <c>new Uri (base, "root:/a")</c> would read
        /// <c>root:</c> as a URI scheme.
        /// </summary>
        private Uri At (string relative) => new (drive.AbsoluteUri + relative);

        /// <summary>The relative URL of an item by path: <c>root</c>, or <c>root:/a/b</c> plus <paramref name="suffix"/>.</summary>
        private static string Item (string logical, string suffix)
        {
            if (logical.Length == 0)
                return "root" + (suffix.StartsWith (":/", StringComparison.Ordinal) ? "/" + suffix[2..] : suffix);
            return "root:/" + string.Join ('/', logical.Split ('/').Select (Uri.EscapeDataString)) + (suffix.Length == 0 ? ":" : suffix);
        }

        private async Task<JsonNode?> GetJsonAsync (Uri uri, CancellationToken ct, bool notFoundIsNull = false, bool goneIsNull = false)
        {
            using var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Get, uri), http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (goneIsNull && response.StatusCode == HttpStatusCode.Gone)
                return null;
            await EnsureSuccessAsync (response, uri.AbsolutePath, ct).ConfigureAwait (false);
            return JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
        }

        /// <summary>Sends, waiting out one 429/503 (Retry-After) or 5xx, and once more after a dropped connection.</summary>
        private static async Task<HttpResponseMessage> SendAsync (Func<HttpRequestMessage> create, HttpClient client, HttpCompletionOption completion, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++) {
                using var request = create ();
                HttpResponseMessage response;
                try {
                    response = await client.SendAsync (request, completion, ct).ConfigureAwait (false);
                } catch (HttpRequestException ex) {
                    if (attempt == 0)
                        continue;
                    throw new StorageUnavailableException ($"Could not reach OneDrive: {ex.Message}", ex);
                } catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
                    throw new StorageUnavailableException ("OneDrive did not answer in time.", ex);
                }
                if (attempt == 0 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)) {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds (1);
                    response.Dispose ();
                    await Task.Delay (wait < TimeSpan.FromSeconds (30) ? wait : TimeSpan.FromSeconds (30), ct).ConfigureAwait (false);
                    continue;
                }
                return response;
            }
        }

        private static async Task EnsureSuccessAsync (HttpResponseMessage response, string what, CancellationToken ct)
        {
            if (response.IsSuccessStatusCode)
                return;
            var code = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
            string message;
            try {
                message = JsonNode.Parse (text)?["error"]?["message"]?.GetValue<string> () ?? text;
            } catch (JsonException) {
                message = text;
            }
            if (code is 401 or 403)
                throw new StorageAuthenticationException ($"OneDrive refused the sign-in ({code}): {message}");
            if (code == 429 || code >= 500)
                throw new StorageUnavailableException ($"OneDrive is unavailable ({code}).");
            throw new IOException ($"OneDrive refused '{what}' ({code}): {message}");
        }
    }
}
