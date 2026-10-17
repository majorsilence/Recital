using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Majorsilence.Recital.Storage.OAuth;

namespace Majorsilence.Recital.Storage.Dropbox
{
    /// <summary>
    /// The user's Dropbox (API v2). Logical paths are relative to <see cref="RootFolder"/> (the Dropbox root by
    /// default, so the sync home is the visible <c>/Recital</c> folder and import can browse everything).
    /// ETags are file revisions (<c>rev</c>); conditional writes use upload's <c>update</c> mode; the change
    /// feed is <c>list_folder/continue</c> on a recursive cursor.
    /// </summary>
    /// <remarks>
    /// Dropbox paths are case-insensitive. Entries carry the case the file was created with
    /// (<c>path_display</c>), which is what Recital wrote. The <see cref="HttpClient"/> must authenticate (an
    /// <see cref="OAuthHandler"/>).
    /// </remarks>
    public sealed class DropboxStorageProvider : IStorageProvider, IStorageAccountProvider
    {
        public static readonly Uri ApiBase = new ("https://api.dropboxapi.com/2/");
        public static readonly Uri ContentBase = new ("https://content.dropboxapi.com/2/");

        /// <summary>The OAuth registration for a Dropbox app key (PKCE, offline access for a refresh token).</summary>
        public static OAuthConfig OAuth (string appKey) => new ("Dropbox",
            new Uri ("https://www.dropbox.com/oauth2/authorize"), new Uri ("https://api.dropboxapi.com/oauth2/token"),
            appKey, "files.metadata.read files.content.read files.content.write account_info.read",
            ExtraAuthorizeParameters: new Dictionary<string, string> { ["token_access_type"] = "offline" });

        private readonly HttpClient http;

        /// <param name="rootFolder">The Dropbox folder logical paths are relative to ("" is the Dropbox root).</param>
        /// <param name="feedFolder">The folder (logical) the change feed covers: the sync home's <c>Recital</c>, so the first
        /// sync does not walk the user's whole Dropbox.</param>
        public DropboxStorageProvider (HttpClient http, string rootFolder = "", string feedFolder = "")
        {
            this.http = http;
            RootFolder = StoragePath.Normalize (rootFolder);
            FeedFolder = StoragePath.Normalize (feedFolder);
        }

        public string RootFolder { get; }

        public string FeedFolder { get; }

        public string Id => "dropbox";

        public StorageCapabilities Capabilities => StorageCapabilities.ChangeFeed | StorageCapabilities.ETags | StorageCapabilities.Browse;

