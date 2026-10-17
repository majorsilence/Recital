using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// An in-process Google Drive API v3 (what the provider calls): files by id with parents, queries of the
    /// form the provider sends (parents, name, trashed), results in pages of three, multipart, media and
    /// resumable uploads, and the changes feed. Same-named files in one folder are allowed, as on Drive;
    /// <see cref="AddDuplicateFolder"/> makes one the way two racing devices would.
    /// </summary>
    internal sealed partial class FakeDriveServer : HttpMessageHandler
    {
        private const string FolderType = "application/vnd.google-apps.folder";
        private const int PageSize = 3;
        private readonly object gate = new ();
        private readonly Dictionary<string, Item> items = new (StringComparer.Ordinal);
        private readonly Dictionary<string, (string Method, string? Id, JsonNode Metadata)> sessions = [];
        private long sequence;
        private DateTimeOffset clock = new (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

        private sealed record Item (string Id, string Name, string MimeType, string Parent, byte[] Data, long Version, DateTimeOffset Created, DateTimeOffset Modified, long Sequence);

        public FakeDriveServer ()
        {
            items["root-id"] = new Item ("root-id", "My Drive", FolderType, "", [], 1, clock, clock, 0);
        }

        public HttpStatusCode? FailWith { get; set; }

        /// <summary>Another folder named like the one at <paramref name="parentId"/>/<paramref name="name"/>, created later.</summary>
        public string AddDuplicateFolder (string parentId, string name)
        {
            lock (gate)
                return Add (name, FolderType, parentId, []).Id;
        }

        /// <summary>A file written straight into folder <paramref name="parentId"/> (by another device).</summary>
        public void AddFile (string parentId, string name, byte[] data)
        {
            lock (gate)
                Add (name, "application/octet-stream", parentId, data);
        }

        public string IdOf (string parentId, string name)
        {
            lock (gate)
                return items.Values.Where (i => i.Parent == parentId && i.Name == name).OrderBy (i => i.Created).First ().Id;
        }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (FailWith is { } failure)
                return new HttpResponseMessage (failure) { Content = new StringContent ("{}") };
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var query = Majorsilence.Recital.Storage.OAuth.OAuthClient.ParseQuery (uri.Query);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync (cancellationToken);
            var contentType = request.Content?.Headers.ContentType;

            lock (gate) {
                var method = request.Method.Method;
                if (path.StartsWith ("/upload/drive/v3/resumable/", StringComparison.Ordinal))
                    return FinishSession (path[(path.LastIndexOf ('/') + 1)..], body);
                if (path.StartsWith ("/upload/drive/v3/files", StringComparison.Ordinal)) {
                    var id = path.Length > "/upload/drive/v3/files/".Length ? path["/upload/drive/v3/files/".Length..] : null;
                    return query["uploadType"] switch {
                        "multipart" when method == "POST" => CreateMultipart (body, contentType!),
                        "media" when method == "PATCH" && id is not null => Replace (id, body),
                        "resumable" => StartSession (method, id, JsonNode.Parse (body.Length == 0 ? "{}"u8.ToArray () : body)!),
                        _ => Error (HttpStatusCode.BadRequest, "unsupported upload"),
                    };
                }
                var rest = path["/drive/v3/".Length..];
                if (rest == "about")
                    return Ok (new JsonObject { ["user"] = new JsonObject { ["permissionId"] = "perm-1", ["displayName"] = "A Reader", ["emailAddress"] = "reader@example.com" } });
                if (rest == "changes/startPageToken")
                    return Ok (new JsonObject { ["startPageToken"] = sequence.ToString (CultureInfo.InvariantCulture) });
                if (rest == "changes")
                    return Changes (query["pageToken"]);
                if (rest == "files" && method == "GET")
                    return List (query["q"], query.GetValueOrDefault ("pageToken"));
                if (rest == "files" && method == "POST") {
                    var metadata = JsonNode.Parse (body)!;
                    return Ok (Json (Add (metadata["name"]!.GetValue<string> (), metadata["mimeType"]?.GetValue<string> () ?? "application/octet-stream", metadata["parents"]![0]!.GetValue<string> (), [])));
                }
                if (rest.StartsWith ("files/", StringComparison.Ordinal) && method == "GET") {
                    var id = Uri.UnescapeDataString (rest[6..]);
                    if (id == "root")
                        id = "root-id";
                    if (!items.TryGetValue (id, out var item))
                        return Error (HttpStatusCode.NotFound, "notFound");
                    return query.GetValueOrDefault ("alt") == "media" ? new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (item.Data) } : Ok (Json (item));
                }
                return Error (HttpStatusCode.BadRequest, $"{method} {path}");
            }
        }

        private HttpResponseMessage List (string q, string? pageToken)
        {
            var parents = ParentsClause ().Matches (q).Select (m => m.Groups[1].Value).ToHashSet (StringComparer.Ordinal);
            var nameMatch = NameClause ().Match (q);
            var name = nameMatch.Success ? Regex.Replace (nameMatch.Groups[1].Value, @"\\(.)", "$1") : null;
            if (!q.Contains ("trashed = false", StringComparison.Ordinal))
                return Error (HttpStatusCode.BadRequest, "expected trashed = false");
            var matching = items.Values.Where (i => parents.Contains (i.Parent) && (name is null || i.Name == name)).OrderBy (i => i.Id, StringComparer.Ordinal).ToList ();
            var offset = pageToken is null ? 0 : int.Parse (pageToken, CultureInfo.InvariantCulture);
            var page = matching.Skip (offset).Take (PageSize).ToList ();
            var result = new JsonObject { ["files"] = new JsonArray (page.Select (i => (JsonNode)Json (i)).ToArray ()) };
            if (offset + page.Count < matching.Count)
                result["nextPageToken"] = (offset + page.Count).ToString (CultureInfo.InvariantCulture);
            return Ok (result);
        }

        private HttpResponseMessage Changes (string token)
        {
            var parts = token.Split (':');
            var since = long.Parse (parts[0], CultureInfo.InvariantCulture);
            var offset = parts.Length > 1 ? int.Parse (parts[1], CultureInfo.InvariantCulture) : 0;
            var changed = items.Values.Where (i => i.Sequence > since).OrderBy (i => i.Sequence).ToList ();
            var page = changed.Skip (offset).Take (PageSize).ToList ();
            var result = new JsonObject {
                ["changes"] = new JsonArray (page.Select (i => (JsonNode)new JsonObject { ["fileId"] = i.Id, ["removed"] = false, ["file"] = Json (i) }).ToArray ()),
            };
            if (offset + page.Count < changed.Count)
                result["nextPageToken"] = $"{since}:{offset + page.Count}";
            else
                result["newStartPageToken"] = sequence.ToString (CultureInfo.InvariantCulture);
            return Ok (result);
        }

        private HttpResponseMessage CreateMultipart (byte[] body, System.Net.Http.Headers.MediaTypeHeaderValue type)
        {
            var boundary = type.Parameters.First (p => p.Name == "boundary").Value!.Trim ('"');
            var parts = SplitMultipart (body, boundary);
            if (parts.Count != 2)
                return Error (HttpStatusCode.BadRequest, "expected metadata and media");
            var metadata = JsonNode.Parse (parts[0])!;
            return Ok (Json (Add (metadata["name"]!.GetValue<string> (), "application/octet-stream", metadata["parents"]![0]!.GetValue<string> (), parts[1])));
        }

        private HttpResponseMessage Replace (string id, byte[] data)
        {
            if (!items.TryGetValue (id, out var item))
                return Error (HttpStatusCode.NotFound, "notFound");
            clock = clock.AddSeconds (1);
            var updated = item with { Data = data, Version = item.Version + 1, Modified = clock, Sequence = ++sequence };
            items[id] = updated;
            return Ok (Json (updated));
        }

        private HttpResponseMessage StartSession (string method, string? id, JsonNode metadata)
        {
            var session = Guid.NewGuid ().ToString ("N");
            sessions[session] = (method, id, metadata);
            var response = new HttpResponseMessage (HttpStatusCode.OK);
            response.Headers.Location = new Uri ("https://www.googleapis.com/upload/drive/v3/resumable/" + session);
            return response;
        }

        private HttpResponseMessage FinishSession (string session, byte[] data)
        {
            if (!sessions.Remove (session, out var started))
                return Error (HttpStatusCode.NotFound, "no session");
            if (started.Id is not null)
                return Replace (started.Id, data);
            return Ok (Json (Add (started.Metadata["name"]!.GetValue<string> (), "application/octet-stream", started.Metadata["parents"]![0]!.GetValue<string> (), data)));
        }

        private Item Add (string name, string mimeType, string parent, byte[] data)
        {
            clock = clock.AddSeconds (1);
            var item = new Item ("id" + (++sequence).ToString ("D5", CultureInfo.InvariantCulture), name, mimeType, parent, data, 1, clock, clock, sequence);
            items[item.Id] = item;
            return item;
        }

        private static JsonObject Json (Item item)
        {
            var json = new JsonObject {
                ["id"] = item.Id,
                ["name"] = item.Name,
                ["mimeType"] = item.MimeType,
                ["parents"] = new JsonArray (item.Parent),
                ["version"] = item.Version.ToString (CultureInfo.InvariantCulture),
                ["createdTime"] = item.Created.ToString ("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["modifiedTime"] = item.Modified.ToString ("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["trashed"] = false,
            };
            if (item.MimeType != FolderType)
                json["size"] = item.Data.Length.ToString (CultureInfo.InvariantCulture);
            return json;
        }

        private static List<byte[]> SplitMultipart (byte[] body, string boundary)
        {
            var marker = Encoding.ASCII.GetBytes ("--" + boundary);
            var parts = new List<byte[]> ();
            var position = IndexOf (body, marker, 0);
            while (position >= 0) {
                var start = position + marker.Length;
                if (start + 1 < body.Length && body[start] == '-' && body[start + 1] == '-')
                    break;
                var headersEnd = IndexOf (body, "\r\n\r\n"u8.ToArray (), start);
                var next = IndexOf (body, marker, headersEnd);
                var contentStart = headersEnd + 4;
                var contentEnd = next - 2; // the CRLF before the boundary
                parts.Add (body[contentStart..contentEnd]);
                position = next;
            }
            return parts;
        }

        private static int IndexOf (byte[] haystack, byte[] needle, int from)
        {
            for (var i = Math.Max (0, from); i <= haystack.Length - needle.Length; i++)
                if (haystack.AsSpan (i, needle.Length).SequenceEqual (needle))
                    return i;
            return -1;
        }

        private static HttpResponseMessage Ok (JsonNode json) => new (HttpStatusCode.OK) { Content = new StringContent (json.ToJsonString (), Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Error (HttpStatusCode code, string reason)
            => new (code) { Content = new StringContent (new JsonObject { ["error"] = new JsonObject { ["message"] = reason, ["errors"] = new JsonArray (new JsonObject { ["reason"] = reason }) } }.ToJsonString ()) };

        [GeneratedRegex (@"'((?:\\.|[^'\\])*)' in parents")]
        private static partial Regex ParentsClause ();

        [GeneratedRegex (@"name = '((?:\\.|[^'\\])*)'")]
        private static partial Regex NameClause ();
    }
}
