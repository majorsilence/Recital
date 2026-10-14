using System.Globalization;
using System.Text;
using Majorsilence.Recital.Layout.Boxes;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Fonts;
using Majorsilence.Recital.Layout.Text;

namespace Majorsilence.Recital.Layout.Tests
{
    /// <summary>A readable JSON dump of a styled box tree, for golden-file tests.</summary>
    internal static class BoxTreeJson
    {
        public static string Write (Box root)
        {
            var json = new StringBuilder ();
            Write (json, root, 0);
            json.Append ('\n');
            return json.ToString ();
        }

        private static void Write (StringBuilder json, Box box, int depth)
        {
            var indent = new string (' ', depth * 2);
            var style = box.Style;
            json.Append (indent).Append ("{ \"box\": ").Append (Snapshot.Quote (Kind (box)));

            if (box.Element is { } element && box is not ParagraphBox)
                json.Append (", \"element\": ").Append (Snapshot.Quote (element.LocalName + (string.IsNullOrEmpty (element.ClassName) ? "" : "." + element.ClassName)));

            if (box is not ParagraphBox) {
                json.Append (", \"style\": ").Append (Snapshot.Quote (Describe (style)));
            }

            switch (box) {
                case BlockBox block:
                    if (block.Marker is { } marker)
                        json.Append (", \"marker\": ").Append (Snapshot.Quote (marker));
                    Children (json, block.Children, depth);
                    break;

                case ParagraphBox paragraph:
                    var shaped = ShapedParagraph.Create (paragraph, FontLibrary.Bundled);
                    json.Append (", \"text\": ").Append (Snapshot.Quote (shaped.Text))
                        .Append (", \"align\": ").Append (Snapshot.Quote (paragraph.Style.TextAlign.ToString ()))
                        .Append (", \"indent\": ").Append (Snapshot.Quote (paragraph.Style.TextIndent.ToString ()))
                        .Append (", \"runs\": [").Append (string.Join (", ", paragraph.Items.Select (Item))).Append (']');
                    if (paragraph.Marker is { } paragraphMarker)
                        json.Append (", \"marker\": ").Append (Snapshot.Quote (paragraphMarker));
                    json.Append (" }");
                    break;

                case ImageBox image:
                    json.Append (", \"src\": ").Append (Snapshot.Quote (image.Path))
                        .Append (", \"size\": ").Append (Snapshot.Quote ($"{image.IntrinsicWidth}x{image.IntrinsicHeight}")).Append (" }");
                    break;

                case TableBox table:
                    json.Append (", \"rows\": [\n");
                    for (var r = 0; r < table.Rows.Count; r++) {
                        json.Append (indent).Append ("  [\n");
                        var cells = table.Rows[r].Cells;
                        for (var c = 0; c < cells.Count; c++) {
                            Write (json, cells[c], depth + 2);
                            json.Append (c < cells.Count - 1 ? ",\n" : "\n");
                        }
                        json.Append (indent).Append ("  ]").Append (r < table.Rows.Count - 1 ? ",\n" : "\n");
                    }
                    json.Append (indent).Append ("] }");
                    break;
            }
        }

        private static void Children (StringBuilder json, List<Box> children, int depth)
        {
            if (children.Count == 0) {
                json.Append (" }");
                return;
            }

            json.Append (", \"children\": [\n");
            for (var i = 0; i < children.Count; i++) {
                Write (json, children[i], depth + 1);
                json.Append (i < children.Count - 1 ? ",\n" : "\n");
            }
            json.Append (new string (' ', depth * 2)).Append ("] }");
        }

        private static string Kind (Box box) => box switch {
            BlockBox { Element: null } => "anonymous",
            BlockBox => "block",
            ParagraphBox => "paragraph",
            ImageBox => "image",
            TableBox => "table",
            _ => box.GetType ().Name,
        };

        private static string Item (InlineItem item) => Snapshot.Quote (item switch {
            TextItem text => $"{(text.Node.ParentElement?.LocalName ?? "?")} {Font (text.Style)}",
            LineBreakItem => "br",
            _ => "?",
        });

        private static string Font (ComputedStyle style)
            => string.Create (CultureInfo.InvariantCulture, $"{style.FontSize:0.##}px {style.FontWeight}{(style.Italic ? " italic" : "")}{(style.SmallCaps ? " small-caps" : "")}");

        private static string Describe (ComputedStyle style)
        {
            var text = FormattableString.Invariant ($"{style.Display} {Font (style)} margin {style.MarginTop} {style.MarginRight} {style.MarginBottom} {style.MarginLeft}");
            if (style.PaddingLeft.Resolve (0) != 0)
                text += FormattableString.Invariant ($" padding-left {style.PaddingLeft}");
            if (style.BorderTopWidth != 0)
                text += FormattableString.Invariant ($" border-top {style.BorderTopWidth:0.##}px");
            if (style.BreakBefore != BreakRule.Auto)
                text += $" break-before {style.BreakBefore}";
            if (style.BreakAfter != BreakRule.Auto)
                text += $" break-after {style.BreakAfter}";
            return text;
        }
    }
}
