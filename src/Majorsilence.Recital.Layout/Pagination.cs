using Majorsilence.Recital.Layout.Boxes;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Fonts;
using Majorsilence.Recital.Layout.Text;

namespace Majorsilence.Recital.Layout
{
    internal enum FlowKind { Content, Gap, Break }

    /// <summary>One step of a chapter's vertical flow, before it is cut into pages.</summary>
    internal sealed class FlowItem
    {
        public FlowKind Kind { get; init; }
        public float Height { get; set; }

        /// <summary>Content: the items to place, with Y relative to this flow item's top.</summary>
        public List<PageItem> Items { get; init; } = [];

        // Pagination hints for lines.
        public TextLine? Line { get; init; }
        public int Widows { get; init; } = 1;
        public int Orphans { get; init; } = 1;
        public bool KeepWithNext { get; set; }

        /// <summary>Non-zero when inside a <c>break-inside: avoid</c> block: items with one group id stay together.</summary>
        public int Group { get; set; }

        public static FlowItem Break () => new () { Kind = FlowKind.Break };
        public static FlowItem Gap (float height) => new () { Kind = FlowKind.Gap, Height = height };
    }

    /// <summary>Block layout: walks the box tree and produces the chapter's flow at a given column width.</summary>
    internal sealed class FlowBuilder
    {
        private readonly FontLibrary fonts;
        private readonly LayoutSettings settings;
        private readonly List<FlowItem> flow = [];
        private float pending_margin;
        private int next_group;

        public FlowBuilder (FontLibrary fonts, LayoutSettings settings)
        {
            this.fonts = fonts;
            this.settings = settings;
        }

        public List<FlowItem> Build (BlockBox root) => Build (root, 0, settings.ContentWidth);

        private void Block (BlockBox block, float x, float width)
        {
            var style = block.Style;
            var first = flow.Count;

            if (style.BreakBefore == BreakRule.Page) {
                flow.Add (FlowItem.Break ());
                // Margins adjoining a forced break: the one after it is kept, the one before is not.
                pending_margin = 0;
            }

            pending_margin = Collapse (pending_margin, style.MarginTop.Resolve (width));

            var (innerX, innerWidth) = Horizontal (style, x, width);

            if (style.BorderTopWidth > 0 || style.PaddingTop.Resolve (width) > 0) {
                EmitPendingMargin ();
                if (style.BorderTopWidth > 0)
                    Rule (style, innerX, innerWidth, style.BorderTopWidth);
                if (style.PaddingTop.Resolve (width) is > 0 and var paddingTop)
                    flow.Add (FlowItem.Gap (paddingTop));
            }

            foreach (var child in block.Children) {
                switch (child) {
                    case BlockBox childBlock:
                        Block (childBlock, innerX, innerWidth);
                        break;
                    case ParagraphBox paragraph:
                        Paragraph (paragraph, innerX, innerWidth);
                        break;
                    case ImageBox image:
                        Image (image, innerX, innerWidth, style);
                        break;
                    case TableBox table:
                        Table (table, innerX, innerWidth);
                        break;
                }
            }

            if (style.BorderBottomWidth > 0 || style.PaddingBottom.Resolve (width) > 0) {
                EmitPendingMargin ();
                if (style.PaddingBottom.Resolve (width) is > 0 and var paddingBottom)
                    flow.Add (FlowItem.Gap (paddingBottom));
                if (style.BorderBottomWidth > 0)
                    Rule (style, innerX, innerWidth, style.BorderBottomWidth);
            }

            pending_margin = Collapse (pending_margin, style.MarginBottom.Resolve (width));

            var content = flow.Skip (first).Where (f => f.Kind == FlowKind.Content).ToList ();

            if (style.BreakInside == BreakRule.Avoid && content.Count > 1 && content.All (f => f.Group == 0)) {
                var group = ++next_group;
                foreach (var item in content)
                    item.Group = group;
            }

            if (style.BreakAfter == BreakRule.Avoid && content.Count > 0)
                content[^1].KeepWithNext = true;

            if (style.BreakAfter == BreakRule.Page) {
                flow.Add (FlowItem.Break ());
                pending_margin = 0;
            }
        }

        /// <summary>The content box of a block: margins, padding, an explicit width, auto-margin centring.</summary>
        private static (float X, float Width) Horizontal (ComputedStyle style, float x, float width)
        {
            var marginLeft = style.MarginLeft.Resolve (width);
            var marginRight = style.MarginRight.Resolve (width);
            var paddingLeft = style.PaddingLeft.Resolve (width);
            var paddingRight = style.PaddingRight.Resolve (width);
            var inner = width - marginLeft - marginRight - paddingLeft - paddingRight;

            if (!style.Width.IsAuto || !style.MaxWidth.IsAuto) {
                var wanted = style.Width.IsAuto ? inner : style.Width.Resolve (width);
                if (!style.MaxWidth.IsAuto)
                    wanted = Math.Min (wanted, style.MaxWidth.Resolve (width));
                wanted = Math.Max (0, Math.Min (wanted, width - paddingLeft - paddingRight));

                if (style.MarginLeft.IsAuto && style.MarginRight.IsAuto)
                    marginLeft = (width - wanted - paddingLeft - paddingRight) / 2;
                else if (style.MarginLeft.IsAuto)
                    marginLeft = width - wanted - paddingLeft - paddingRight - marginRight;
                inner = wanted;
            }

            // A column narrower than one em helps nobody; keep text readable.
            inner = Math.Max (inner, Math.Min (width, style.FontSize));
            return (x + marginLeft + paddingLeft, inner);
        }

