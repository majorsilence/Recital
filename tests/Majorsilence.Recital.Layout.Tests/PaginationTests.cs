using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Epub.Tests;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    /// <summary>
    /// Every test uses 40px lines on a 200px-tall content box: exactly five lines per page. Lines are made
    /// with &lt;br/&gt; so their count does not depend on text widths. The base font size is 16px, so CSS
    /// pixels are not scaled.
    /// </summary>
    public class PaginationTests
    {
        // A 16px base: CSS pixels are not scaled by the reader's font size, so 40px lines are 40px.
        private static readonly LayoutSettings Settings = LayoutKit.Small with { FontSize = 16 };

        private const string Css = "p, h1, h2, div { margin: 0; line-height: 40px; font-size: 18px; font-weight: normal } ";

        private static string Lines (int count, string label = "line")
            => string.Join ("<br/>", Enumerable.Range (1, count).Select (i => $"{label} {i}"));

        private static int[] LinesPerPage (ChapterLayout chapter) => chapter.Pages.Select (p => p.Lines.Count ()).ToArray ();

        [Fact]
        public async Task FillsPagesWithWholeLines ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (12)}</p>", Css, Settings);

            Assert.Equal ([5, 5, 2], LinesPerPage (chapter));
            Assert.All (chapter.Pages, page => Assert.Equal (0f, page.Items[0].Y));
        }

        [Fact]
        public async Task AWidowPullsALineOver ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (6)}</p>", Css, Settings);
            Assert.Equal ([4, 2], LinesPerPage (chapter));
        }

        [Fact]
        public async Task AnOrphanMovesToTheNextPageAndTheWidowRuleStillHolds ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (4, "filler")}</p><p>{Lines (6)}</p>", Css, Settings);

            Assert.Equal ([4, 4, 2], LinesPerPage (chapter));
            Assert.All (chapter.Pages[1].Lines, l => Assert.StartsWith ("line", l.Text, StringComparison.Ordinal));
        }

        [Fact]
        public async Task WidowsAndOrphansCanBeTurnedOff ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (6)}</p>", Css + "p { widows: 1; orphans: 1 }", Settings);
            Assert.Equal ([5, 1], LinesPerPage (chapter));
        }

        [Fact]
        public async Task AHeadingIsNeverLeftAtTheBottomOfAPage ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (4, "filler")}</p><h2>Heading</h2><p>{Lines (3)}</p>", Css, Settings);

            Assert.Equal ([4, 4], LinesPerPage (chapter));
            Assert.Equal ("Heading", chapter.Pages[1].Lines.First ().Text);
        }

        [Fact]
        public async Task ForcedBreaksStartNewPages ()
        {
            var chapter = await LayoutKit.Layout ("<p>a</p><p style=\"page-break-before: always\">b</p><p style=\"break-after: page\">c</p><p>d</p>", Css, Settings);

            Assert.Equal (["a", "b c", "d"], chapter.Pages.Select (p => string.Join (' ', p.Lines.Select (l => l.Text))));
        }

        [Fact]
        public async Task MarginsAreDroppedAtAnUnforcedBreak ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (4)}</p><p>next</p>", Css + "p { margin-bottom: 100px }", Settings);

            Assert.Equal (2, chapter.Pages.Count);
            Assert.Equal (0f, chapter.Pages[1].Items[0].Y);
        }

        [Fact]
        public async Task MarginsAreKeptAfterAForcedBreak ()
        {
            var chapter = await LayoutKit.Layout ("<p>x</p><h1>Chapter</h1>", Css + "h1 { page-break-before: always; margin-top: 30px }", Settings);

            Assert.Equal (30f, chapter.Pages[1].Lines.Single ().Y);
        }

        [Fact]
        public async Task BreakInsideAvoidMovesTheBlockWhole ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (3, "filler")}</p><div class=\"keep\">{Lines (3)}</div>", Css + ".keep { page-break-inside: avoid }", Settings);

            Assert.Equal ([3, 3], LinesPerPage (chapter));
        }

        [Fact]
        public async Task ImagesAreScaledToFitTheColumnAndThePage ()
        {
            var epub = TestEpub.Epub3 ()
                .With ("OEBPS/images/tall.png", LayoutKit.Png (2000, 3000))
                .With ("OEBPS/images/small.png", LayoutKit.Png (50, 40));
            var chapter = await LayoutKit.Layout ("<p><img src=\"../images/tall.png\" alt=\"\"/></p><p><img src=\"../images/small.png\" alt=\"\"/></p>", Css, Settings, epub);

            var images = chapter.Pages.SelectMany (p => p.Items.OfType<ImageItem> ()).ToList ();
            Assert.Equal (2, images.Count);

            var tall = images[0];
            Assert.Equal (200f, tall.Height, 1);
            Assert.Equal (133.33f, tall.Width, 1);
            Assert.Equal ("OEBPS/images/tall.png", tall.Path);

            var small = images[1];
            Assert.Equal ((50f, 40f), (small.Width, small.Height));
        }

        [Fact]
        public async Task TableRowsLayCellsSideBySideAndStayWhole ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (4, "filler")}</p><table><tr><td>left</td><td>{Lines (2, "right")}</td></tr></table>", Css + "td { line-height: 40px }", Settings);

            Assert.Equal (2, chapter.Pages.Count);
            var row = chapter.Pages[1].Lines.ToList ();
            Assert.Equal (3, row.Count);
            var left = row.Single (l => l.Text == "left");
            var right = row.First (l => l.Text.StartsWith ("right", StringComparison.Ordinal));
            Assert.Equal (left.Y, right.Y);
            Assert.True (right.X >= left.X + 140, "The second cell starts half way across.");
        }

        [Fact]
        public async Task PageOfFindsThePageShowingAPosition ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{Lines (12)}</p>", Css, Settings);

            foreach (var page in chapter.Pages)
                foreach (var line in page.Lines)
                    Assert.Same (page, chapter.PageOf (line.StartPosition!.Value));
        }

        // A contents link names the heading element, and Gutenberg's CSS breaks the page before every h2: the
        // position at the heading's start comes before its first character, past the end of the previous page.
        // It landed on that previous page, so "Chapter 1" opened on the last page of the front matter.
        [Fact]
        public async Task PageOfAnElementAfterAForcedBreakIsThePageItStarts ()
        {
            var chapter = await LayoutKit.Layout ("<p>front</p><h2 id=\"c1\">\n  Chapter 1\n</h2><p>text</p>", Css + "h2 { page-break-before: always }", Settings);
            var heading = chapter.Document.FindFragment ("c1")!;

            Assert.Equal (1, chapter.PageOf (new DomPosition (heading, 0)).Index);
            Assert.Equal (0, chapter.PageOf (chapter.Pages[0].EndPosition!.Value).Index);
        }

        [Fact]
        public async Task DarkModeAppliesTheBooksColourSchemeRulesToImages ()
        {
            const string css = """
                img { width: 50px }
                @media all and (prefers-color-scheme: dark) { img.ink { filter: invert(100%) } }
                """;
            var epub = TestEpub.Epub3 ().With ("OEBPS/images/ink.png", LayoutKit.Png (50, 40));
            const string body = "<p><img class=\"ink\" src=\"../images/ink.png\" alt=\"\"/></p>";

            var light = await LayoutKit.Layout (body, css, Settings, epub);
            var dark = await LayoutKit.Layout (body, css, Settings with { DarkMode = true }, epub);

            Assert.False (light.Pages.SelectMany (p => p.Items.OfType<ImageItem> ()).Single ().Invert);
            Assert.True (dark.Pages.SelectMany (p => p.Items.OfType<ImageItem> ()).Single ().Invert);
        }
    }
}
