using System.Diagnostics;
using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using Majorsilence.Recital.Epub;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    /// <summary>The plan's Phase 3 acceptance tests, run on the real corpus.</summary>
    public class CorpusLayoutTests
    {
        public static readonly Dictionary<string, LayoutSettings> Viewports = new () {
            ["phone"] = new () { PageWidth = 360, PageHeight = 640, MarginLeft = 20, MarginRight = 20, MarginTop = 28, MarginBottom = 28, FontSize = 18 },
            ["tablet"] = new () { PageWidth = 768, PageHeight = 1024, MarginLeft = 64, MarginRight = 64, MarginTop = 72, MarginBottom = 72, FontSize = 20 },
            ["desktop"] = new () { PageWidth = 1280, PageHeight = 800, MarginLeft = 300, MarginRight = 300, MarginTop = 56, MarginBottom = 56, FontSize = 20 },
        };

        public static TheoryData<string> Books => new () {
            "se-austen-pride-and-prejudice.epub",
            "se-shelley-frankenstein.epub",
            "pg-11-alice-epub3.epub",
            "pg-1342-pride-and-prejudice-epub2.epub",
            "pg-84-frankenstein-epub3.epub",
            "pg-1661-sherlock-holmes-epub3.epub",
            "pg-2701-moby-dick-epub3.epub",
        };

        /// <summary>
        /// Golden page breaks: every page's first position (as a CFI), line count and opening text. Alice in
        /// full (Gutenberg markup, images, justified), and the opening of the Standard Ebooks Pride and
        /// Prejudice (its own CSS, small caps, title page). Fonts are bundled and every metric comes from
        /// the font files, so these must match on every OS.
        /// </summary>
        [Theory]
        [InlineData ("pg-11-alice-epub3.epub", "alice", int.MaxValue, "phone")]
        [InlineData ("pg-11-alice-epub3.epub", "alice", int.MaxValue, "tablet")]
        [InlineData ("pg-11-alice-epub3.epub", "alice", int.MaxValue, "desktop")]
        [InlineData ("se-austen-pride-and-prejudice.epub", "se-pride", 8, "phone")]
        [InlineData ("se-austen-pride-and-prejudice.epub", "se-pride", 8, "tablet")]
        [InlineData ("se-austen-pride-and-prejudice.epub", "se-pride", 8, "desktop")]
        public async Task PageBreaksMatchTheGoldenSnapshot (string file, string name, int chapters, string viewport)
        {
            await using var book = await LayoutKit.OpenCorpus (file);
            using var engine = new LayoutEngine (book);
            var settings = Viewports[viewport];
            var layouts = await LayoutKit.LayoutBook (engine, settings, chapters);

            var json = new StringBuilder ();
            json.Append ("{\n  \"book\": ").Append (Snapshot.Quote (file))
                .Append (",\n  \"viewport\": ").Append (Snapshot.Quote ($"{settings.PageWidth}x{settings.PageHeight}, margins {settings.MarginTop}/{settings.MarginRight}/{settings.MarginBottom}/{settings.MarginLeft}, font {settings.FontSize}px"))
                .Append (",\n  \"chapters\": [");

            for (var c = 0; c < layouts.Count; c++) {
                var chapter = layouts[c];
                json.Append (c == 0 ? "\n" : ",\n").Append ("    { \"spine\": ").Append (chapter.SpineIndex).Append (", \"pages\": [");
                for (var p = 0; p < chapter.Pages.Count; p++) {
                    var page = chapter.Pages[p];
                    var start = page.StartPosition is { } position ? chapter.Document.GetCfi (position).ToString () : "";
                    var lines = page.Lines.Count ();
                    var opening = page.Items[0] switch {
                        TextLine line => line.Text,
                        ImageItem image => $"[image {image.Path} {image.Width:0}x{image.Height:0}]",
                        _ => "[rule]",
                    };
                    if (opening.Length > 32)
                        opening = opening[..32];
                    json.Append (p == 0 ? "\n" : ",\n").Append ("      [").Append (Snapshot.Quote (start)).Append (", ").Append (lines).Append (", ").Append (Snapshot.Quote (opening)).Append (']');
                }
                json.Append ("\n    ] }");
            }

            json.Append ("\n  ]\n}\n");
            Snapshot.AssertMatches ($"{name}.{viewport}", json.ToString ());
        }

        /// <summary>
        /// Plan Phase 3: a position at 10%, 50% and 90% of the book maps to a CFI and back to the same
        /// paragraph at a different viewport size, landing on the page that shows it.
        /// </summary>
        [Theory]
        [MemberData (nameof (Books))]
        public async Task CfiFindsTheSameParagraphAtAnotherViewport (string file)
        {
            await using var book = await LayoutKit.OpenCorpus (file);
            using var engine = new LayoutEngine (book);

            foreach (var (from, to) in new[] { ("phone", "desktop"), ("desktop", "tablet") }) {
                var source = await LayoutKit.LayoutBook (engine, Viewports[from]);
                var target = await LayoutKit.LayoutBook (engine, Viewports[to]);
                var pages = source.SelectMany (c => c.Pages.Select (p => (Chapter: c, Page: p))).ToList ();

                foreach (var fraction in new[] { 0.1, 0.5, 0.9 }) {
                    var (chapter, page) = pages[(int)(pages.Count * fraction)];
                    var position = page.StartPosition!.Value;
                    var cfi = EpubCfi.Parse (chapter.Document.GetCfi (position).ToString ());

                    var spine = Assert.IsType<int> (book.ResolveSpineIndex (cfi));
                    var other = target[spine];
                    var resolved = Assert.IsType<DomPosition> (other.Document.Resolve (cfi));
                    var landed = Assert.IsType<LayoutPage> (other.PageOf (cfi));

                    Assert.Same (Paragraph (position.Node), Paragraph (resolved.Node));
                    Assert.True (DomOrder.Compare (landed.StartPosition!.Value, resolved) <= 0, $"{file} {fraction:P0}: page {landed.Index} starts after {cfi}.");
                    Assert.True (DomOrder.Compare (resolved, landed.EndPosition!.Value) <= 0, $"{file} {fraction:P0}: page {landed.Index} ends before {cfi}.");
                }
            }
        }

        private static IElement? Paragraph (INode node)
        {
            for (var element = node as IElement ?? node.ParentElement; element is not null; element = element.ParentElement)
                if (element.LocalName is "p" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "div" or "img" or "td")
                    return element;
            return null;
        }

        /// <summary>Properties every page of every book must have.</summary>
        [Theory]
        [MemberData (nameof (Books))]
        public async Task EveryPageIsWellFormed (string file)
        {
            await using var book = await LayoutKit.OpenCorpus (file);
            using var engine = new LayoutEngine (book);
            var settings = Viewports["phone"];
            var height = settings.ContentHeight;
            var width = settings.ContentWidth;

            foreach (var chapter in await LayoutKit.LayoutBook (engine, settings)) {
                DomPosition? previousStart = null;
                var linesSeen = new Dictionary<object, int> ();
                var linesPerPage = new List<(object Paragraph, int Count, int LineCount)> ();

                foreach (var page in chapter.Pages) {
                    // Pages come in document order.
                    if (page.StartPosition is { } start) {
                        if (previousStart is { } before)
                            Assert.True (DomOrder.Compare (before, start) < 0, $"{file} spine {chapter.SpineIndex}: page {page.Index} starts before the previous page.");
                        previousStart = start;
                    }

                    // Everything fits the content box (a single oversized item may overflow its own page).
                    if (page.Items.Count > 1)
                        Assert.All (page.Items, i => Assert.True (i.Y + i.Height <= height + 0.5f, $"{file} spine {chapter.SpineIndex} page {page.Index}: item ends at {i.Y + i.Height}."));
                    foreach (var image in page.Items.OfType<ImageItem> ())
                        Assert.True (image.Width <= width + 0.5f && image.Height <= height + 0.5f);

                    // Lines fit the column, apart from text that may not wrap (<pre>, a word with nowhere to
                    // break) which overflows as in CSS.
                    foreach (var line in page.Lines) {
                        var mayOverflow = line.BlockElement?.Closest ("pre") is not null || line.Text.Trim ().IndexOf (' ', StringComparison.Ordinal) < 0;
                        Assert.True (mayOverflow || line.Runs.Count == 0 || line.Runs.Max (r => r.EndX) <= line.Width + 0.5f,
                            $"{file} spine {chapter.SpineIndex}: '{line.Text}' overflows.");
                    }

                    // Each paragraph's lines appear once, in order.
                    foreach (var group in page.Lines.GroupBy (l => l.ParagraphKey)) {
                        foreach (var line in group) {
                            var expected = linesSeen.GetValueOrDefault (group.Key);
                            Assert.Equal (expected, line.LineIndex);
                            linesSeen[group.Key] = expected + 1;
                        }
                        linesPerPage.Add ((group.Key, group.Count (), group.First ().LineCount));
                    }

                    // No heading is the last thing on a page that has room for more than the heading.
                    if (page.Items.LastOrDefault () is TextLine last && last.BlockElement?.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                        Assert.True (page.Lines.All (l => l.BlockElement == last.BlockElement) || page == chapter.Pages[^1],
                            $"{file} spine {chapter.SpineIndex}: heading '{last.Text}' ends page {page.Index}.");
                }

                // Widows and orphans: a paragraph split across pages leaves at least two lines on each side
                // (when it has at least four lines to share).
                foreach (var split in linesPerPage.GroupBy (p => p.Paragraph).Where (g => g.Count () > 1 && g.First ().LineCount >= 4)) {
                    Assert.True (split.First ().Count >= 2, $"{file} spine {chapter.SpineIndex}: an orphan of {split.First ().Count} line.");
                    Assert.True (split.Last ().Count >= 2, $"{file} spine {chapter.SpineIndex}: a widow of {split.Last ().Count} line.");
                }

                // Every paragraph that was laid out appears whole.
                foreach (var (paragraph, count) in linesSeen)
                    Assert.Equal (linesPerPage.First (p => p.Paragraph == paragraph).LineCount, count);
            }
        }

        [Fact]
        public async Task BoxTreeMatchesTheGoldenSnapshot ()
        {
            var epub = Epub.Tests.TestEpub.Epub3 ().With ("OEBPS/images/fig.png", LayoutKit.Png (120, 80));
            const string css = """
                .note { font-size: smaller; margin: 0 2em } .hidden { display: none }
                h1 + p { text-indent: 0 } p { text-indent: 1em; margin: 0.5em 0 }
                """;
            const string body = """
                <h1 id="top">A  Title</h1>
                <p>First   <em>emphasised</em> text,<br/>then a break.</p>
                <p class="hidden">Not shown.</p>
                <div>Loose text <span>inline</span><p>a nested paragraph</p>after it</div>
                <ol><li>one</li><li value="5">five</li><li>six</li></ol>
                <blockquote class="note"><p>Quoted.</p></blockquote>
                <p class="figure"><img src="../images/fig.png" alt="figure"/></p>
                <table><tr><th>H</th><td>cell</td></tr></table>
                <hr/>
                """;

            var tree = await LayoutKit.BoxTree (body, css, epub: epub);
            Snapshot.AssertMatches ("box-tree", BoxTreeJson.Write (tree));
        }
    }

    /// <summary>Tests that measure time run alone, after the parallel ones, so nothing shares the CPU.</summary>
    [CollectionDefinition ("Timing", DisableParallelization = true)]
    public sealed class TimingTestsDefinition
    {
    }

    [Collection ("Timing")]
    public class LayoutTimingTests
    {
        /// <summary>
        /// Plan Phase 3: laying out a 300-page novel takes under 2 s. Pride and Prejudice (Gutenberg) comes to
        /// about 440 pages at the tablet viewport (20px type): parsing, styling, shaping and paginating every
        /// chapter, with a fresh engine.
        /// </summary>
        [Fact]
        public async Task ANovelLaysOutInUnderTwoSeconds ()
        {
            await using var book = await LayoutKit.OpenCorpus ("pg-1342-pride-and-prejudice-epub2.epub");
            using var engine = new LayoutEngine (book);

            var timer = Stopwatch.StartNew ();
            var pages = (await LayoutKit.LayoutBook (engine, CorpusLayoutTests.Viewports["tablet"])).Sum (c => c.Pages.Count);
            timer.Stop ();

            TestContext.Current.TestOutputHelper?.WriteLine (string.Create (CultureInfo.InvariantCulture, $"{pages} pages in {timer.Elapsed.TotalMilliseconds:0} ms"));
            Assert.InRange (pages, 300, 600);
            Assert.True (timer.Elapsed < TimeSpan.FromSeconds (2), $"{pages} pages took {timer.Elapsed.TotalMilliseconds:0} ms.");
        }
    }
}