        private void Paragraph (ParagraphBox paragraph, float x, float width)
        {
            var shaped = paragraph.Shaped ??= ShapedParagraph.Create (paragraph, fonts);
            if (shaped.Text.Length == 0)
                return;

            var indent = paragraph.Style.TextIndent.Resolve (width);
            var ranges = shaped.BreakLines (width - indent, width);

            EmitPendingMargin ();

            for (var i = 0; i < ranges.Count; i++) {
                var line = shaped.BuildLine (ranges[i], i == 0 ? width - indent : width, i == 0 ? indent : 0, first: i == 0);
                line.X = x;
                line.LineIndex = i;
                line.LineCount = ranges.Count;

                flow.Add (new FlowItem {
                    Kind = FlowKind.Content,
                    Height = line.Height,
                    Items = [line],
                    Line = line,
                    Widows = paragraph.Style.Widows,
                    Orphans = paragraph.Style.Orphans,
                });
            }
        }

        private void Image (ImageBox image, float x, float width, ComputedStyle container)
        {
            var style = image.Style;
            var ratio = image.IntrinsicHeight / image.IntrinsicWidth;
            var w = image.IntrinsicWidth;
            var h = image.IntrinsicHeight;

            if (!style.Width.IsAuto) {
                w = style.Width.Resolve (width);
                h = style.Height.IsAuto ? w * ratio : style.Height.Resolve (width);
            } else if (!style.Height.IsAuto) {
                h = style.Height.Resolve (settings.ContentHeight);
                w = h / ratio;
            }

            if (!style.MaxWidth.IsAuto && w > style.MaxWidth.Resolve (width)) {
                w = style.MaxWidth.Resolve (width);
                h = w * ratio;
            }

            // Fit the column, then the page: an image never needs scrolling.
            if (w > width) {
                w = width;
                h = w * ratio;
            }
            if (h > settings.ContentHeight) {
                h = settings.ContentHeight;
                w = h / ratio;
            }

            pending_margin = Collapse (pending_margin, style.MarginTop.Resolve (width));
            EmitPendingMargin ();

            var centred = style.MarginLeft.IsAuto && style.MarginRight.IsAuto || container.TextAlign == TextAlign.Center;
            var left = centred ? (width - w) / 2 : container.TextAlign == TextAlign.Right ? width - w : 0;

            flow.Add (new FlowItem {
                Kind = FlowKind.Content,
                Height = h,
                Items = [new ImageItem (image.Path, image.Element!) { X = x + left, Width = w, Height = h }],
            });

            pending_margin = Collapse (pending_margin, style.MarginBottom.Resolve (width));
        }

        /// <summary>Simple tables: equal-width columns, each row kept on one page.</summary>
        private void Table (TableBox table, float x, float width)
        {
            pending_margin = Collapse (pending_margin, table.Style.MarginTop.Resolve (width));
            var (innerX, innerWidth) = Horizontal (table.Style, x, width);

            foreach (var row in table.Rows) {
                var cellWidth = innerWidth / row.Cells.Count;
                var items = new List<PageItem> ();
                var rowHeight = 0f;

                for (var c = 0; c < row.Cells.Count; c++) {
                    var cellFlow = new FlowBuilder (fonts, settings).Build (row.Cells[c], innerX + c * cellWidth, cellWidth);
                    var y = 0f;
                    var started = false;

                    foreach (var item in cellFlow) {
                        if (item.Kind == FlowKind.Gap) {
                            if (started)
                                y += item.Height;
                            continue;
                        }
                        if (item.Kind != FlowKind.Content)
                            continue;

                        started = true;
                        foreach (var pageItem in item.Items) {
                            pageItem.Y += y;
                            items.Add (pageItem);
                        }
                        y += item.Height;
                    }

                    rowHeight = Math.Max (rowHeight, y);
                }

                if (items.Count == 0)
                    continue;

                EmitPendingMargin ();
                flow.Add (new FlowItem { Kind = FlowKind.Content, Height = rowHeight, Items = items });
            }

            pending_margin = Collapse (pending_margin, table.Style.MarginBottom.Resolve (width));
        }

        public List<FlowItem> Build (BlockBox root, float x, float width)
        {
            Block (root, x, width);
            EmitPendingMargin ();
            return flow;
        }

        private void Rule (ComputedStyle style, float x, float width, float thickness)
        {
            flow.Add (new FlowItem {
                Kind = FlowKind.Content,
                Height = thickness,
                Items = [new RuleItem { X = x, Width = width, Height = thickness, Color = style.BorderColor }],
            });
        }

