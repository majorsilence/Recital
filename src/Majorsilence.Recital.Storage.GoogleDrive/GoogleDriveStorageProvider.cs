using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Majorsilence.Recital.Storage.OAuth;

namespace Majorsilence.Recital.Storage.GoogleDrive
{
    /// <summary>
    /// The user's Google Drive (API v3), with the <c>drive.file</c> scope: Recital sees only files it created
    /// (plan 2.5), in a visible <c>Recital</c> folder in My Drive. Logical paths are relative to My Drive.
    /// </summary>
    /// <remarks>
    /// <para>Drive addresses files by id and allows several files with the same name in a folder, which two
    /// devices creating <c>Recital/</c> at once would produce. Paths therefore resolve to every match: a
    /// folder's listing merges the children of all same-named folders, a duplicated file reads as its oldest
    /// copy (the first writer, matching write-once semantics), and writes go to the oldest folder.</para>
    /// <para>ETags are Drive's <c>version</c>. Drive v3 has no conditional write, so <c>ifMatchEtag</c> is
    /// checked just before writing. The change feed is <c>changes.list</c>, whose items carry parent ids:
    /// paths are rebuilt from an id map, asking Drive for unknown parents.</para>
    /// </remarks>
    public sealed class GoogleDriveStorageProvider : IStorageProvider, IStorageAccountProvider
    {
        public static readonly Uri ApiBase = new ("https://www.googleapis.com/drive/v3/");
        public static readonly Uri UploadBase = new ("https://www.googleapis.com/upload/drive/v3/");
        private const string FolderType = "application/vnd.google-apps.folder";
        private const string FileFields = "id,name,mimeType,size,modifiedTime,createdTime,version,parents,trashed";
        private const int SimpleUploadLimit = 5 * 1024 * 1024;

        /// <summary>
        /// The OAuth registration for a Google Cloud "Desktop app" client. Google requires its client secret
        /// at the token endpoint even with PKCE, and documents it as not secret for installed apps.
        /// </summary>
        public static OAuthConfig OAuth (string clientId, string? clientSecret) => new ("Google Drive",
            new Uri ("https://accounts.google.com/o/oauth2/v2/auth"), new Uri ("https://oauth2.googleapis.com/token"),
            clientId, "https://www.googleapis.com/auth/drive.file", clientSecret,
            new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" });

        private readonly HttpClient http;
        private readonly Uri api;
        private readonly Uri upload;
        private readonly object gate = new ();
        private readonly Dictionary<string, string> paths_by_id = new (StringComparer.Ordinal);
        private readonly Dictionary<string, (List<string> Ids, DateTimeOffset At)> folder_ids = new (StringComparer.Ordinal);
        // Another device may add a same-named folder at any time: folder ids are looked up again after this.
        private static readonly TimeSpan FolderCacheLifetime = TimeSpan.FromMinutes (5);
        private string? root_id;

        /// <param name="http">Authenticated (an <see cref="OAuthHandler"/>).</param>
        /// <param name="feedFolder">The folder (logical) the change feed covers.</param>
        public GoogleDriveStorageProvider (HttpClient http, string feedFolder = "", Uri? apiBase = null, Uri? uploadBase = null)
        {
            this.http = http;
            FeedFolder = StoragePath.Normalize (feedFolder);
            api = apiBase ?? ApiBase;
            upload = uploadBase ?? UploadBase;
        }

        public string FeedFolder { get; }

        public string Id => "gdrive";

        public StorageCapabilities Capabilities => StorageCapabilities.ChangeFeed | StorageCapabilities.ETags | StorageCapabilities.LargeUpload;

        public async Task<StorageAccount> GetAccountAsync (CancellationToken ct = default)
        {
            var about = await GetJsonAsync (Api ("about?fields=user(permissionId,displayName,emailAddress)"), ct).ConfigureAwait (false);
            var user = about!["user"]!;
            var id = user["permissionId"]!.GetValue<string> ();
            return new StorageAccount (id, user["emailAddress"]?.GetValue<string> () ?? user["displayName"]?.GetValue<string> () ?? id);
        }

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            var ids = await FolderIdsAsync (folder, create: false, ct).ConfigureAwait (false);
            if (ids.Count == 0)
                return [];
            var children = await ChildrenAsync (ids, null, ct).ConfigureAwait (false);
            var entries = new List<StorageEntry> ();
            foreach (var group in children.GroupBy (c => c["name"]!.GetValue<string> (), StringComparer.Ordinal)) {
                if (TryCombine (folder, group.Key) is not { } path)
                    continue;
                var canonical = Oldest (group);
                foreach (var item in group)
                    Remember (item, path);
                if (Entry (canonical, path) is { } entry)
                    entries.Add (entry);
            }
            return entries.OrderBy (e => e.Path, StringComparer.Ordinal).ToList ();
        }

