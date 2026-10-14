using System.Diagnostics;
using System.Text;
using AngleSharp.Dom;
using Xunit;

namespace Majorsilence.Recital.Epub.Tests
{
    /// <summary>Hand-built books for the shapes the corpus does not cover (plan Phase 2).</summary>
    public class EdgeCaseTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ValidEpub3OpensWithoutWarnings ()
        {
            await using var book = await TestEpub.Epub3 ().OpenAsync ();

            Assert.Empty (book.Warnings);
            Assert.Equal ("3.0", book.Package.Version);
            Assert.Equal (2, book.Spine.Count);
            Assert.Equal (EpubTocSource.Nav, book.TableOfContentsSource);

            var one = book.TableOfContents[0];
            Assert.Equal ("One", one.Title);
            Assert.Equal ("OEBPS/text/ch1.xhtml", one.Path);
            Assert.Equal (0, one.SpineIndex);
            var part = Assert.Single (one.Children);
            Assert.Equal ("p2", part.Fragment);
        }

        [Fact]
        public async Task NcxOnlyEpub2UsesTheNcx ()
        {
            var opf = TestEpub.Opf ("2.0",
                """<dc:identifier id="uid">id-1</dc:identifier><dc:title>Old</dc:title><dc:creator opf:role="aut" opf:file-as="Author, Old">Old Author</dc:creator>""",
                """
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                """,
                """<itemref idref="c1"/><itemref idref="c2"/>""",
                spineAttributes: """ toc="ncx" """);

            var ncx = """
                <?xml version="1.0"?>
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
                  <navMap>
                    <navPoint id="n1" playOrder="1"><navLabel><text>Chapter  One</text></navLabel><content src="text/ch1.xhtml"/>
                      <navPoint id="n2" playOrder="2"><navLabel><text>Part two</text></navLabel><content src="text/ch1.xhtml#p2"/></navPoint>
                    </navPoint>
                    <navPoint id="n3" playOrder="3"><navLabel><text>Chapter Two</text></navLabel><content src="text/ch2.xhtml"/></navPoint>
                  </navMap>
                </ncx>
                """;

            await using var book = await TestEpub.Epub3 ()
                .Without ("OEBPS/nav.xhtml")
                .With (TestEpub.OpfPath, opf)
                .With ("OEBPS/toc.ncx", ncx)
                .OpenAsync ();

            Assert.Equal (EpubTocSource.Ncx, book.TableOfContentsSource);
            Assert.Equal (["Chapter One", "Chapter Two"], book.TableOfContents.Select (t => t.Title));
            Assert.Equal ("p2", book.TableOfContents[0].Children[0].Fragment);
            Assert.Equal (1, book.TableOfContents[1].SpineIndex);

            var author = Assert.Single (book.Metadata.Authors);
            Assert.Equal ("Author, Old", author.FileAs);
            Assert.Equal ("aut", author.Role);
        }

        [Fact]
        public async Task NoNavAndNoNcxStillReads ()
        {
            var manifest = """
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                """;

            await using var book = await TestEpub.Epub3 (manifest).Without ("OEBPS/nav.xhtml").OpenAsync ();

            Assert.Equal (EpubTocSource.None, book.TableOfContentsSource);
            Assert.Empty (book.TableOfContents);
            Assert.Contains (book.Warnings, w => w.Code == "no-toc");
            Assert.Equal (2, book.Spine.Count);
            Assert.True ((await book.LoadContentDocumentAsync (1, Ct)).ParsedAsXml);
        }

        [Fact]
        public async Task EmptyNavFallsBackToNcx ()
        {
            var manifest = """
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                """;
            var ncx = """
                <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/"><navMap>
                  <navPoint><navLabel><text>From NCX</text></navLabel><content src="text/ch1.xhtml"/></navPoint>
                </navMap></ncx>
                """;

            await using var book = await TestEpub.Epub3 (manifest)
                .With ("OEBPS/nav.xhtml", TestEpub.Xhtml ("<nav epub:type=\"landmarks\"></nav>"))
                .With ("OEBPS/toc.ncx", ncx)
                .OpenAsync ();

            Assert.Equal (EpubTocSource.Ncx, book.TableOfContentsSource);
            Assert.Equal ("From NCX", Assert.Single (book.TableOfContents).Title);
            Assert.Contains (book.Warnings, w => w.Code == "nav-empty");
        }