        private void EmitPendingMargin ()
        {
            if (pending_margin != 0)
                flow.Add (FlowItem.Gap (pending_margin));
            pending_margin = 0;
        }

        /// <summary>Margin collapsing: the larger positive, plus the most negative.</summary>
        private static float Collapse (float a, float b)
            => a >= 0 && b >= 0 ? Math.Max (a, b) : a <= 0 && b <= 0 ? Math.Min (a, b) : a + b;
    }

    /// <summary>Cuts a chapter's flow into pages.</summary>
    internal static class Paginator
    {
        public static List<LayoutPage> Paginate (List<FlowItem> flow, float pageHeight)
        {
            var pages = new List<LayoutPage> ();
            var placed = new List<(int Index, float Y)> ();
            var y = 0f;
            var forcedStart = true;
            var i = 0;

            void ClosePage (int upTo)
            {
                var items = new List<PageItem> ();
                foreach (var (index, top) in placed.Where (p => p.Index < upTo))
                    foreach (var item in flow[index].Items) {
                        var copy = item.Clone ();
                        copy.Y = top + item.Y;
                        items.Add (copy);
                    }

                if (items.Count > 0)
                    pages.Add (new LayoutPage (pages.Count, items));
                placed.Clear ();
                y = 0;
            }

            while (i < flow.Count) {
                var item = flow[i];
                var pageHasContent = placed.Any (p => flow[p.Index].Kind == FlowKind.Content);

                switch (item.Kind) {
                    case FlowKind.Break:
                        if (pageHasContent)
                            ClosePage (int.MaxValue);
                        forcedStart = true;
                        i++;
                        continue;

                    case FlowKind.Gap:
                        // Margins at the top of a page are dropped, unless a forced break put them there.
                        if (!pageHasContent && !forcedStart) {
                            i++;
                            continue;
                        }
                        placed.Add ((i, y));
                        y += item.Height;
                        i++;
                        continue;
                }

                if (y + item.Height <= pageHeight + 0.01f || !pageHasContent) {
                    placed.Add ((i, y));
                    y += item.Height;
                    i++;
                    continue;
                }

                var breakAt = ChooseBreak (flow, placed, i);
                ClosePage (breakAt);
                forcedStart = false;
                i = breakAt;
            }

            ClosePage (int.MaxValue);
            return pages;
        }

        /// <summary>
        /// Where to end the page when <paramref name="next"/> does not fit: before it, or earlier to honour
        /// break-inside: avoid, widows/orphans and keep-with-next. Never at or before the page's first
        /// content, so every page makes progress.
        /// </summary>
        private static int ChooseBreak (List<FlowItem> flow, List<(int Index, float Y)> placed, int next)
        {
            var firstContent = placed.First (p => flow[p.Index].Kind == FlowKind.Content).Index;
            var breakAt = next;

            // A group that must not be split moves to the next page whole.
            if (flow[next].Group is not 0 and var group) {
                var start = next;
                while (PreviousContent (flow, start, firstContent) is { } previous && flow[previous].Group == group)
                    start = previous;
                if (start > firstContent)
                    breakAt = start;
            }

            // Widows and orphans, for a break inside a paragraph.
            if (breakAt == next && flow[next].Line is { LineIndex: > 0 } line) {
                var firstLineHere = next;
                while (firstLineHere - 1 >= firstContent && flow[firstLineHere - 1].Line is { } before && before.ParagraphKey == line.ParagraphKey)
                    firstLineHere--;

                var onThisPage = next - firstLineHere;
                var remaining = line.LineCount - line.LineIndex;
                var widows = flow[next].Widows;
                var orphans = flow[next].Orphans;

                if (remaining < widows) {
                    var moved = next - (widows - remaining);
                    if (moved - firstLineHere >= orphans && moved > firstContent)
                        breakAt = moved;
                    else if (firstLineHere > firstContent)
                        breakAt = firstLineHere;
                } else if (onThisPage < orphans && firstLineHere > firstContent) {
                    breakAt = firstLineHere;
                }
            }

            // Keep-with-next (headings): an item that must stay with what follows moves too.
            var candidate = breakAt;
            while (PreviousContent (flow, candidate, firstContent) is { } previous && flow[previous].KeepWithNext && previous > firstContent) {
                candidate = previous;
                while (PreviousContent (flow, candidate, firstContent) is { } inGroup && flow[candidate].Group != 0 && flow[inGroup].Group == flow[candidate].Group)
                    candidate = inGroup;
            }

            if (candidate > firstContent)
                return candidate;
            return breakAt > firstContent ? breakAt : next;
        }

        /// <summary>The nearest content item before <paramref name="index"/>, not before <paramref name="floor"/>.</summary>
        private static int? PreviousContent (List<FlowItem> flow, int index, int floor)
        {
            for (var k = index - 1; k >= floor; k--)
                if (flow[k].Kind == FlowKind.Content)
                    return k;
            return null;
        }
    }
}
