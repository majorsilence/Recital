using AngleSharp.Dom;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Fonts;
using Majorsilence.Recital.Layout.Text;

namespace Majorsilence.Recital.Layout
{
    /// <summary>One chapter (spine item) paginated for one set of <see cref="LayoutSettings"/>.</summary>
    public sealed class ChapterLayout
    {
        internal ChapterLayout (ContentDocument document, LayoutSettings settings, IReadOnlyList<LayoutPage> pages)
        {
            Document = document;
            Settings = settings;
            Pages = pages;
        }

        public ContentDocument Document { get; }
        public int SpineIndex => Document.SpineIndex;
        public LayoutSettings Settings { get; }
        public IReadOnlyList<LayoutPage> Pages { get; }

        /// <summary>
        /// The page showing a DOM position: the last page that starts at or before it, or the next page when the
        /// position is past everything on that one and before the next page's first content (the start of an
        /// element a forced break begins a page with, ahead of its first character). Positions in content that
        /// is not laid out (hidden elements) land on the page where it would have been.
        /// </summary>
        public LayoutPage PageOf (DomPosition position)
        {
            var low = 0;
            var high = Pages.Count - 1;
            var found = 0;

            while (low <= high) {
                var mid = (low + high) / 2;
                var start = StartOfPageAtOrBefore (mid);

                if (start is null || DomOrder.Compare (start.Value, position) <= 0) {
                    found = mid;
                    low = mid + 1;
                } else {
                    high = mid - 1;
                }
            }

            // Between two pages: past this page's last content, so it belongs with what follows.
            if (found + 1 < Pages.Count && Pages[found].EndPosition is { } end && DomOrder.Compare (end, position) < 0)
                return Pages[found + 1];

            return Pages[found];
        }

        /// <summary>The page a CFI points at, or null when it is for another chapter or does not resolve.</summary>
        public LayoutPage? PageOf (EpubCfi cfi)
            => Document.Resolve (cfi) is { } position ? PageOf (position) : null;

        private DomPosition? StartOfPageAtOrBefore (int index)
        {
            for (var i = index; i >= 0; i--)
                if (Pages[i].StartPosition is { } start)
                    return start;
            return null;
        }
    }

    public sealed class LayoutPage
    {
        internal LayoutPage (int index, IReadOnlyList<PageItem> items)
        {
            Index = index;
            Items = items;
            StartPosition = items.Select (i => i.StartPosition).FirstOrDefault (p => p is not null);
            EndPosition = items.Select (i => i.EndPosition).LastOrDefault (p => p is not null);
        }

        public int Index { get; }

        /// <summary>Everything on the page, in reading order, positioned in the page's content box.</summary>
        public IReadOnlyList<PageItem> Items { get; }

        /// <summary>The first DOM position on the page (its first line or image); null for a page with neither.</summary>
        public DomPosition? StartPosition { get; }

        public DomPosition? EndPosition { get; }

        public IEnumerable<TextLine> Lines => Items.OfType<TextLine> ();

        public override string ToString () => $"Page {Index}: {Items.Count} items";
    }

    /// <summary>Something placed on a page. Coordinates are CSS px from the page's content-box origin.</summary>
    public abstract class PageItem
    {
        public float X { get; internal set; }
        public float Y { get; internal set; }
        public float Width { get; internal set; }
        public float Height { get; internal set; }

        public virtual DomPosition? StartPosition => null;
        public virtual DomPosition? EndPosition => null;

        internal abstract PageItem Clone ();
    }

    /// <summary>A line of text: glyph runs on a common baseline.</summary>
    public sealed class TextLine : PageItem
    {
        private readonly ShapedParagraph paragraph;

        internal TextLine (ShapedParagraph paragraph, int start, int end, IReadOnlyList<GlyphRun> runs, float baseline)
        {
            this.paragraph = paragraph;
            StartIndex = start;
            EndIndex = end;
            Runs = runs;
            Baseline = baseline;
        }

        /// <summary>The baseline, from the line's top.</summary>
        public float Baseline { get; }

        public IReadOnlyList<GlyphRun> Runs { get; }

        /// <summary>The block element (p, h1, li, ...) whose text this line is part of.</summary>
        public IElement? BlockElement => paragraph.Element;

