using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// An in-process Microsoft Graph drive (the calls the OneDrive provider makes): path-addressed items,
    /// children in pages of three, uploads that create parent folders, If-Match, upload sessions on a
    /// separate pre-authenticated host (which refuses a bearer token), and a delta feed whose items carry
    /// only their parent's id, as personal OneDrive's do.
    /// </summary>
    internal sealed class FakeGraphServer : HttpMessageHandler
    {
        private const int PageSize = 3;
        private const string Drive = "/v1.0/me/drive/";
        private readonly object gate = new ();
        private readonly Dictionary<string, Item> items = new (StringComparer.OrdinalIgnoreCase); // by path; "" is the root
        private readonly Dictionary<string, (string Path, string? IfMatch, MemoryStream Data)> sessions = [];
        private long sequence;

        private sealed record Item (string Id, string Path, bool Folder, byte[] Data, string ETag, DateTimeOffset Modified, long Sequence);

        public FakeGraphServer ()
        {
            items[""] = new Item ("root-id", "", true, [], "\"root\"", DateTimeOffset.UtcNow, 0);
        }

        public HttpStatusCode? FailWith { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailWith is { } failure)
                return new HttpResponseMessage (failure) { Content = new StringContent ("{}") };
            var uri = request.RequestUri!;
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync (cancellationToken);
            var ifMatch = request.Headers.TryGetValues ("If-Match", out var m) ? m.First () : null;

            lock (gate) {
                if (uri.Host == "upload.test")
                    return request.Headers.Authorization is not null ? Error (HttpStatusCode.Unauthorized, "unauthenticated") : UploadChunk (uri, request.Content!.Headers.ContentRange!, body);

                if (uri.AbsolutePath == "/v1.0/me/")
                    return Ok (new JsonObject { ["id"] = "user-1", ["displayName"] = "A Reader", ["userPrincipalName"] = "reader@example.com" });
                var rest = uri.AbsolutePath[Drive.Length..];
                var query = Majorsilence.Recital.Storage.OAuth.OAuthClient.ParseQuery (uri.Query);
                if (rest == "root/delta")
                    return Delta (query);
                if (rest.StartsWith ("items/", StringComparison.Ordinal))
                    return ById (Uri.UnescapeDataString (rest[6..]));

                // root, root/children, root:/a/b:, root:/a/b:/children, root:/a/b:/content, root:/a/b:/createUploadSession
                string path;
                string action;
                if (rest.StartsWith ("root:/", StringComparison.Ordinal)) {
                    var end = rest.IndexOf (':', 6);
                    path = Uri.UnescapeDataString (end < 0 ? rest[6..] : rest[6..end]);
                    action = end < 0 ? "" : rest[(end + 1)..].TrimStart ('/');
                } else {
                    path = "";
                    action = rest == "root" ? "" : rest["root/".Length..];
                }

                return (request.Method.Method, action) switch {
                    ("GET", "") => Get (path),
                    ("GET", "children") => Children (path, query),
                    ("GET", "content") => items.TryGetValue (path, out var file) && !file.Folder ? new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (file.Data) } : NotFound (),
                    ("PUT", "content") => Put (path, body, ifMatch),
                    ("POST", "createUploadSession") => StartSession (path, ifMatch),
                    _ => Error (HttpStatusCode.BadRequest, $"{request.Method} {rest}"),
                };
            }
        }

        private HttpResponseMessage Get (string path) => items.TryGetValue (path, out var item) ? Ok (Json (item, withParentPath: true)) : NotFound ();

        private HttpResponseMessage ById (string id)
            => items.Values.FirstOrDefault (i => i.Id == id) is { } item ? Ok (Json (item, withParentPath: false)) : NotFound ();

        private HttpResponseMessage Children (string path, Dictionary<string, string> query)
        {
            if (!items.TryGetValue (path, out var folder) || !folder.Folder)
                return NotFound ();
            var skip = query.TryGetValue ("skip", out var s) ? int.Parse (s, CultureInfo.InvariantCulture) : 0;
            var children = items.Values.Where (i => i.Path.Length > 0 && string.Equals (Parent (i.Path), path, StringComparison.OrdinalIgnoreCase))
                .OrderBy (i => i.Path, StringComparer.Ordinal).ToList ();
            var page = children.Skip (skip).Take (PageSize).ToList ();
            var result = new JsonObject { ["value"] = new JsonArray (page.Select (i => (JsonNode)Json (i, withParentPath: true)).ToArray ()) };
            if (skip + page.Count < children.Count)
                result["@odata.nextLink"] = $"https://graph.microsoft.com{Drive}{(path.Length == 0 ? "root" : "root:/" + Escape (path) + ":")}/children?skip={skip + page.Count}";
            return Ok (result);
        }

        private HttpResponseMessage Put (string path, byte[] data, string? ifMatch)
        {
            if (ifMatch is not null && (!items.TryGetValue (path, out var current) || current.ETag != ifMatch))
                return Error (HttpStatusCode.PreconditionFailed, "eTag mismatch");
            if (items.TryGetValue (path, out var existing) && existing.Folder)
                return Error (HttpStatusCode.Conflict, "nameAlreadyExists");
            var segments = path.Split ('/');
            for (var i = 1; i < segments.Length; i++) {
                var folder = string.Join ('/', segments[..i]);
                if (!items.TryGetValue (folder, out var f))
                    items[folder] = new Item ("id-" + Guid.NewGuid ().ToString ("N")[..8], folder, true, [], "\"" + Guid.NewGuid () + "\"", DateTimeOffset.UtcNow, ++sequence);
                else if (!f.Folder)
                    return Error (HttpStatusCode.Conflict, "nameAlreadyExists");
            }
            var item = new Item (existing?.Id ?? "id-" + Guid.NewGuid ().ToString ("N")[..8], existing?.Path ?? path, false, data, "\"{" + Guid.NewGuid () + "},1\"", DateTimeOffset.UtcNow, ++sequence);
            items[path] = item;
            return new HttpResponseMessage (existing is null ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = Content (Json (item, withParentPath: true)) };
        }

        private HttpResponseMessage StartSession (string path, string? ifMatch)
        {
            if (ifMatch is not null && (!items.TryGetValue (path, out var current) || current.ETag != ifMatch))
                return Error (HttpStatusCode.PreconditionFailed, "eTag mismatch");
            var id = Guid.NewGuid ().ToString ("N");
            sessions[id] = (path, ifMatch, new MemoryStream ());
            return Ok (new JsonObject { ["uploadUrl"] = "https://upload.test/session/" + id });
        }

        private HttpResponseMessage UploadChunk (Uri uri, System.Net.Http.Headers.ContentRangeHeaderValue range, byte[] body)
        {
            var id = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf ('/') + 1)..];
            if (!sessions.TryGetValue (id, out var session) || range.From != session.Data.Length)
                return Error (HttpStatusCode.RequestedRangeNotSatisfiable, "bad range");
            session.Data.Write (body);
            if (session.Data.Length < range.Length)
                return new HttpResponseMessage (HttpStatusCode.Accepted) { Content = Content (new JsonObject { ["nextExpectedRanges"] = new JsonArray ($"{session.Data.Length}-") }) };
            sessions.Remove (id);
            return Put (session.Path, session.Data.ToArray (), null);
        }

        private HttpResponseMessage Delta (Dictionary<string, string> query)
        {
            var token = query.GetValueOrDefault ("token", "0");
            if (token == "latest")
                return Ok (new JsonObject { ["value"] = new JsonArray (), ["@odata.deltaLink"] = DeltaLink (sequence) });
            var since = long.Parse (token, CultureInfo.InvariantCulture);
            var skip = query.TryGetValue ("skip", out var s) ? int.Parse (s, CultureInfo.InvariantCulture) : 0;
            var changed = items.Values.Where (i => i.Sequence > since && i.Path.Length > 0).OrderBy (i => i.Sequence).ToList ();
            var page = changed.Skip (skip).Take (PageSize).ToList ();
            var result = new JsonObject { ["value"] = new JsonArray (page.Select (i => (JsonNode)Json (i, withParentPath: false)).ToArray ()) };
            if (skip + page.Count < changed.Count)
                result["@odata.nextLink"] = $"https://graph.microsoft.com{Drive}root/delta?token={since}&skip={skip + page.Count}";
            else
                result["@odata.deltaLink"] = DeltaLink (sequence);
            return Ok (result);
        }

        private static string DeltaLink (long token) => $"https://graph.microsoft.com{Drive}root/delta?token={token}";

        private JsonObject Json (Item item, bool withParentPath)
        {
            var name = item.Path.Length == 0 ? "root" : item.Path[(item.Path.LastIndexOf ('/') + 1)..];
            var json = new JsonObject {
                ["id"] = item.Id,
                ["name"] = name,
                ["eTag"] = item.ETag,
                ["lastModifiedDateTime"] = item.Modified.ToString ("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            };
            if (item.Path.Length > 0) {
                var parent = items[Parent (item.Path)];
                var reference = new JsonObject { ["id"] = parent.Id, ["driveId"] = "d1" };
                if (withParentPath)
                    reference["path"] = "/drive/root:" + (parent.Path.Length == 0 ? "" : "/" + Escape (parent.Path));
                json["parentReference"] = reference;
            }
            if (item.Folder)
                json["folder"] = new JsonObject { ["childCount"] = 0 };
            else {
                json["file"] = new JsonObject { ["mimeType"] = "application/octet-stream" };
                json["size"] = item.Data.Length;
            }
            return json;
        }

        private static string Escape (string path) => string.Join ('/', path.Split ('/').Select (Uri.EscapeDataString));

        private static string Parent (string path) => path.LastIndexOf ('/') is var slash and >= 0 ? path[..slash] : "";

        private static StringContent Content (JsonNode json) => new (json.ToJsonString (), Encoding.UTF8, "application/json");

        private static HttpResponseMessage Ok (JsonNode json) => new (HttpStatusCode.OK) { Content = Content (json) };

        private static HttpResponseMessage NotFound () => Error (HttpStatusCode.NotFound, "itemNotFound");

        private static HttpResponseMessage Error (HttpStatusCode code, string error)
            => new (code) { Content = Content (new JsonObject { ["error"] = new JsonObject { ["code"] = error, ["message"] = error } }) };
    }
}