        [Fact]
        public async Task BrokenManifestEntriesAreSkippedWithWarnings ()
        {
            var manifest = """
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c1" href="text/duplicate.xhtml" media-type="application/xhtml+xml"/>
                <item id="gone" href="text/missing.xhtml" media-type="application/xhtml+xml"/>
                <item id="nohref" media-type="application/xhtml+xml"/>
                <item id="pic" href="images/page.png" media-type="image/png" fallback="c2"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="web" href="https://example.com/remote.xhtml" media-type="application/xhtml+xml"/>
                """;
            var spine = """
                <itemref idref="c1"/>
                <itemref idref="nobody"/>
                <itemref idref="gone"/>
                <itemref idref="pic" linear="no"/>
                """;

            await using var book = await TestEpub.Epub3 (manifest, spine)
                .With ("OEBPS/images/page.png", [0x89, 0x50, 0x4E, 0x47])
                .OpenAsync ();

            var codes = book.Warnings.Select (w => w.Code).ToList ();
            Assert.Contains ("manifest-duplicate-id", codes);
            Assert.Contains ("manifest-missing-file", codes);
            Assert.Contains ("manifest-item-invalid", codes);
            Assert.Contains ("spine-unknown-idref", codes);
            Assert.Contains ("spine-missing-file", codes);

            Assert.Equal (2, book.Spine.Count);
            Assert.Equal ("OEBPS/text/ch1.xhtml", book.Spine[0].Content.Path);

            // The image itemref renders through its XHTML fallback, keeps its own linear flag, and keeps the
            // CFI step of its position in the source spine (the fourth itemref → 8), not of its index here.
            var pic = book.Spine[1];
            Assert.Equal ("pic", pic.Item.Id);
            Assert.Equal ("OEBPS/text/ch2.xhtml", pic.Content.Path);
            Assert.False (pic.Linear);
            Assert.Equal (8, pic.CfiStep);

            var remote = book.Package.FindById ("web")!;
            Assert.Null (remote.Path);
            Assert.False (remote.Exists);
        }

        [Fact]
        public async Task MissingContainerFindsThePackage ()
        {
            await using var book = await TestEpub.Epub3 ().Without ("META-INF/container.xml").OpenAsync ();

            Assert.Contains (book.Warnings, w => w.Code == "container-missing");
            Assert.Equal (TestEpub.OpfPath, book.Package.Path);
            Assert.Equal (2, book.Spine.Count);
        }

        [Fact]
        public async Task ContainerPointingNowhereFindsThePackage ()
        {
            await using var book = await TestEpub.Epub3 ().With ("META-INF/container.xml", TestEpub.Container ("wrong/place.opf")).OpenAsync ();

            Assert.Contains (book.Warnings, w => w.Code == "container-rootfile");
            Assert.Equal (TestEpub.OpfPath, book.Package.Path);
        }

        [Fact]
        public async Task NoPackageAtAllIsNotAnEpub ()
        {
            var epub = TestEpub.Epub3 ().Without ("META-INF/container.xml").Without (TestEpub.OpfPath);

            var error = await Assert.ThrowsAsync<EpubException> (epub.OpenAsync);
            Assert.Contains ("no package document", error.Message);
        }

        [Fact]
        public async Task NotAZipIsNotAnEpub ()
        {
            var stream = new MemoryStream (Encoding.UTF8.GetBytes ("<html>not a zip</html>"));

            await Assert.ThrowsAsync<EpubException> (() => EpubBook.OpenAsync (stream, ct: Ct));
        }

        [Fact]
        public async Task NonSeekableStreamOpens ()
        {
            await using var book = await EpubBook.OpenAsync (new ForwardOnlyStream (TestEpub.Epub3 ().ToStream ()), ct: Ct);

            Assert.Equal ("Test Book", book.Metadata.Title);
        }

        [Fact]
        public async Task HrefWithWrongCaseStillResolves ()
        {
            var manifest = """<item id="c1" href="Text/CH1.xhtml" media-type="application/xhtml+xml"/>""";

            await using var book = await TestEpub.Epub3 (manifest, """<itemref idref="c1"/>""").Without ("OEBPS/nav.xhtml").OpenAsync ();

            Assert.Contains (book.Warnings, w => w.Code == "path-case");
            Assert.Equal ("One", (await book.LoadContentDocumentAsync (0, Ct)).Document.QuerySelector ("h1")!.TextContent);
        }

