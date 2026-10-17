using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// An in-process Dropbox API v2 (the endpoints the provider uses), answering as Dropbox does: errors as
    /// 409 with an <c>error_summary</c>, case-insensitive paths that keep their first spelling, parent folders
    /// made by upload, listings in pages (three entries, to exercise <c>has_more</c>), and a change feed from
    /// a cursor.
    /// </summary>
    internal sealed class FakeDropboxServer : HttpMessageHandler
    {
        private const int PageSize = 3;
        private readonly object gate = new ();
        // Keyed by lower-case path; the display path keeps the first spelling.
        private readonly Dictionary<string, Item> items = new (StringComparer.Ordinal);
        private long sequence;

        private sealed record Item (string Display, bool Folder, byte[] Data, string Rev, DateTimeOffset Modified, long Sequence);

        public HttpStatusCode? FailWith { get; set; }

        /// <summary>The text of every file (for checking nothing secret was uploaded).</summary>
        public IReadOnlyList<string> AllFileText ()
        {
            lock (gate)
                return items.Values.Where (i => !i.Folder).Select (i => Encoding.UTF8.GetString (i.Data)).ToList ();
        }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailWith is { } failure)
                return new HttpResponseMessage (failure) { Content = new StringContent ("{}") };
            var endpoint = request.RequestUri!.AbsolutePath.TrimStart ('/')[2..].TrimStart ('/'); // drop "2/"
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync (cancellationToken);
            var arg = request.Headers.TryGetValues ("Dropbox-API-Arg", out var values) ? JsonNode.Parse (values.First ()) : null;
            if (arg is null && body.Length > 0 && request.RequestUri.Host.StartsWith ("api.", StringComparison.Ordinal))
                arg = JsonNode.Parse (body);
            if (values?.First ().Any (c => c > 127) == true)
                return Error (HttpStatusCode.BadRequest, "Dropbox-API-Arg must be ASCII");

            lock (gate) {
                return endpoint switch {
                    "files/list_folder" => ListFolder (arg!["path"]!.GetValue<string> (), arg["recursive"]?.GetValue<bool> () == true),
                    "files/list_folder/continue" => Continue (arg!["cursor"]!.GetValue<string> ()),
                    "files/get_metadata" => Metadata (arg!["path"]!.GetValue<string> ()),
                    "files/download" => Download (arg!["path"]!.GetValue<string> ()),
                    "files/upload" => Upload (arg!, body),
                    "users/get_current_account" => Ok (new JsonObject { ["account_id"] = "dbid:AAA", ["email"] = "reader@example.com", ["name"] = new JsonObject { ["display_name"] = "A Reader" } }),
                    _ => Error (HttpStatusCode.BadRequest, "unknown endpoint " + endpoint),
                };
            }
        }

        private HttpResponseMessage ListFolder (string path, bool recursive)
        {
            if (path.Length > 0 && !(items.TryGetValue (path.ToLowerInvariant (), out var folder) && folder.Folder))
                return Conflict ("path/not_found/..");
            return Page (path, recursive, since: -1, offset: 0, snapshot: sequence);
        }

        /// <summary>A cursor: the folder, recursion, the change sequence it covers up to, and how far into the listing it is.</summary>
        private HttpResponseMessage Continue (string cursor)
        {
            var parts = Encoding.UTF8.GetString (Convert.FromBase64String (cursor)).Split ('|');
            var path = parts[0];
            var recursive = parts[1] == "1";
            var since = long.Parse (parts[2], CultureInfo.InvariantCulture);
            var offset = int.Parse (parts[3], CultureInfo.InvariantCulture);
            var snapshot = long.Parse (parts[4], CultureInfo.InvariantCulture);
            return Page (path, recursive, since, offset, snapshot);
        }

        private HttpResponseMessage Page (string path, bool recursive, long since, int offset, long snapshot)
        {
            var prefix = path.Length == 0 ? "/" : path.ToLowerInvariant () + "/";
            var matching = items.Where (kv => kv.Key.StartsWith (prefix, StringComparison.Ordinal)
                    && (recursive || !kv.Key[prefix.Length..].Contains ('/'))
                    && (since < 0 ? kv.Value.Sequence <= snapshot : kv.Value.Sequence > since && kv.Value.Sequence <= snapshot))
                .OrderBy (kv => kv.Value.Sequence).ThenBy (kv => kv.Key, StringComparer.Ordinal)
                .Select (kv => kv.Value).ToList ();
            var page = matching.Skip (offset).Take (PageSize).ToList ();
            var more = offset + page.Count < matching.Count;
            var cursor = more
                ? Cursor (path, recursive, since, offset + page.Count, snapshot)
                : Cursor (path, recursive, Math.Min (snapshot, sequence), 0, long.MaxValue);
            var result = new JsonObject {
                ["entries"] = new JsonArray (page.Select (Json).ToArray<JsonNode?> ()),
                ["cursor"] = cursor,
                ["has_more"] = more,
            };
            return Ok (result);
        }

        private static string Cursor (string path, bool recursive, long since, int offset, long snapshot)
            => Convert.ToBase64String (Encoding.UTF8.GetBytes (string.Join ('|', path, recursive ? "1" : "0", since.ToString (CultureInfo.InvariantCulture), offset.ToString (CultureInfo.InvariantCulture), snapshot.ToString (CultureInfo.InvariantCulture))));

        private HttpResponseMessage Metadata (string path)
            => items.TryGetValue (path.ToLowerInvariant (), out var item) ? Ok (Json (item)) : Conflict ("path/not_found/");

        private HttpResponseMessage Download (string path)
        {
            if (!items.TryGetValue (path.ToLowerInvariant (), out var item) || item.Folder)
                return Conflict ("path/not_found/");
            var response = new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (item.Data) };
            response.Headers.TryAddWithoutValidation ("Dropbox-API-Result", Json (item).ToJsonString ());
            return response;
        }

        private HttpResponseMessage Upload (JsonNode arg, byte[] data)
        {
            var path = arg["path"]!.GetValue<string> ();
            var key = path.ToLowerInvariant ();
            var mode = arg["mode"];
            items.TryGetValue (key, out var existing);
            if (existing?.Folder == true)
                return Conflict ("path/conflict/folder/");
            if (mode is JsonObject update && update[".tag"]?.GetValue<string> () == "update" && existing?.Rev != update["update"]?.GetValue<string> ())
                return Conflict ("path/conflict/file/");

            // Parent folders, made as needed and spelled as given.
            var segments = path.TrimStart ('/').Split ('/');
            for (var i = 1; i < segments.Length; i++) {
                var folder = "/" + string.Join ('/', segments[..i]);
                if (!items.TryGetValue (folder.ToLowerInvariant (), out var f))
                    items[folder.ToLowerInvariant ()] = new Item (folder, true, [], "", DateTimeOffset.UtcNow, ++sequence);
                else if (!f.Folder)
                    return Conflict ("path/conflict/file_ancestor/");
            }
            var display = existing is null ? path : existing.Display;
            var item = new Item (display, false, data, (++sequence).ToString ("x9", CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, sequence);
            items[key] = item;
            return Ok (Json (item));
        }

        private static JsonObject Json (Item item)
        {
            var name = item.Display[(item.Display.LastIndexOf ('/') + 1)..];
            var json = new JsonObject {
                [".tag"] = item.Folder ? "folder" : "file",
                ["name"] = name,
                ["path_lower"] = item.Display.ToLowerInvariant (),
                ["path_display"] = item.Display,
                ["id"] = "id:" + item.Display.GetHashCode (StringComparison.OrdinalIgnoreCase).ToString ("x", CultureInfo.InvariantCulture),
            };
            if (!item.Folder) {
                json["rev"] = item.Rev;
                json["size"] = item.Data.Length;
                json["server_modified"] = item.Modified.ToString ("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }
            return json;
        }

        private static HttpResponseMessage Ok (JsonNode json) => new (HttpStatusCode.OK) { Content = new StringContent (json.ToJsonString (), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Conflict (string summary)
            => new (HttpStatusCode.Conflict) { Content = new StringContent (new JsonObject { ["error_summary"] = summary }.ToJsonString (), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error (HttpStatusCode code, string text) => new (code) { Content = new StringContent (text) };
    }
}