        /// <summary>Index of this line within its paragraph.</summary>
        public int LineIndex { get; internal set; }

        public int LineCount { get; internal set; }

        /// <summary>Identifies the paragraph, so pagination can tell which lines belong together.</summary>
        internal object ParagraphKey => paragraph;

        internal int StartIndex { get; }
        internal int EndIndex { get; }

        /// <summary>The line's text after whitespace processing and text-transform.</summary>
        public string Text => paragraph.Text[StartIndex..EndIndex];

        public override DomPosition? StartPosition => paragraph.PositionAt (StartIndex);
        public override DomPosition? EndPosition => paragraph.PositionAfter (EndIndex);

        /// <summary>The DOM position under <paramref name="x"/> (line coordinates): the nearer edge of the character there.</summary>
        public DomPosition? HitTest (float x)
        {
            var best = StartIndex;
            var bestDistance = float.MaxValue;

            foreach (var run in Runs) {
                for (var i = 0; i < run.Glyphs.Length; i++) {
                    var cluster = run.Clusters[i];
                    if (cluster < 0)
                        continue;

                    var left = run.X[i];
                    var right = i + 1 < run.Glyphs.Length ? run.X[i + 1] : left + run.FontSize * 0.5f;
                    var leftDistance = Math.Abs (x - left);
                    var rightDistance = Math.Abs (x - right);

                    if (leftDistance < bestDistance) {
                        bestDistance = leftDistance;
                        best = cluster;
                    }
                    if (rightDistance < bestDistance) {
                        bestDistance = rightDistance;
                        best = cluster + 1;
                    }
                }
            }

            return best >= EndIndex ? EndPosition : paragraph.PositionAt (best);
        }

        internal override PageItem Clone () => new TextLine (paragraph, StartIndex, EndIndex, Runs, Baseline) {
            X = X, Y = Y, Width = Width, Height = Height, LineIndex = LineIndex, LineCount = LineCount,
        };
    }

    /// <summary>Glyphs in one face, size and colour; positions are relative to the line's X.</summary>
    public sealed class GlyphRun
    {
        internal GlyphRun (FontFace face, float fontSize, ushort[] glyphs, float[] x, int[] clusters, float baselineShift, uint color, TextDecoration decoration)
        {
            Face = face;
            FontSize = fontSize;
            Glyphs = glyphs;
            X = x;
            Clusters = clusters;
            BaselineShift = baselineShift;
            Color = color;
            Decoration = decoration;
        }

        public FontFace Face { get; }
        public float FontSize { get; }
        public ushort[] Glyphs { get; }
        public float[] X { get; }

        /// <summary>Index into the paragraph text of each glyph's character, or -1 for generated glyphs (a hyphen, a list marker).</summary>
        internal int[] Clusters { get; }

        /// <summary>How far the baseline is raised (positive) or lowered for super/subscript.</summary>
        public float BaselineShift { get; }

        public uint Color { get; }
        public TextDecoration Decoration { get; }

        /// <summary>X just past the last glyph.</summary>
        public float EndX { get; internal set; }
    }

    public sealed class ImageItem : PageItem
    {
        internal ImageItem (string path, IElement element)
        {
            Path = path;
            Element = element;
        }

        /// <summary>Archive path of the image.</summary>
        public string Path { get; }

        public IElement Element { get; }

        /// <summary>Drawn with its colours inverted (CSS <c>filter: invert()</c>).</summary>
        public bool Invert { get; internal set; }

        public override DomPosition? StartPosition => new DomPosition (Element, 0);
        public override DomPosition? EndPosition => new DomPosition (Element, 0);

        internal override PageItem Clone () => new ImageItem (Path, Element) { X = X, Y = Y, Width = Width, Height = Height, Invert = Invert };
    }

    /// <summary>A horizontal rule: a block's top or bottom border (an <c>&lt;hr/&gt;</c>, a scene break).</summary>
    public sealed class RuleItem : PageItem
    {
        public uint Color { get; internal set; }

        internal override PageItem Clone () => new RuleItem { X = X, Y = Y, Width = Width, Height = Height, Color = Color };
    }
}