        public async Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            var file = await FileAsync (path, ct).ConfigureAwait (false) ?? throw new FileNotFoundException ($"No file '{path}'.", path);
            var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Get, Api ($"files/{file["id"]!.GetValue<string> ()}?alt=media")), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait (false);
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

            var existing = await FileAsync (path, ct).ConfigureAwait (false);
            if (existing?["mimeType"]?.GetValue<string> () == FolderType)
                throw new IOException ($"'{path}' clashes with an existing folder.");
            if (ifMatchEtag is not null && existing?["version"]?.GetValue<string> () != ifMatchEtag)
                throw new StoragePreconditionFailedException (path);

            JsonNode? file;
            if (existing is not null) {
                file = await UploadAsync (HttpMethod.Patch, $"files/{existing["id"]!.GetValue<string> ()}", null, bytes, ct).ConfigureAwait (false);
            } else {
                var parents = await FolderIdsAsync (StoragePath.Parent (path), create: true, ct).ConfigureAwait (false);
                var metadata = new JsonObject { ["name"] = StoragePath.Name (path), ["parents"] = new JsonArray (parents[0]) };
                file = await UploadAsync (HttpMethod.Post, "files", metadata, bytes, ct).ConfigureAwait (false);
            }
            Remember (file!, path);
            return Entry (file, path) ?? throw new IOException ($"Google Drive gave no metadata for '{path}'.");
        }

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0)
                return new StorageEntry ("", true, 0, DateTimeOffset.MinValue, null);
            var parents = await FolderIdsAsync (StoragePath.Parent (path), create: false, ct).ConfigureAwait (false);
            if (parents.Count == 0)
                return null;
            var matches = await ChildrenAsync (parents, StoragePath.Name (path), ct).ConfigureAwait (false);
            if (matches.Count == 0)
                return null;
            foreach (var item in matches)
                Remember (item, path);
            return Entry (Oldest (matches), path);
        }

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            if (cursor is null) {
                var start = await GetJsonAsync (Api ("changes/startPageToken"), ct).ConfigureAwait (false);
                var token = start!["startPageToken"]!.GetValue<string> ();
                var all = new List<StorageEntry> ();
                var folders = new Stack<string> ([FeedFolder]);
                while (folders.TryPop (out var folder))
                    foreach (var entry in await ListAsync (folder, ct).ConfigureAwait (false))
                        if (entry.IsFolder)
                            folders.Push (entry.Path);
                        else
                            all.Add (entry);
                return new ChangeSet (all, token, true);
            }

            var changed = new Dictionary<string, StorageEntry> (StringComparer.Ordinal);
            var pageToken = cursor;
            while (true) {
                var page = await GetJsonAsync (Api ($"changes?pageToken={Uri.EscapeDataString (pageToken)}&spaces=drive&pageSize=1000&fields=nextPageToken,newStartPageToken,changes(fileId,removed,file({FileFields}))"), ct).ConfigureAwait (false);
                foreach (var change in page!["changes"]?.AsArray () ?? []) {
                    var file = change?["file"];
                    if (change?["removed"]?.GetValue<bool> () == true || file is null || file["trashed"]?.GetValue<bool> () == true || file["mimeType"]?.GetValue<string> () == FolderType)
                        continue;
                    var path = await PathOfAsync (file, ct).ConfigureAwait (false);
                    if (path is null || !StoragePath.IsWithin (path, FeedFolder) || changed.ContainsKey (path))
                        continue;
                    // A duplicate's change reads as the path's canonical (oldest) copy.
                    if (await StatAsync (path, ct).ConfigureAwait (false) is { IsFolder: false } entry)
                        changed[path] = entry;
                }
                if (page["nextPageToken"]?.GetValue<string> () is { } next) {
                    pageToken = next;
                    continue;
                }
                return new ChangeSet (changed.Values.ToList (), page["newStartPageToken"]?.GetValue<string> () ?? cursor, false);
            }
        }

        /// <summary>The ids of every folder at <paramref name="folder"/> (same-named duplicates included), oldest first; creating it when asked.</summary>
        private async Task<List<string>> FolderIdsAsync (string folder, bool create, CancellationToken ct)
        {
            if (folder.Length == 0)
                return [await RootIdAsync (ct).ConfigureAwait (false)];
            lock (gate) {
                if (folder_ids.TryGetValue (folder, out var cached) && DateTimeOffset.UtcNow - cached.At < FolderCacheLifetime)
                    return cached.Ids;
            }
            var parents = await FolderIdsAsync (StoragePath.Parent (folder), create, ct).ConfigureAwait (false);
            if (parents.Count == 0)
                return [];
            var matches = (await ChildrenAsync (parents, StoragePath.Name (folder), ct).ConfigureAwait (false))
                .Where (i => i["mimeType"]?.GetValue<string> () == FolderType).ToList ();
            if (matches.Count == 0) {
                if (!create)
                    return [];
                var made = await PostJsonAsync (Api ($"files?fields={FileFields}"), new JsonObject {
                    ["name"] = StoragePath.Name (folder), ["mimeType"] = FolderType, ["parents"] = new JsonArray (parents[0]),
                }, ct).ConfigureAwait (false);
                matches = [made!];
            }
            var ids = matches.OrderBy (CreatedTime).ThenBy (i => i["id"]!.GetValue<string> (), StringComparer.Ordinal)
                .Select (i => i["id"]!.GetValue<string> ()).ToList ();
            lock (gate) {
                folder_ids[folder] = (ids, DateTimeOffset.UtcNow);
                foreach (var id in ids)
                    paths_by_id[id] = folder;
            }
            return ids;
        }

        private async Task<JsonNode?> FileAsync (string path, CancellationToken ct)
        {
            var parents = await FolderIdsAsync (StoragePath.Parent (path), create: false, ct).ConfigureAwait (false);
            if (parents.Count == 0)
                return null;
            var matches = await ChildrenAsync (parents, StoragePath.Name (path), ct).ConfigureAwait (false);
            return matches.Count == 0 ? null : Oldest (matches);
        }

        /// <summary>Children of any of <paramref name="parents"/>, optionally only those named <paramref name="name"/>.</summary>
        private async Task<List<JsonNode>> ChildrenAsync (IReadOnlyList<string> parents, string? name, CancellationToken ct)
        {
            var q = "trashed = false and (" + string.Join (" or ", parents.Select (id => $"'{Escape (id)}' in parents")) + ")"
                + (name is null ? "" : $" and name = '{Escape (name)}'");
            var result = new List<JsonNode> ();
            string? pageToken = null;
            do {
                var url = $"files?q={Uri.EscapeDataString (q)}&pageSize=1000&spaces=drive&fields=nextPageToken,files({FileFields})"
                    + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString (pageToken));
                var page = await GetJsonAsync (Api (url), ct).ConfigureAwait (false);
                foreach (var file in page!["files"]?.AsArray () ?? [])
                    if (file is not null && (name is null || file["name"]?.GetValue<string> () == name))
                        result.Add (file);
                pageToken = page["nextPageToken"]?.GetValue<string> ();
            } while (pageToken is not null);
            return result;
        }

        private async Task<string> RootIdAsync (CancellationToken ct)
        {
            if (root_id is not null)
                return root_id;
            var root = await GetJsonAsync (Api ("files/root?fields=id"), ct).ConfigureAwait (false);
            root_id = root!["id"]!.GetValue<string> ();
            lock (gate)
                paths_by_id[root_id] = "";
            return root_id;
        }

        private async Task<string?> PathOfAsync (JsonNode file, CancellationToken ct)
        {
            var name = file["name"]?.GetValue<string> ();
            var parent = file["parents"]?.AsArray ().FirstOrDefault ()?.GetValue<string> ();
            if (name is null || parent is null)
                return null;
            var folder = await PathOfIdAsync (parent, 0, ct).ConfigureAwait (false);
            return folder is null ? null : TryCombine (folder, name);
        }

        private async Task<string?> PathOfIdAsync (string id, int depth, CancellationToken ct)
        {
            await RootIdAsync (ct).ConfigureAwait (false);
            lock (gate) {
                if (paths_by_id.TryGetValue (id, out var known))
                    return known;
            }
            if (depth > 64)
                return null;
            var item = await GetJsonAsync (Api ($"files/{Uri.EscapeDataString (id)}?fields=id,name,mimeType,parents"), ct, notFoundIsNull: true).ConfigureAwait (false);
            if (item?["name"]?.GetValue<string> () is not { } name || item["parents"]?.AsArray ().FirstOrDefault ()?.GetValue<string> () is not { } parentId)
                return null;
            var parent = await PathOfIdAsync (parentId, depth + 1, ct).ConfigureAwait (false);
            var path = parent is null ? null : TryCombine (parent, name);
            if (path is not null) {
                lock (gate) {
                    paths_by_id[id] = path;
                    // A folder this provider did not know at a path it did: a same-named duplicate from another
                    // device. Look the path's folders up again, so its contents are not missed.
                    if (item["mimeType"]?.GetValue<string> () == FolderType && folder_ids.TryGetValue (path, out var cached) && !cached.Ids.Contains (id))
                        folder_ids.Remove (path);
                }
            }
            return path;
        }

        private void Remember (JsonNode item, string path)
        {
            if (item["id"]?.GetValue<string> () is { } id)
                lock (gate)
                    paths_by_id[id] = path;
        }

        private static JsonNode Oldest (IEnumerable<JsonNode> items)
            => items.OrderBy (CreatedTime).ThenBy (i => i["id"]!.GetValue<string> (), StringComparer.Ordinal).First ();

        private static DateTimeOffset CreatedTime (JsonNode item)
            => DateTimeOffset.TryParse (item["createdTime"]?.GetValue<string> (), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.MaxValue;

        private static StorageEntry? Entry (JsonNode? item, string path)
        {
            if (item is null)
                return null;
            var modified = DateTimeOffset.TryParse (item["modifiedTime"]?.GetValue<string> (), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : DateTimeOffset.MinValue;
            if (item["mimeType"]?.GetValue<string> () == FolderType)
                return new StorageEntry (path, true, 0, modified, null);
            var size = long.TryParse (item["size"]?.GetValue<string> (), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            return new StorageEntry (path, false, size, modified, item["version"]?.GetValue<string> ());
        }

        private static string? TryCombine (string folder, string name)
        {
            try {
                return StoragePath.Combine (folder, name);
            } catch (ArgumentException) {
                return null;
            }
        }

        /// <summary>A string literal in a Drive query: backslash and quote escaped.</summary>
        private static string Escape (string text) => text.Replace ("\\", "\\\\", StringComparison.Ordinal).Replace ("'", "\\'", StringComparison.Ordinal);

        private Uri Api (string relative) => new (api.AbsoluteUri + relative);

        /// <summary>Uploads content: multipart (metadata and media) up to 5 MB, a resumable session above.</summary>
        private async Task<JsonNode?> UploadAsync (HttpMethod method, string resource, JsonObject? metadata, byte[] bytes, CancellationToken ct)
        {
            var fields = $"fields={FileFields}";
            if (bytes.Length <= SimpleUploadLimit) {
                using var response = await SendAsync (() => {
                    HttpContent content;
                    if (metadata is null) {
                        content = new ByteArrayContent (bytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue ("application/octet-stream");
                    } else {
                        var multipart = new MultipartContent ("related");
                        multipart.Add (new StringContent (metadata.ToJsonString (), Encoding.UTF8, "application/json"));
                        var media = new ByteArrayContent (bytes);
                        media.Headers.ContentType = new MediaTypeHeaderValue ("application/octet-stream");
                        multipart.Add (media);
                        content = multipart;
                    }
                    var type = metadata is null ? "media" : "multipart";
                    return new HttpRequestMessage (method, new Uri (upload.AbsoluteUri + $"{resource}?uploadType={type}&{fields}")) { Content = content };
                }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
                await EnsureSuccessAsync (response, resource, ct).ConfigureAwait (false);
                return JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
            }

            var json = (metadata ?? new JsonObject ()).ToJsonString ();
            using var start = await SendAsync (() => new HttpRequestMessage (method, new Uri (upload.AbsoluteUri + $"{resource}?uploadType=resumable&{fields}")) {
                Content = new StringContent (json, Encoding.UTF8, "application/json"),
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            await EnsureSuccessAsync (start, resource, ct).ConfigureAwait (false);
            var session = start.Headers.Location ?? throw new IOException ("Google Drive gave no upload session.");
            using var put = await SendAsync (() => new HttpRequestMessage (HttpMethod.Put, session) { Content = new ByteArrayContent (bytes) }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            await EnsureSuccessAsync (put, resource, ct).ConfigureAwait (false);
            return JsonNode.Parse (await put.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
        }

        private async Task<JsonNode?> GetJsonAsync (Uri uri, CancellationToken ct, bool notFoundIsNull = false)
        {
            using var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Get, uri), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            if (notFoundIsNull && response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync (response, uri.AbsolutePath, ct).ConfigureAwait (false);
            return JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
        }

        private async Task<JsonNode?> PostJsonAsync (Uri uri, JsonObject body, CancellationToken ct)
        {
            var json = body.ToJsonString ();
            using var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Post, uri) { Content = new StringContent (json, Encoding.UTF8, "application/json") }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            await EnsureSuccessAsync (response, uri.AbsolutePath, ct).ConfigureAwait (false);
            return JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
        }

        /// <summary>Sends, waiting out one 429 or 5xx (Drive's rate-limit 403s included), and once more after a dropped connection.</summary>
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
                    throw new StorageUnavailableException ($"Could not reach Google Drive: {ex.Message}", ex);
                } catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
                    throw new StorageUnavailableException ("Google Drive did not answer in time.", ex);
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
            string? reason = null;
            try {
                var error = JsonNode.Parse (text)?["error"];
                message = error?["message"]?.GetValue<string> () ?? text;
                reason = error?["errors"]?.AsArray ().FirstOrDefault ()?["reason"]?.GetValue<string> ();
            } catch (JsonException) {
                message = text;
            }
            // Drive reports rate limits as 403 with a reason.
            if (code == 403 && reason is "rateLimitExceeded" or "userRateLimitExceeded")
                throw new StorageUnavailableException ($"Google Drive is busy: {message}");
            if (code is 401 or 403)
                throw new StorageAuthenticationException ($"Google Drive refused the sign-in ({code}): {message}");
            if (code == 429 || code >= 500)
                throw new StorageUnavailableException ($"Google Drive is unavailable ({code}).");
            throw new IOException ($"Google Drive refused '{what}' ({code}): {message}");
        }
    }
}
