using AngleSharp.Dom;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Text;

namespace Majorsilence.Recital.Layout.Boxes
{
    /// <summary>A node of the styled box tree: what the layout engine makes of one chapter's DOM and CSS.</summary>
    public abstract class Box
    {
        protected Box (ComputedStyle style, IElement? element)
        {
            Style = style;
            Element = element;
        }

        public ComputedStyle Style { get; }

        /// <summary>The element that generated the box; null for an anonymous box.</summary>
        public IElement? Element { get; }
    }

    /// <summary>A block container: its children are blocks, paragraphs, images and tables.</summary>
    public sealed class BlockBox : Box
    {
        public BlockBox (ComputedStyle style, IElement? element) : base (style, element)
        {
        }

        public List<Box> Children { get; } = [];

        /// <summary>The list marker ("1.", "•") when this is a list item.</summary>
        public string? Marker { get; set; }
    }

    /// <summary>
    /// An inline formatting context: the text and line breaks that flow into lines together. Its style is
    /// the containing block's (text-align, text-indent, the line-height strut); each item has its own.
    /// </summary>
    public sealed class ParagraphBox : Box
    {
        public ParagraphBox (ComputedStyle style, IElement? element) : base (style, element)
        {
        }

        public List<InlineItem> Items { get; } = [];

        /// <summary>The list marker of the list item this paragraph starts, if any.</summary>
        public string? Marker { get; set; }

        /// <summary>Shaped text, built on first layout and reused for every page size.</summary>
        internal ShapedParagraph? Shaped { get; set; }
    }

    public abstract record InlineItem (ComputedStyle Style);

    /// <summary>A DOM text node, whole: whitespace processing happens when the paragraph is shaped.</summary>
    public sealed record TextItem (ComputedStyle Style, IText Node) : InlineItem (Style);

    /// <summary>A forced line break (<c>&lt;br/&gt;</c>).</summary>
    public sealed record LineBreakItem (ComputedStyle Style, IElement Element) : InlineItem (Style);

    /// <summary>An image. Always laid out as its own block, scaled to fit the column and the page.</summary>
    public sealed class ImageBox : Box
    {
        public ImageBox (ComputedStyle style, IElement element, string path, float width, float height) : base (style, element)
        {
            Path = path;
            IntrinsicWidth = width;
            IntrinsicHeight = height;
        }

        /// <summary>Archive path of the image file.</summary>
        public string Path { get; }

        public float IntrinsicWidth { get; }
        public float IntrinsicHeight { get; }
    }

    public sealed class TableBox : Box
    {
        public TableBox (ComputedStyle style, IElement element) : base (style, element)
        {
        }

        public List<TableRowBox> Rows { get; } = [];
    }

    public sealed class TableRowBox : Box
    {
        public TableRowBox (ComputedStyle style, IElement element) : base (style, element)
        {
        }

        public List<BlockBox> Cells { get; } = [];
    }
}
