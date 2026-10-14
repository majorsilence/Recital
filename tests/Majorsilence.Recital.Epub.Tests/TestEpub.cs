using System.IO.Compression;
using System.Text;
using Xunit;

namespace Majorsilence.Recital.Epub.Tests
{
    /// <summary>
    /// Builds small EPUBs in memory for edge-case tests. <see cref="Epub3"/> is a valid two-chapter EPUB 3;
    /// tests then break it with <see cref="With(string, string)"/> / <see cref="Without"/>.
    /// </summary>
    internal sealed class TestEpub
    {
        public const string OpfPath = "OEBPS/content.opf";
        public const string Identifier = "urn:uuid:12345678-90ab-cdef-1234-567890abcdef";

        private readonly Dictionary<string, byte[]> files = new (StringComparer.Ordinal);

        public static TestEpub Epub3 (string? manifest = null, string? spine = null, string? metadata = null)
        {
            manifest ??= """
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                """;
            spine ??= """
                <itemref idref="c1" id="ref-c1"/>
                <itemref idref="c2" id="ref-c2"/>
                """;
            metadata ??= """
                <dc:identifier id="uid">urn:uuid:12345678-90ab-cdef-1234-567890abcdef</dc:identifier>
                <dc:title>Test Book</dc:title>
                <dc:creator>A. Author</dc:creator>
                <dc:language>en</dc:language>
                <meta property="dcterms:modified">2026-10-14T00:00:00Z</meta>
                """;

            return new TestEpub ()
                .With ("mimetype", "application/epub+zip")
                .With ("META-INF/container.xml", Container (OpfPath))
                .With (OpfPath, Opf ("3.0", metadata, manifest, spine))
                .With ("OEBPS/nav.xhtml", Xhtml ("""
                    <nav epub:type="toc"><ol>
                      <li><a href="text/ch1.xhtml">One</a><ol><li><a href="text/ch1.xhtml#p2">One, part two</a></li></ol></li>
                      <li><a href="text/ch2.xhtml">Two</a></li>
                    </ol></nav>
                    """))
                .With ("OEBPS/text/ch1.xhtml", Xhtml ("""<h1>One</h1><p id="p1">First paragraph.</p><p id="p2">Second <em>emphasised</em> paragraph.</p>"""))
                .With ("OEBPS/text/ch2.xhtml", Xhtml ("""<h1>Two</h1><p>Third paragraph.</p>"""));
        }

        public static string Container (string opfPath) => $"""
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="{opfPath}" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """;

        public static string Opf (string version, string metadata, string manifest, string spine, string spineAttributes = "", string extra = "") => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="{version}" unique-identifier="uid">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">{metadata}</metadata>
              <manifest>{manifest}</manifest>
              <spine{spineAttributes}>{spine}</spine>
              {extra}
            </package>
            """;

        public static string Xhtml (string body, string head = "<title>t</title>") => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head>{head}</head>
            <body>{body}</body>
            </html>
            """;

        public TestEpub With (string path, string content) => With (path, Encoding.UTF8.GetBytes (content));

        public TestEpub With (string path, byte[] content)
        {
            files[path] = content;
            return this;
        }

        public TestEpub Without (string path)
        {
            files.Remove (path);
            return this;
        }

        public MemoryStream ToStream ()
        {
            var stream = new MemoryStream ();

            using (var zip = new ZipArchive (stream, ZipArchiveMode.Create, leaveOpen: true)) {
                foreach (var (path, content) in files) {
                    var entry = zip.CreateEntry (path, path == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                    using var writer = entry.Open ();
                    writer.Write (content);
                }
            }

            stream.Position = 0;
            return stream;
        }

        public Task<EpubBook> OpenAsync () => EpubBook.OpenAsync (ToStream (), leaveOpen: false, TestContext.Current.CancellationToken);
    }
}
