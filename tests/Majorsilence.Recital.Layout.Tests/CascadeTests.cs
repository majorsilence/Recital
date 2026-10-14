using Majorsilence.Recital.Layout.Boxes;
using Majorsilence.Recital.Layout.Css;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    public class CascadeTests
    {
        private static async Task<ComputedStyle> Style (string body, string css, string id, LayoutSettings? settings = null)
            => Find (await LayoutKit.BoxTree (body, css, settings), id)
               ?? throw new InvalidOperationException ($"No box or text for #{id}.");

        /// <summary>The style of the block with that id, or of the text directly inside the inline element with that id.</summary>
        internal static ComputedStyle? Find (Box box, string id)
        {
            if (box.Element?.Id == id && box is not ParagraphBox)
                return box.Style;

            switch (box) {
                case BlockBox block:
                    foreach (var child in block.Children)
                        if (Find (child, id) is { } found)
                            return found;
                    break;
                case ParagraphBox paragraph:
                    foreach (var item in paragraph.Items)
                        if (item is TextItem text && text.Node.ParentElement?.Id == id)
                            return text.Style;
                    break;
                case TableBox table:
                    foreach (var cell in table.Rows.SelectMany (r => r.Cells))
                        if (Find (cell, id) is { } found)
                            return found;
                    break;
            }

            return null;
        }

        [Fact]
        public async Task SpecificityThenOrderDecide ()
        {
            const string css = "p { color: #ff0000 } .a { color: #0000ff } #x { color: #00ff00 } p { color: #ffff00 }";

            Assert.Equal (0xFF00FF00u, (await Style ("<p id=\"x\" class=\"a\">t</p>", css, "x")).Color);
            Assert.Equal (0xFF0000FFu, (await Style ("<p id=\"y\" class=\"a\">t</p>", css, "y")).Color);
            Assert.Equal (0xFFFFFF00u, (await Style ("<p id=\"z\">t</p>", css, "z")).Color);
        }

        [Fact]
        public async Task ImportantBeatsInlineAndInlineBeatsIds ()
        {
            Assert.Equal (0xFF00FF00u, (await Style ("<p id=\"x\" style=\"color: #ff0000\">t</p>", "p { color: #00ff00 !important }", "x")).Color);
            Assert.Equal (0xFFFF0000u, (await Style ("<p id=\"x\" style=\"color: #ff0000\">t</p>", "#x { color: #00ff00 }", "x")).Color);
        }

        [Fact]
        public async Task SelectorListMembersKeepTheirOwnSpecificity ()
        {
            // "div p" matches with (0,0,2); the id member does not match this element.
            const string css = "#nope, div p { color: #00ff00 } .a { color: #ff0000 }";

            Assert.Equal (0xFFFF0000u, (await Style ("<div><p id=\"x\" class=\"a\">t</p></div>", css, "x")).Color);
        }

        [Fact]
        public async Task CompoundAndComplexSelectorsAreNotSplitIntoParts ()
        {
            // Regression: compound selectors are enumerable in AngleSharp; splitting "div.agate" into "div"
            // and ".agate" made every div match.
            const string css = "div.agate { text-align: center } #footer div { letter-spacing: 3px }";

            var style = await Style ("<div id=\"x\" class=\"chapter\"><p>t</p></div>", css, "x");
            Assert.Equal (TextAlign.Left, style.TextAlign);
            Assert.Equal (0, style.LetterSpacing);
        }

        [Fact]
        public async Task UserAgentDefaults ()
        {
            var tree = await LayoutKit.BoxTree ("<h1 id=\"h\">T</h1><p>a <i id=\"i\">b</i> <b id=\"b\">c</b></p>");

            var h1 = Find (tree, "h")!;
            Assert.Equal (700, h1.FontWeight);
            Assert.Equal (36f, h1.FontSize);
            Assert.Equal (BreakRule.Avoid, h1.BreakAfter);
            Assert.True (Find (tree, "i")!.Italic);
            Assert.Equal (700, Find (tree, "b")!.FontWeight);
        }

        [Fact]
        public async Task RelativeLengthsCompound ()
        {
            const string css = "div { font-size: 2em } p { font-size: 75%; margin-top: 1em; text-indent: 1rem } span { font-size: larger }";
            var tree = await LayoutKit.BoxTree ("<div><p id=\"p\">a <span id=\"s\">b</span></p></div>", css);

            var p = Find (tree, "p")!;
            Assert.Equal (27f, p.FontSize, 3);                // 18 x 2 x 0.75
            Assert.Equal (27f, p.MarginTop.Resolve (0), 3);   // 1em of its own size
            Assert.Equal (18f, p.TextIndent.Resolve (0), 3);  // 1rem = the root size
            Assert.Equal (27f * 1.2f, Find (tree, "s")!.FontSize, 3);
        }

        [Fact]
        public async Task AbsoluteLengthsScaleWithTheReadersFontSize ()
        {
            const string css = "p { font-size: 12pt; margin-left: 10px }";

            var normal = await Style ("<p id=\"p\">t</p>", css, "p", LayoutKit.Small with { FontSize = 16 });
            var large = await Style ("<p id=\"p\">t</p>", css, "p", LayoutKit.Small with { FontSize = 32 });

            Assert.Equal (16f, normal.FontSize, 3);
            Assert.Equal (32f, large.FontSize, 3);
            Assert.Equal (20f, large.MarginLeft.Resolve (0), 3);
        }

        [Fact]
        public async Task ShorthandsExpand ()
        {
            var style = await Style ("<p id=\"p\">t</p>", "p { margin: 1em 2em; font: italic bold 2em/1.5 monospace }", "p");

            Assert.True (style.Italic);
            Assert.Equal (700, style.FontWeight);
            Assert.Equal (36f, style.FontSize, 3);
            Assert.Equal (1.5f, style.LineHeightFactor);
            Assert.Equal ("monospace", style.FontFamilies[0]);
            Assert.Equal (36f, style.MarginTop.Resolve (0), 3);
            Assert.Equal (72f, style.MarginLeft.Resolve (0), 3);
        }

        [Fact]
        public async Task PseudoElementRulesDoNotStyleTheWholeElement ()
        {
            var style = await Style ("<p id=\"p\">t</p>", "p::first-letter { font-size: 5em } p:before { color: #ff0000 }", "p");

            Assert.Equal (18f, style.FontSize);
            Assert.Equal (0xFF000000u, style.Color);
        }

        [Theory]
        [InlineData ("@media print { p { color: #ff0000 } }", 0xFF000000u)]
        [InlineData ("@media screen { p { color: #ff0000 } }", 0xFFFF0000u)]
        [InlineData ("@media (prefers-color-scheme: dark) { p { color: #ff0000 } }", 0xFF000000u)]
        [InlineData ("@media amzn-kf8 { p { color: #ff0000 } }", 0xFF000000u)]
        [InlineData ("@media (min-width: 300px) { p { color: #ff0000 } }", 0xFFFF0000u)]
        [InlineData ("@media (min-width: 900px) { p { color: #ff0000 } }", 0xFF000000u)]
        [InlineData ("@supports (display: grid) { p { color: #ff0000 } }", 0xFF000000u)]
        [InlineData ("@supports not (display: grid) { p { color: #ff0000 } }", 0xFFFF0000u)]
        public async Task MediaAndSupportsRules (string css, uint color)
        {
            Assert.Equal (color, (await Style ("<p id=\"p\">t</p>", css, "p")).Color);
        }

        [Fact]
        public async Task EpubNamespacedAttributeSelectorsMatch ()
        {
            const string css = """
                @namespace epub "http://www.idpf.org/2007/ops";
                [epub|type~="z3998:roman"] { font-variant: small-caps }
                """;

            Assert.True ((await Style ("<p>Chapter <span id=\"r\" epub:type=\"z3998:roman\">iv</span></p>", css, "r")).SmallCaps);
        }

        [Fact]
        public async Task ReaderOverridesWin ()
        {
            const string css = "p { font-family: \"Book Face\", serif; line-height: 3; text-align: justify } pre { font-family: monospace }";
            var settings = LayoutKit.Small with { FontFamily = "sans-serif", LineHeight = 1.4f, Justify = false };
            var tree = await LayoutKit.BoxTree ("<p id=\"p\">t</p><pre id=\"code\">x</pre><h2 id=\"h\">H</h2>", css, settings);

            var p = Find (tree, "p")!;
            Assert.Equal ("sans-serif", p.FontFamilies[0]);
            Assert.Equal (1.4f, p.LineHeightFactor);
            Assert.Equal (TextAlign.Left, p.TextAlign);
            Assert.Equal ("monospace", Find (tree, "code")!.FontFamilies[0]);
        }

        [Fact]
        public async Task DisplayNoneAndHeadAreNotLaidOut ()
        {
            var tree = await LayoutKit.BoxTree ("<p id=\"a\">shown</p><p id=\"b\" class=\"x\">hidden</p>", ".x { display: none }");

            Assert.NotNull (Find (tree, "a"));
            Assert.Null (Find (tree, "b"));
        }
    }
}
