using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// An in-process WebDAV server (the parts of RFC 4918 the provider uses): PROPFIND depth 0 and 1, GET,
    /// PUT with If-Match, MKCOL. It answers the way Apache mod_dav does: 409 for a PUT into a missing
    /// folder, 405 for MKCOL on an existing one, quoted strong ETags, hrefs as escaped absolute paths.
    /// </summary>
    internal sealed class FakeWebDavServer : HttpMessageHandler
    {
        private static readonly XNamespace Dav = "DAV:";
        private readonly object gate = new ();
        private readonly Dictionary<string, (byte[] Data, DateTimeOffset Modified, string ETag)> files = new (StringComparer.Ordinal);
        private readonly HashSet<string> folders = new (StringComparer.Ordinal) { "" };
        private readonly string root;

        public FakeWebDavServer (string rootPath = "/dav/")
        {
            root = rootPath;
            BaseUri = new Uri ("http://webdav.test" + rootPath);
        }

        public Uri BaseUri { get; }

        /// <summary>When set, every request fails as this status code would.</summary>
        public HttpStatusCode? FailWith { get; set; }

        public int Requests { get; private set; }

        /// <summary>When set, requests must carry these Basic credentials ("user:password").</summary>
        public string? RequiredCredentials { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (FailWith is { } failure)
                return new HttpResponseMessage (failure);
            if (RequiredCredentials is { } required
                && request.Headers.Authorization?.Parameter != Convert.ToBase64String (Encoding.UTF8.GetBytes (required)))
                return new HttpResponseMessage (HttpStatusCode.Unauthorized);
            var absolute = request.RequestUri!.AbsolutePath;
            if (!absolute.StartsWith (root, StringComparison.Ordinal) && absolute + "/" != root)
                return new HttpResponseMessage (HttpStatusCode.NotFound);
            var path = Uri.UnescapeDataString (absolute.Length > root.Length ? absolute[root.Length..] : "").TrimEnd ('/');
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync (cancellationToken);

            lock (gate) {
                return request.Method.Method switch {
                    "PROPFIND" => Propfind (path, request.Headers.TryGetValues ("Depth", out var depth) ? depth.First () : "infinity"),
                    "GET" => Get (path),
                    "PUT" => Put (path, body, request.Headers.TryGetValues ("If-Match", out var match) ? match.First () : null),
                    "MKCOL" => Mkcol (path),
                    _ => new HttpResponseMessage (HttpStatusCode.MethodNotAllowed),
                };
            }
        }

        private HttpResponseMessage Get (string path)
        {
            if (!files.TryGetValue (path, out var file))
                return new HttpResponseMessage (HttpStatusCode.NotFound);
            var response = new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (file.Data) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue (file.ETag);
            return response;
        }

        private HttpResponseMessage Put (string path, byte[] data, string? ifMatch)
        {
            if (folders.Contains (path))
                return new HttpResponseMessage (HttpStatusCode.MethodNotAllowed);
            if (!folders.Contains (Parent (path)))
                return new HttpResponseMessage (HttpStatusCode.Conflict);
            if (ifMatch is not null && (!files.TryGetValue (path, out var current) || current.ETag != ifMatch))
                return new HttpResponseMessage (HttpStatusCode.PreconditionFailed);
            var etag = "\"" + Convert.ToHexStringLower (SHA256.HashData (data), 0, 8) + "-" + Guid.NewGuid ().ToString ("N")[..6] + "\"";
            var created = !files.ContainsKey (path);
            files[path] = (data, DateTimeOffset.UtcNow, etag);
            return new HttpResponseMessage (created ? HttpStatusCode.Created : HttpStatusCode.NoContent);
        }

        private HttpResponseMessage Mkcol (string path)
        {
            if (folders.Contains (path) || files.ContainsKey (path))
                return new HttpResponseMessage (HttpStatusCode.MethodNotAllowed);
            if (!folders.Contains (Parent (path)))
                return new HttpResponseMessage (HttpStatusCode.Conflict);
            folders.Add (path);
            return new HttpResponseMessage (HttpStatusCode.Created);
        }

        private HttpResponseMessage Propfind (string path, string depth)
        {
            var isFolder = folders.Contains (path);
            if (!isFolder && !files.ContainsKey (path))
                return new HttpResponseMessage (HttpStatusCode.NotFound);

            var responses = new List<XElement> { Response (path, isFolder) };
            if (isFolder && depth == "1") {
                foreach (var folder in folders.Where (f => f.Length > 0 && Parent (f) == path))
                    responses.Add (Response (folder, true));
                foreach (var file in files.Keys.Where (f => Parent (f) == path))
                    responses.Add (Response (file, false));
            }
            var xml = new XDocument (new XElement (Dav + "multistatus", new XAttribute (XNamespace.Xmlns + "D", Dav), responses));
            return new HttpResponseMessage ((HttpStatusCode)207) { Content = new StringContent (xml.ToString (), Encoding.UTF8, "application/xml") };
        }

        private XElement Response (string path, bool isFolder)
        {
            var href = root + string.Join ('/', path.Split ('/').Select (Uri.EscapeDataString)) + (isFolder && path.Length > 0 ? "/" : "");
            var prop = new XElement (Dav + "prop",
                new XElement (Dav + "resourcetype", isFolder ? new XElement (Dav + "collection") : null));
            if (!isFolder) {
                var file = files[path];
                prop.Add (new XElement (Dav + "getcontentlength", file.Data.Length.ToString (CultureInfo.InvariantCulture)),
                    new XElement (Dav + "getlastmodified", file.Modified.ToString ("R", CultureInfo.InvariantCulture)),
                    new XElement (Dav + "getetag", file.ETag));
            }
            var missing = isFolder ? new XElement (Dav + "propstat",
                new XElement (Dav + "prop", new XElement (Dav + "getcontentlength"), new XElement (Dav + "getetag")),
                new XElement (Dav + "status", "HTTP/1.1 404 Not Found")) : null;
            return new XElement (Dav + "response",
                new XElement (Dav + "href", href),
                new XElement (Dav + "propstat", prop, new XElement (Dav + "status", "HTTP/1.1 200 OK")),
                missing);
        }

        private static string Parent (string path)
        {
            var slash = path.LastIndexOf ('/');
            return slash < 0 ? "" : path[..slash];
        }
    }
}
