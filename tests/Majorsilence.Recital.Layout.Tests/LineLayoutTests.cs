using AngleSharp.Dom;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout.Fonts;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    public class LineLayoutTests
    {
        private const float Width = 300; // LayoutKit.Small's content width

        private static float Left (TextLine line) => line.X + line.Runs.Where (r => r.Clusters.Any (c => c >= 0)).Min (r => r.X[0]);
        private static float Right (TextLine line) => line.X + line.Runs.Max (r => r.EndX);

        [Fact]
        public async Task JustifiedLinesFillTheColumnExceptTheLast ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{LayoutKit.Words (60)}</p>", "p { text-align: justify; margin: 0 }", LayoutKit.Small with { PageHeight = 2000 });
            var lines = LayoutKit.Lines (chapter).Select (l => l.Line).ToList ();

            Assert.True (lines.Count > 3);
            foreach (var line in lines.SkipLast (1))
                Assert.Equal (Width, Right (line), 1);
            Assert.True (Right (lines[^1]) < Width - 5);
        }

        [Fact]
        public async Task LinesNeverExceedTheColumn ()
        {
            var chapter = await LayoutKit.Layout ($"<p>{LayoutKit.Words (80)}</p><p>Pneumonoultramicroscopicsilicovolcanoconiosis-and-Supercalifragilisticexpialidocious</p>",
                "p { font-size: 2em }", LayoutKit.Small with { PageHeight = 4000 });

            foreach (var (_, line) in LayoutKit.Lines (chapter))
                Assert.True (Right (line) <= Width + 0.5f, $"'{line.Text}' ends at {Right (line)}.");
        }

        [Fact]
        public async Task TextIndentAppliesToTheFirstLineOnly ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ($"<p>{LayoutKit.Words (30)}</p>", "p { text-indent: 2em }")).Select (l => l.Line).ToList ();

            Assert.Equal (36f, Left (lines[0]), 1);
            Assert.Equal (0f, Left (lines[1]), 1);
        }

        [Fact]
        public async Task NegativeIndentHangsIntoThePadding ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ($"<p>{LayoutKit.Words (30)}</p>", "p { text-indent: -1em; padding-left: 1em }")).Select (l => l.Line).ToList ();

            Assert.Equal (0f, Left (lines[0]), 1);
            Assert.Equal (18f, Left (lines[1]), 1);
        }

        [Theory]
        [InlineData ("center")]
        [InlineData ("right")]
        public async Task AlignmentOffsetsShortLines (string align)
        {
            var line = LayoutKit.Lines (await LayoutKit.Layout ("<p>short</p>", $"p {{ text-align: {align} }}")).Single ().Line;

            var free = Width - (Right (line) - Left (line));
            Assert.Equal (align == "center" ? free / 2 : free, Left (line), 1);
        }

        [Fact]
        public async Task LineBreakElementsForceLines ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<p>one<br/>two<br/><br/>four</p>")).Select (l => l.Line.Text.TrimEnd ('\n')).ToList ();

            Assert.Equal (["one", "two", "", "four"], lines);
        }

        [Fact]
        public async Task SoftHyphenBreaksWithAVisibleHyphen ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<p>extra&#xAD;ordinarily&#xAD;long&#xAD;compound&#xAD;word</p>", "p { font-size: 3em }")).Select (l => l.Line).ToList ();

            Assert.True (lines.Count > 1);
            var first = lines[0];
            Assert.EndsWith ("­", first.Text);
            Assert.Contains (first.Runs, r => r.Clusters[^1] == -1); // the generated hyphen glyph
            Assert.All (lines.SkipLast (1), l => Assert.True (Right (l) <= Width + 0.5f));
        }

        [Fact]
        public async Task NowrapKeepsTextOnOneLine ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<p>Before <span style=\"white-space: nowrap\">one two three four five six</span> after.</p>")).Select (l => l.Line.Text).ToList ();

            Assert.Contains (lines, l => l.Contains ("one two three four five six", StringComparison.Ordinal));
        }

        [Fact]
        public async Task NowrapTextLongerThanTheLineOverflowsRatherThanSplitting ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<p>a <span style=\"white-space: nowrap\">unbreakable unbreakable unbreakable</span> b</p>")).Select (l => l.Line.Text.Trim ()).ToList ();

            Assert.Contains ("unbreakable unbreakable unbreakable", lines);
        }

        [Fact]
        public async Task PreKeepsSpacesAndNewlines ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<pre>a  b\n  c</pre>", "pre { margin: 0 }")).Select (l => l.Line.Text.TrimEnd ('\n')).ToList ();

            Assert.Equal (["a  b", "  c"], lines);
        }

        [Fact]
        public async Task WhitespaceCollapsesAndMapsBackToTheDom ()
        {
            var chapter = await LayoutKit.Layout ("<p>  lots   of\n\t space  <em>here</em> </p>");
            var line = Assert.Single (LayoutKit.Lines (chapter)).Line;

            Assert.Equal ("lots of space here", line.Text);

            // The line starts at "lots", offset 2 of the paragraph's first text node.
            var start = Assert.IsType<DomPosition> (line.StartPosition);
            Assert.Equal ("  lots   of\n\t space  ", ((IText)start.Node).Data);
            Assert.Equal (2, start.Offset);

            // Hitting the middle of "here" lands inside the <em>'s text.
            var em = line.Runs.Last ();
            var hit = line.HitTest (em.X[2] + 1)!.Value;
            Assert.Equal ("here", ((IText)hit.Node).Data);
            Assert.InRange (hit.Offset, 2, 3);
        }

        [Fact]
        public async Task TextTransformAndSmallCaps ()
        {
            var lines = LayoutKit.Lines (await LayoutKit.Layout ("<p><span style=\"text-transform: uppercase\">up</span> <span style=\"font-variant: small-caps\">Small Caps</span></p>")).Select (l => l.Line).ToList ();
            var line = Assert.Single (lines);

            Assert.StartsWith ("UP ", line.Text);
            // Small caps: capitals at full size, lowercase letters as smaller capitals.
            Assert.Contains (line.Runs, r => r.FontSize == 18f);
            Assert.Contains (line.Runs, r => r.FontSize < 18f);
        }

        [Fact]
        public async Task SuperscriptIsRaisedAndSmaller ()
        {
            var line = Assert.Single (LayoutKit.Lines (await LayoutKit.Layout ("<p>x<sup>2</sup></p>"))).Line;

            var sup = line.Runs.Single (r => r.BaselineShift > 0);
            Assert.True (sup.FontSize < 18f);
        }

        [Fact]
        public async Task ListItemsGetMarkersOutsideTheText ()
        {
            var chapter = await LayoutKit.Layout ("<ol start=\"3\"><li>three</li><li>four</li></ol><ul><li>dot</li></ul>");
            var lines = LayoutKit.Lines (chapter).Select (l => l.Line).ToList ();

            Assert.Equal (3, lines.Count);
            foreach (var line in lines) {
                var marker = line.Runs[0];
                Assert.All (marker.Clusters, c => Assert.Equal (-1, c));
                Assert.True (marker.EndX <= 0.01f, "The marker sits in the list's padding, left of the text.");
            }
            // 40px of padding (scaled by 18/16).
            Assert.Equal (45f, lines[0].X, 1);
        }

        [Fact]
        public async Task MissingGlyphsFallBackToAFaceThatHasThem ()
        {
            // U+2603 SNOWMAN is in DejaVu Sans but not DejaVu Serif.
            Assert.False (FontLibrary.Bundled.Resolve (["serif"], 400, false).HasGlyph (0x2603));

            var line = Assert.Single (LayoutKit.Lines (await LayoutKit.Layout ("<p>let it ☃</p>"))).Line;

            Assert.Contains (line.Runs, r => r.Face.Family == FontLibrary.SansSerif);
            Assert.DoesNotContain (line.Runs, r => r.Glyphs.Contains ((ushort)0));
        }

        [Fact]
        public async Task KerningAndLigaturesApplyAcrossWholeWords ()
        {
            // Regression: a bug split every lowercase letter into its own shaping call, which loses both.
            var line = Assert.Single (LayoutKit.Lines (await LayoutKit.Layout ("<p>office</p>"))).Line;

            // DejaVu Serif ligates "ff" (it has no "ffi"): six characters, five glyphs.
            Assert.Equal (5, line.Runs.Sum (r => r.Glyphs.Length));
        }
    }
}
