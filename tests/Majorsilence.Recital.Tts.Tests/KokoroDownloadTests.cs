using System.Net;
using System.Security.Cryptography;
using System.Xml.Linq;
using Majorsilence.Recital.Tts.Kokoro;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>Recital's voices fetched on a phone or tablet: the same pinned files as the desktop bundle, checked by hash.</summary>
    public class KokoroDownloadTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public void TheDownloadListIsTheBundleList ()
        {
            var targets = XDocument.Load (Path.Combine (Repo (), "build", "Kokoro.targets"));
            var bundled = targets.Descendants ().Where (e => e.Name.LocalName == "_RecitalKokoroFile")
                .Select (e => (Path: (string) e.Attribute ("Include")!, Sha256: (string) e.Attribute ("Sha256")!, Url: (string) e.Attribute ("Url")!))
                .ToList ();
            var revision = targets.Descendants ().Single (e => e.Name.LocalName == "_RecitalKokoroRevision").Value;

            Assert.Equal (KokoroFiles.Revision, revision);
            Assert.Equal (bundled.Select (b => (b.Path, b.Sha256)), KokoroFiles.All.Select (f => (f.Path, f.Sha256)));
            Assert.All (KokoroFiles.All, f => Assert.EndsWith (f.Source, bundled.Single (b => b.Path == f.Path).Url, StringComparison.Ordinal));
        }

        [Fact]
        public async Task KeepsWhatMatchesItsHashAndNothingElse ()
        {
            var folder = Directory.CreateTempSubdirectory ("recital-kokoro-").FullName;
            try {
                var good = "good model"u8.ToArray ();
                var files = new List<KokoroFiles.File> {
                    new ("model.onnx", "onnx/model.onnx", Hash (good), good.Length),
                    new ("voices/af_heart.bin", "voices/af_heart.bin", Hash ("expected"u8.ToArray ()), 8),
                };
                var served = new List<string> ();
                using var http = new HttpClient (new Serve (path => {
                    served.Add (path);
                    return path.EndsWith ("model.onnx", StringComparison.Ordinal) ? good : "tampered"u8.ToArray ();
                }));
                var reported = new List<long> ();

                await Assert.ThrowsAsync<InvalidDataException> (() => KokoroFiles.DownloadAsync (http, folder, files, new SyncProgress (reported.Add), Ct));

                Assert.Equal (good, await File.ReadAllBytesAsync (Path.Combine (folder, "model.onnx"), Ct));
                Assert.False (File.Exists (Path.Combine (folder, "voices", "af_heart.bin")));
                Assert.Empty (Directory.EnumerateFiles (folder, "*.part", SearchOption.AllDirectories));
                Assert.Contains ((long) good.Length, reported);   // the whole of the first file
                Assert.All (served, p => Assert.StartsWith ($"/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/{KokoroFiles.Revision}/", p, StringComparison.Ordinal));

                // Again: the file already in place is not fetched twice.
                served.Clear ();
                await Assert.ThrowsAsync<InvalidDataException> (() => KokoroFiles.DownloadAsync (http, folder, files, null, Ct));
                Assert.Equal (["/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/" + KokoroFiles.Revision + "/voices/af_heart.bin"], served);
            } finally {
                Directory.Delete (folder, recursive: true);
            }
        }

        private static string Hash (byte[] data) => Convert.ToHexStringLower (SHA256.HashData (data));

        private static string Repo ()
        {
            var dir = new DirectoryInfo (AppContext.BaseDirectory);
            while (dir is not null && !File.Exists (Path.Combine (dir.FullName, "Majorsilence.Recital.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException ("The repository root was not found.");
        }

        private sealed class Serve (Func<string, byte[]> content) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult (new HttpResponseMessage (HttpStatusCode.OK) { Content = new ByteArrayContent (content (request.RequestUri!.AbsolutePath)) });
        }

        private sealed class SyncProgress (Action<long> report) : IProgress<long>
        {
            public void Report (long value) => report (value);
        }
    }
}