        [Fact]
        public async Task PercentEncodedHrefResolves ()
        {
            var manifest = """<item id="c1" href="text/chapter%201.xhtml" media-type="application/xhtml+xml"/>""";

            await using var book = await TestEpub.Epub3 (manifest, """<itemref idref="c1"/>""")
                .Without ("OEBPS/nav.xhtml")
                .With ("OEBPS/text/chapter 1.xhtml", TestEpub.Xhtml ("<p>spaced</p>"))
                .OpenAsync ();

            Assert.Equal ("OEBPS/text/chapter 1.xhtml", book.Spine[0].Content.Path);
            Assert.True (book.Spine[0].Content.Exists);
        }

        [Fact]
        public async Task HtmlNamedEntitiesStillParseAsXml ()
        {
            await using var book = await TestEpub.Epub3 ()
                .With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml ("<p>a&nbsp;b&mdash;c &amp; d &#169;</p>"))
                .OpenAsync ();

            var document = await book.LoadContentDocumentAsync (0, Ct);

            Assert.True (document.ParsedAsXml);
            Assert.Equal ("a b—c & d ©", document.Document.QuerySelector ("p")!.TextContent);
        }

        [Fact]
        public async Task SelfClosingElementsKeepTheirSiblings ()
        {
            await using var book = await TestEpub.Epub3 ()
                .With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml ("""<a id="anchor"/><p>after</p><div/><p>last</p>"""))
                .OpenAsync ();

            var body = (await book.LoadContentDocumentAsync (0, Ct)).Document.Body!;

            Assert.Equal (["a", "p", "div", "p"], body.Children.Select (c => c.LocalName));
            Assert.Equal (string.Empty, body.Children[0].TextContent);
        }

        [Fact]
        public async Task ContentKeepsHtmlElementTypesAndEpubType ()
        {
            await using var book = await TestEpub.Epub3 ()
                .With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml ("""<section epub:type="chapter"><p>x</p><svg xmlns="http://www.w3.org/2000/svg"><rect/></svg></section>"""))
                .OpenAsync ();

            var document = (await book.LoadContentDocumentAsync (0, Ct)).Document;
            var section = document.QuerySelector ("section")!;

            Assert.IsAssignableFrom<AngleSharp.Html.Dom.IHtmlElement> (section);
            Assert.IsAssignableFrom<AngleSharp.Svg.Dom.ISvgElement> (document.QuerySelector ("svg"));
            Assert.Equal ("chapter", section.GetAttribute ("http://www.idpf.org/2007/ops", "type"));
        }

        [Fact]
        public async Task MalformedXhtmlFallsBackToHtmlAndCfisStillWork ()
        {
            await using var book = await TestEpub.Epub3 ()
                .With ("OEBPS/text/ch1.xhtml", "<html><body><p>line one<br>line two</p><p>unclosed</body></html>")
                .OpenAsync ();

            var document = await book.LoadContentDocumentAsync (0, Ct);

            Assert.False (document.ParsedAsXml);
            Assert.Contains (book.Warnings, w => w.Code == "xhtml-not-well-formed");

            var text = (IText)document.Document.QuerySelectorAll ("p")[1].FirstChild!;
            var resolved = document.Resolve (EpubCfi.Parse (document.GetCfi (text, 3).ToString ()));
            Assert.Same (text, resolved!.Value.Node);
            Assert.Equal (3, resolved.Value.Offset);
        }

        [Fact]
        public async Task DeclaredWindows1252EncodingIsHonoured ()
        {
            var head = Encoding.ASCII.GetBytes ("<?xml version=\"1.0\" encoding=\"windows-1252\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body><p>");
            byte[] quoted = [0x93, (byte)'q', 0x94];
            var tail = Encoding.ASCII.GetBytes ("</p></body></html>");

            await using var book = await TestEpub.Epub3 ().With ("OEBPS/text/ch1.xhtml", [.. head, .. quoted, .. tail]).OpenAsync ();

            var document = await book.LoadContentDocumentAsync (0, Ct);
            Assert.Equal ("“q”", document.Document.QuerySelector ("p")!.TextContent);
        }

        [Fact]
        public async Task Epub3MetadataRefinementsPickTheMainTitleAndRoles ()
        {
            var metadata = """
                <dc:identifier id="uid">urn:isbn:9780000000000</dc:identifier>
                <dc:title id="sub">A Subtitle</dc:title>
                <dc:title id="main">The Main Title</dc:title>
                <meta refines="#main" property="title-type">main</meta>
                <meta refines="#sub" property="title-type">subtitle</meta>
                <dc:creator id="ed">E. Editor</dc:creator>
                <meta refines="#ed" property="role" scheme="marc:relators">edt</meta>
                <dc:creator id="au">W. Writer</dc:creator>
                <meta refines="#au" property="role" scheme="marc:relators">aut</meta>
                <meta refines="#au" property="file-as">Writer, W.</meta>
                <dc:language>fr</dc:language>
                <dc:language>en</dc:language>
                """;

            await using var book = await TestEpub.Epub3 (metadata: metadata).OpenAsync ();

            Assert.Equal ("The Main Title", book.Metadata.Title);
            Assert.Equal (2, book.Metadata.Titles.Count);
            var author = Assert.Single (book.Metadata.Authors);
            Assert.Equal ("W. Writer", author.Name);
            Assert.Equal ("Writer, W.", author.FileAs);
            Assert.Equal ("fr", book.Metadata.Language);
            Assert.Equal ("urn:isbn:9780000000000", book.Metadata.UniqueIdentifier);
        }

        [Fact]
        public async Task Epub2MetaCoverById ()
        {
            var manifest = """
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="the-cover" href="images/front.jpg" media-type="image/jpeg"/>
                """;
            var opf = TestEpub.Opf ("2.0",
                """<dc:identifier id="uid">x</dc:identifier><dc:title>T</dc:title><meta name="cover" content="the-cover"/>""",
                manifest, """<itemref idref="c1"/>""");

            await using var book = await TestEpub.Epub3 ()
                .With (TestEpub.OpfPath, opf)
                .With ("OEBPS/images/front.jpg", [0xFF, 0xD8, 0xFF])
                .OpenAsync ();

            Assert.Equal ("the-cover", book.Cover?.Id);
        }

        [Fact]
        public async Task GuideCoverPageYieldsItsImage ()
        {
            var manifest = """
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="cv" href="text/titlepage.xhtml" media-type="application/xhtml+xml"/>
                <item id="img" href="images/art.png" media-type="image/png"/>
                """;
            var opf = TestEpub.Opf ("2.0", """<dc:identifier id="uid">x</dc:identifier><dc:title>T</dc:title>""",
                manifest, """<itemref idref="cv"/><itemref idref="c1"/>""",
                extra: """<guide><reference type="cover" href="text/titlepage.xhtml" title="Cover"/></guide>""");

            await using var book = await TestEpub.Epub3 ()
                .With (TestEpub.OpfPath, opf)
                .With ("OEBPS/text/titlepage.xhtml", TestEpub.Xhtml ("""<div><img src="../images/art.png" alt=""/></div>"""))
                .With ("OEBPS/images/art.png", [0x89, 0x50, 0x4E, 0x47])
                .OpenAsync ();

            Assert.Equal ("img", book.Cover?.Id);
        }

        [Fact]
        public async Task IdpfObfuscatedFontIsDeobfuscated ()
        {
            var original = Enumerable.Range (0, 3000).Select (i => (byte)(i * 7)).ToArray ();
            var stored = (byte[])original.Clone ();
            Assert.True (EpubFonts.Deobfuscate (stored, FontObfuscation.Idpf, TestEpub.Identifier));

            await using var book = await WithFont (stored, EpubFonts.IdpfAlgorithm).OpenAsync ();

            var font = Assert.Single (book.Fonts);
            Assert.Equal (FontObfuscation.Idpf, font.Obfuscation);
            Assert.Equal (original, await book.ReadResourceAsync (font.Item, Ct));
            Assert.False (book.IsDrmProtected);
        }

        [Fact]
        public async Task AdobeObfuscatedFontIsDeobfuscated ()
        {
            var original = Enumerable.Range (0, 3000).Select (i => (byte)(i * 13)).ToArray ();
            var stored = (byte[])original.Clone ();
            Assert.True (EpubFonts.Deobfuscate (stored, FontObfuscation.Adobe, TestEpub.Identifier));

            await using var book = await WithFont (stored, EpubFonts.AdobeAlgorithm).OpenAsync ();

            Assert.Equal (original, await book.ReadResourceAsync (book.Fonts[0].Item, Ct));
        }

        [Fact]
        public void IdpfKeyIsSha1OfTheIdentifierWithoutWhitespace ()
        {
            // Independently computed: sha1("urn:uuid:A1B2").
            var expected = Convert.FromHexString ("83ffa5d7c0790e03056ceafde6d213920f5361fe");
            var zeros = new byte[1100];

            EpubFonts.Deobfuscate (zeros, FontObfuscation.Idpf, " urn:uuid:A1B2 \n");

            Assert.Equal (expected, zeros[..20]);
            Assert.Equal (expected, zeros[1020..1040]);
            Assert.All (zeros[1040..], b => Assert.Equal (0, b));
        }

        [Fact]
        public async Task DrmEncryptedContentIsReportedNotRead ()
        {
            var encryption = """
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData>
                    <enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
                    <enc:CipherData><enc:CipherReference URI="OEBPS/text/ch1.xhtml"/></enc:CipherData>
                  </enc:EncryptedData>
                </encryption>
                """;

            await using var book = await TestEpub.Epub3 ().With ("META-INF/encryption.xml", encryption).OpenAsync ();

            Assert.True (book.IsDrmProtected);
            Assert.Contains (book.Warnings, w => w.Code == "drm");
            await Assert.ThrowsAsync<EpubException> (() => book.LoadContentDocumentAsync (0, Ct));
        }

        [Fact]
        public async Task CfiIdAssertionsWinOverStaleIndexes ()
        {
            await using var book = await TestEpub.Epub3 ().OpenAsync ();
            var document = await book.LoadContentDocumentAsync (0, Ct);

            // Index 2 is now the h1, but the assertion names p2: the id wins. The itemref step is wrong
            // too (/8), and its assertion still finds ref-c1.
            var cfi = EpubCfi.Parse ("epubcfi(/6/8[ref-c1]!/4/2[p2]/1:3)");

            Assert.Equal (0, book.ResolveSpineIndex (cfi));
            var position = document.Resolve (cfi)!.Value;
            Assert.Equal ("Second ", ((IText)position.Node).Data);
            Assert.Equal (3, position.Offset);
        }

        [Fact]
        public async Task CfiOffsetPastTheEndIsClamped ()
        {
            await using var book = await TestEpub.Epub3 ().OpenAsync ();
            var document = await book.LoadContentDocumentAsync (1, Ct);

            var position = document.Resolve (EpubCfi.Parse ("epubcfi(/6/4!/4/4/1:9999)"))!.Value;

            Assert.Equal ("Third paragraph.".Length, position.Offset);
        }

        [Fact]
        public async Task CfiForAnotherDocumentOrMissingPathDoesNotResolve ()
        {
            await using var book = await TestEpub.Epub3 ().OpenAsync ();
            var document = await book.LoadContentDocumentAsync (0, Ct);

            Assert.Null (book.ResolveSpineIndex (EpubCfi.Parse ("epubcfi(/6/40!/4/2)")));
            Assert.Null (book.ResolveSpineIndex (EpubCfi.Parse ("epubcfi(/4/2!/4/2)")));
            Assert.Null (document.Resolve (EpubCfi.Parse ("epubcfi(/6/2!/4/40/1:0)")));
        }

        /// <summary>
        /// A comment and a CDATA section split one run of text into several DOM text nodes; a CFI counts
        /// characters across the whole run. A boundary offset resolves to the end of the earlier node.
        /// </summary>
        [Fact]
        public async Task CfiOffsetsCountAcrossTextSplitByCommentsAndCdata ()
        {
            await using var book = await TestEpub.Epub3 ()
                .With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml ("<p>abc<!-- note -->def<![CDATA[ghi]]><em>x</em>tail</p>"))
                .OpenAsync ();
            var document = await book.LoadContentDocumentAsync (0, Ct);
            var texts = document.Document.QuerySelector ("p")!.ChildNodes.OfType<IText> ().ToList ();

            Assert.Equal (["abc", "def", "ghi", "tail"], texts.Select (t => t.Data));
            Assert.Equal ("epubcfi(/6/2[ref-c1]!/4/2/1:4)", document.GetCfi (texts[1], 1).ToString ());
            Assert.Equal ("epubcfi(/6/2[ref-c1]!/4/2/1:8)", document.GetCfi (texts[2], 2).ToString ());
            Assert.Equal ("epubcfi(/6/2[ref-c1]!/4/2/3:2)", document.GetCfi (texts[3], 2).ToString ());

            Assert.Equal (new DomPosition (texts[1], 1), document.Resolve (EpubCfi.Parse ("epubcfi(/6/2!/4/2/1:4)")));
            Assert.Equal (new DomPosition (texts[2], 2), document.Resolve (EpubCfi.Parse ("epubcfi(/6/2!/4/2/1:8)")));
            Assert.Equal (new DomPosition (texts[0], 3), document.Resolve (EpubCfi.Parse ("epubcfi(/6/2!/4/2/1:3)")));
            Assert.Equal (new DomPosition (texts[3], 2), document.Resolve (EpubCfi.Parse ("epubcfi(/6/2!/4/2/3:2)")));
        }

        [Fact]
        public async Task RangeCfiRoundTripsThroughADocument ()
        {
            await using var book = await TestEpub.Epub3 ().OpenAsync ();
            var document = await book.LoadContentDocumentAsync (0, Ct);
            var paragraphs = document.Document.QuerySelectorAll ("p");
            var first = (IText)paragraphs[0].FirstChild!;
            var emphasised = (IText)paragraphs[1].QuerySelector ("em")!.FirstChild!;

            var range = document.GetRangeCfi (new DomPosition (first, 6), new DomPosition (emphasised, 4));
            var parsed = EpubCfi.Parse (range.ToString ());

            Assert.Equal (range, parsed);
            Assert.Same (first, document.Resolve (parsed)!.Value.Node);
            Assert.Same (emphasised, document.Resolve (parsed, end: true)!.Value.Node);
            Assert.Equal (4, document.Resolve (parsed, end: true)!.Value.Offset);
        }

        [Fact]
        public async Task HugeChapterParsesAndResolves ()
        {
            const int count = 20_000;
            var body = new StringBuilder ();
            for (var i = 0; i < count; i++)
                body.Append ("<p id=\"p").Append (i).Append ("\">Paragraph ").Append (i).Append (" with <em>some</em> emphasis and a bit more text to make it realistic.</p>\n");

            var timer = Stopwatch.StartNew ();
            await using var book = await TestEpub.Epub3 ().With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml (body.ToString ())).OpenAsync ();
            var document = await book.LoadContentDocumentAsync (0, Ct);
            timer.Stop ();

            var paragraphs = document.Document.QuerySelectorAll ("p");
            Assert.Equal (count, paragraphs.Length);
            Assert.True (timer.Elapsed < TimeSpan.FromSeconds (10), $"Loading {count} paragraphs took {timer.Elapsed}.");

            foreach (var index in new[] { 0, count / 2, count - 1 }) {
                var text = (IText)paragraphs[index].LastChild!;
                var cfi = EpubCfi.Parse (document.GetCfi (text, 4).ToString ());
                Assert.Same (text, document.Resolve (cfi)!.Value.Node);
            }
        }

        private static TestEpub WithFont (byte[] stored, string algorithm)
        {
            var manifest = """
                <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                <item id="font" href="fonts/body.otf" media-type="application/vnd.ms-opentype"/>
                """;
            var encryption = $"""
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                  <enc:EncryptedData>
                    <enc:EncryptionMethod Algorithm="{algorithm}"/>
                    <enc:CipherData><enc:CipherReference URI="OEBPS/fonts/body.otf"/></enc:CipherData>
                  </enc:EncryptedData>
                </encryption>
                """;

            return TestEpub.Epub3 (manifest)
                .With ("OEBPS/fonts/body.otf", stored)
                .With ("META-INF/encryption.xml", encryption);
        }

        /// <summary>A stream that cannot seek, like a network or content-provider stream.</summary>
        private sealed class ForwardOnlyStream (Stream inner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException ();
            public override long Position { get => throw new NotSupportedException (); set => throw new NotSupportedException (); }
            public override void Flush () { }
            public override int Read (byte[] buffer, int offset, int count) => inner.Read (buffer, offset, count);
            public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
            public override void SetLength (long value) => throw new NotSupportedException ();
            public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

            protected override void Dispose (bool disposing)
            {
                if (disposing)
                    inner.Dispose ();
                base.Dispose (disposing);
            }
        }
    }
}