        public async Task<StorageAccount> GetAccountAsync (CancellationToken ct = default)
        {
            using var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Post, new Uri (ApiBase, "users/get_current_account")), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            await EnsureSuccessAsync (response, "users/get_current_account", ct).ConfigureAwait (false);
            var account = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false))!;
            var id = account["account_id"]!.GetValue<string> ();
            return new StorageAccount (id, account["email"]?.GetValue<string> () ?? account["name"]?["display_name"]?.GetValue<string> () ?? id);
        }

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            var folder = StoragePath.Normalize (folderPath);
            var entries = new List<StorageEntry> ();
            JsonNode? page = await RpcAsync ("files/list_folder", new JsonObject { ["path"] = Remote (folder), ["recursive"] = false }, ct, notFoundIsNull: true).ConfigureAwait (false);
            while (page is not null) {
                foreach (var item in page["entries"]?.AsArray () ?? [])
                    if (Entry (item) is { } entry)
                        entries.Add (entry);
                if (page["has_more"]?.GetValue<bool> () != true)
                    break;
                page = await RpcAsync ("files/list_folder/continue", new JsonObject { ["cursor"] = page["cursor"]!.GetValue<string> () }, ct).ConfigureAwait (false);
            }
            return entries.OrderBy (e => e.Path, StringComparer.Ordinal).ToList ();
        }

        public async Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            var response = await SendAsync (() => {
                var request = new HttpRequestMessage (HttpMethod.Post, new Uri (ContentBase, "files/download"));
                request.Headers.TryAddWithoutValidation ("Dropbox-API-Arg", Arg (new JsonObject { ["path"] = Remote (path) }));
                return request;
            }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait (false);
            if (await IsNotFoundAsync (response, ct).ConfigureAwait (false)) {
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

            JsonNode mode = ifMatchEtag is null ? JsonValue.Create ("overwrite") : new JsonObject { [".tag"] = "update", ["update"] = ifMatchEtag };
            using var response = await SendAsync (() => {
                var request = new HttpRequestMessage (HttpMethod.Post, new Uri (ContentBase, "files/upload")) { Content = new ByteArrayContent (bytes) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue ("application/octet-stream");
                request.Headers.TryAddWithoutValidation ("Dropbox-API-Arg", Arg (new JsonObject {
                    ["path"] = Remote (path), ["mode"] = mode.DeepClone (), ["mute"] = true, ["strict_conflict"] = true,
                }));
                return request;
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);

            if (response.StatusCode == HttpStatusCode.Conflict) {
                var error = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
                if (ifMatchEtag is not null && error.Contains ("conflict", StringComparison.Ordinal))
                    throw new StoragePreconditionFailedException (path);
                throw new IOException ($"Dropbox refused '{path}': {Summary (error)}");
            }
            await EnsureSuccessAsync (response, path, ct).ConfigureAwait (false);
            var metadata = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
            return Entry (metadata) ?? throw new IOException ($"Dropbox gave no metadata for '{path}'.");
        }

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            path = StoragePath.Normalize (path);
            if (path.Length == 0 && RootFolder.Length == 0)
                return new StorageEntry ("", true, 0, DateTimeOffset.MinValue, null);
            var metadata = await RpcAsync ("files/get_metadata", new JsonObject { ["path"] = Remote (path) }, ct, notFoundIsNull: true).ConfigureAwait (false);
            return metadata is null ? null : Entry (metadata);
        }

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            var changed = new List<StorageEntry> ();
            JsonNode? page;
            if (cursor is null) {
                page = await RpcAsync ("files/list_folder", new JsonObject { ["path"] = Remote (FeedFolder), ["recursive"] = true }, ct, notFoundIsNull: true).ConfigureAwait (false);
                if (page is null) {
                    // The folder does not exist yet. A cursor needs an existing folder, so start with no cursor and
                    // list again next time; there is nothing to miss until the folder is created.
                    return new ChangeSet ([], "", true);
                }
            } else if (cursor.Length == 0) {
                return await GetChangesAsync (null, ct).ConfigureAwait (false);
            } else {
                page = await RpcAsync ("files/list_folder/continue", new JsonObject { ["cursor"] = cursor }, ct, resetIsNull: true).ConfigureAwait (false);
                if (page is null)
                    return await GetChangesAsync (null, ct).ConfigureAwait (false);
            }

            while (true) {
                foreach (var item in page!["entries"]?.AsArray () ?? [])
                    if (Entry (item) is { IsFolder: false } entry)
                        changed.Add (entry);
                var next = page["cursor"]!.GetValue<string> ();
                if (page["has_more"]?.GetValue<bool> () != true)
                    return new ChangeSet (changed, next, cursor is null or "");
                page = await RpcAsync ("files/list_folder/continue", new JsonObject { ["cursor"] = next }, ct).ConfigureAwait (false);
            }
        }

        /// <summary>The Dropbox path of a logical path ("" is the Dropbox root).</summary>
        private string Remote (string logical)
        {
            var full = StoragePath.Combine (RootFolder, logical);
            return full.Length == 0 ? "" : "/" + full;
        }

        /// <summary>A metadata object as an entry, or null for a deletion or something outside the root.</summary>
        private StorageEntry? Entry (JsonNode? item)
        {
            var tag = item?[".tag"]?.GetValue<string> ();
            var display = item?["path_display"]?.GetValue<string> ();
            if (tag is not ("file" or "folder") || display is null)
                return null;
            var full = display.TrimStart ('/');
            string logical;
            try {
                full = StoragePath.Normalize (full);
                if (RootFolder.Length > 0) {
                    if (!full.StartsWith (RootFolder + "/", StringComparison.OrdinalIgnoreCase))
                        return null;
                    full = full[(RootFolder.Length + 1)..];
                }
                logical = full;
            } catch (ArgumentException) {
                return null;
            }
            if (tag == "folder")
                return new StorageEntry (logical, true, 0, DateTimeOffset.MinValue, null);
            var modified = DateTimeOffset.TryParse (item!["server_modified"]?.GetValue<string> (), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : DateTimeOffset.MinValue;
            return new StorageEntry (logical, false, item["size"]?.GetValue<long> () ?? 0, modified, item["rev"]?.GetValue<string> ());
        }

        // Dropbox-API-Arg must be ASCII: the default encoder escapes everything else as \\uXXXX.
        private static string Arg (JsonObject arg) => arg.ToJsonString ();

        private async Task<JsonNode?> RpcAsync (string endpoint, JsonObject body, CancellationToken ct, bool notFoundIsNull = false, bool resetIsNull = false)
        {
            var json = body.ToJsonString ();
            using var response = await SendAsync (() => new HttpRequestMessage (HttpMethod.Post, new Uri (ApiBase, endpoint)) {
                Content = new StringContent (json, Encoding.UTF8, "application/json"),
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait (false);
            var text = await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false);
            if (response.StatusCode == HttpStatusCode.Conflict) {
                if (notFoundIsNull && text.Contains ("not_found", StringComparison.Ordinal))
                    return null;
                if (resetIsNull && text.Contains ("reset", StringComparison.Ordinal))
                    return null;
                throw new IOException ($"Dropbox refused {endpoint}: {Summary (text)}");
            }
            await EnsureSuccessAsync (response, endpoint, ct).ConfigureAwait (false);
            return JsonNode.Parse (text);
        }

        private static async Task<bool> IsNotFoundAsync (HttpResponseMessage response, CancellationToken ct)
            => response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false)).Contains ("not_found", StringComparison.Ordinal);

        /// <summary>Sends, waiting out one 429 (Retry-After) or 5xx, and once more after a dropped connection.</summary>
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
                    throw new StorageUnavailableException ($"Could not reach Dropbox: {ex.Message}", ex);
                } catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) {
                    throw new StorageUnavailableException ("Dropbox did not answer in time.", ex);
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
            if (code is 401 or 403)
                throw new StorageAuthenticationException ($"Dropbox refused the sign-in ({code}): {Summary (text)}");
            if (code == 429 || code >= 500)
                throw new StorageUnavailableException ($"Dropbox is unavailable ({code}).");
            throw new IOException ($"Dropbox refused '{what}' ({code}): {Summary (text)}");
        }

        private static string Summary (string text)
        {
            try {
                return JsonNode.Parse (text)?["error_summary"]?.GetValue<string> () ?? text;
            } catch (JsonException) {
                return text.Length > 200 ? text[..200] : text;
            }
        }
    }
}
