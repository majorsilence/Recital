using AngleSharp.Dom;
using Majorsilence.Recital.Layout.Css;

namespace Majorsilence.Recital.Layout.Boxes
{
    /// <summary>Builds the styled box tree for one content document.</summary>
    internal sealed class BoxTreeBuilder
    {
        private const string SvgNamespace = "http://www.w3.org/2000/svg";
        private const string XlinkNamespace = "http://www.w3.org/1999/xlink";

        private readonly StyleResolver resolver;
        private readonly IReadOnlyDictionary<string, (float Width, float Height)> imageSizes;
        private readonly string directory;
        private float rootFontSize;

        public BoxTreeBuilder (StyleResolver resolver, IReadOnlyDictionary<string, (float, float)> imageSizes, string documentPath)
        {
            this.resolver = resolver;
            this.imageSizes = imageSizes;
            var slash = documentPath.LastIndexOf ('/');
            directory = slash < 0 ? string.Empty : documentPath[..slash];
        }

        /// <summary>Archive paths of every image the document shows, so their sizes can be read first.</summary>
        public static IEnumerable<string> ImagePaths (IDocument document, string documentPath)
        {
            var slash = documentPath.LastIndexOf ('/');
            var dir = slash < 0 ? string.Empty : documentPath[..slash];

            foreach (var element in document.QuerySelectorAll ("img, image"))
                if (ImageSource (element) is { } src && Resolve (dir, src) is { } path)
                    yield return path;
        }

        public BlockBox Build (IDocument document)
        {
            var html = document.DocumentElement;
            var htmlStyle = resolver.Compute (html, resolver.InitialStyle (), resolver.InitialStyle ().FontSize);
            rootFontSize = htmlStyle.FontSize;

            var root = new BlockBox (htmlStyle, html);
            ParagraphBox? paragraph = null;
            Visit (html, htmlStyle, root, ref paragraph);
            Flush (root, ref paragraph);
            return root;
        }

        private void Visit (IElement element, ComputedStyle style, BlockBox block, ref ParagraphBox? paragraph)
        {
            foreach (var node in element.ChildNodes) {
                if (node is IText text) {
                    if (text.Length > 0 && !style.Hidden)
                        Paragraph (block, ref paragraph).Items.Add (new TextItem (style, text));
                    continue;
                }

                if (node is not IElement child)
                    continue;

                var childStyle = resolver.Compute (child, style, rootFontSize);
                if (childStyle.Display == Display.None)
                    continue;

                if (IsImage (child)) {
                    Flush (block, ref paragraph);
                    if (Image (child, childStyle) is { } image)
                        block.Children.Add (image);
                    continue;
                }

                // Inline SVG without a raster image: not rendered (no SVG renderer yet), and its text is
                // not body text.
                if (child.NamespaceUri == SvgNamespace)
                    continue;

                if (child.LocalName == "br") {
                    Paragraph (block, ref paragraph).Items.Add (new LineBreakItem (childStyle, child));
                    continue;
                }

                if (!childStyle.IsBlockLevel) {
                    // Inline: its text joins the current paragraph; a block inside it splits the paragraph.
                    Visit (child, childStyle, block, ref paragraph);
                    continue;
                }

                Flush (block, ref paragraph);

                if (childStyle.Display == Display.Table) {
                    block.Children.Add (Table (child, childStyle));
                    continue;
                }

                var childBlock = new BlockBox (childStyle, child);
                if (childStyle.Display == Display.ListItem)
                    childBlock.Marker = Marker (child, childStyle);

                ParagraphBox? inner = null;
                Visit (child, childStyle, childBlock, ref inner);
                Flush (childBlock, ref inner);
                block.Children.Add (childBlock);
            }
        }

        private static ParagraphBox Paragraph (BlockBox block, ref ParagraphBox? paragraph)
            => paragraph ??= new ParagraphBox (block.Style, block.Element);

        /// <summary>Ends the current paragraph, dropping it when it holds nothing but collapsible whitespace.</summary>
        private static void Flush (BlockBox block, ref ParagraphBox? paragraph)
        {
            if (paragraph is null)
                return;

            var visible = paragraph.Items.Any (item => item switch {
                LineBreakItem => true,
                TextItem t => t.Style.WhiteSpace is WhiteSpace.Pre or WhiteSpace.PreWrap
                    ? t.Node.Length > 0
                    : t.Node.Data.Any (c => !IsCollapsible (c)),
                _ => false,
            });

            if (visible) {
                // The first paragraph of a list item carries its marker.
                if (block.Marker is { } marker && !block.Children.OfType<ParagraphBox> ().Any ())
                    paragraph.Marker = marker;
                block.Children.Add (paragraph);
            }

            paragraph = null;
        }

        private static bool IsCollapsible (char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        private TableBox Table (IElement table, ComputedStyle tableStyle)
        {
            var box = new TableBox (tableStyle, table);

            void AddRows (IElement parent, ComputedStyle parentStyle)
            {
                foreach (var child in parent.Children) {
                    var style = resolver.Compute (child, parentStyle, rootFontSize);
                    if (style.Display == Display.None)
                        continue;

                    if (style.Display == Display.TableRowGroup) {
                        AddRows (child, style);
                    } else if (style.Display == Display.TableRow) {
                        var row = new TableRowBox (style, child);
                        foreach (var cellElement in child.Children) {
                            var cellStyle = resolver.Compute (cellElement, style, rootFontSize);
                            if (cellStyle.Display == Display.None)
                                continue;
                            var cell = new BlockBox (cellStyle, cellElement);
                            ParagraphBox? paragraph = null;
                            Visit (cellElement, cellStyle, cell, ref paragraph);
                            Flush (cell, ref paragraph);
                            row.Cells.Add (cell);
                        }
                        if (row.Cells.Count > 0)
                            box.Rows.Add (row);
                    }
                }
            }

            AddRows (table, tableStyle);
            return box;
        }

        private static bool IsImage (IElement element)
            => element.LocalName == "img"
            || element.NamespaceUri == SvgNamespace && (element.LocalName == "image" || element.LocalName == "svg" && element.QuerySelector ("image") is not null);

        private ImageBox? Image (IElement element, ComputedStyle style)
        {
            // An <svg> wrapping an <image> (a common cover page shape) shows that image.
            var source = element.LocalName == "svg" ? element.QuerySelector ("image") : element;
            if (source is null || ImageSource (source) is not { } src || Resolve (directory, src) is not { } path)
                return null;
            if (!imageSizes.TryGetValue (path, out var size) || size.Width <= 0 || size.Height <= 0)
                return null;

            return new ImageBox (style, element, path, size.Width, size.Height);
        }

        private static string? ImageSource (IElement element)
            => element.GetAttribute ("src")
            ?? element.GetAttribute (XlinkNamespace, "href")
            ?? element.GetAttribute ("xlink:href")
            ?? element.GetAttribute ("href");

        private static string? Resolve (string dir, string href)
        {
            href = href.Trim ();
            if (href.Length == 0 || href.StartsWith ('#') || href.Contains (':', StringComparison.Ordinal))
                return null;

            var hash = href.IndexOf ('#');
            if (hash >= 0)
                href = href[..hash];

            var parts = new List<string> ();
            foreach (var segment in Uri.UnescapeDataString (dir.Length == 0 ? href : dir + "/" + href).Split ('/')) {
                if (segment is "" or ".")
                    continue;
                if (segment == "..") {
                    if (parts.Count > 0)
                        parts.RemoveAt (parts.Count - 1);
                    continue;
                }
                parts.Add (segment);
            }
            return string.Join ('/', parts);
        }

        private string? Marker (IElement item, ComputedStyle style)
        {
            var type = style.ListStyleType;
            if (type == "none")
                return null;

            if (type is "disc" or "circle" or "square")
                return type switch { "circle" => "◦", "square" => "▪", _ => "•" };

            // Ordinal: <li value>, else <ol start> plus the count of earlier list items.
            var number = 1;
            if (item.ParentElement is { } list) {
                number = int.TryParse (list.GetAttribute ("start"), out var start) ? start : 1;
                foreach (var sibling in list.Children) {
                    if (sibling == item)
                        break;
                    if (sibling.LocalName == "li")
                        number = int.TryParse (sibling.GetAttribute ("value"), out var v) ? v + 1 : number + 1;
                }
            }
            if (int.TryParse (item.GetAttribute ("value"), out var value))
                number = value;

            var text = type switch {
                "lower-alpha" or "lower-latin" => Alpha (number, 'a'),
                "upper-alpha" or "upper-latin" => Alpha (number, 'A'),
                "lower-roman" => Roman (number).ToLowerInvariant (),
                "upper-roman" => Roman (number),
                _ => number.ToString (System.Globalization.CultureInfo.InvariantCulture),
            };
            return text + ".";
        }

        private static string Alpha (int n, char first)
        {
            if (n < 1)
                return n.ToString (System.Globalization.CultureInfo.InvariantCulture);
            var result = string.Empty;
            while (n > 0) {
                n--;
                result = (char)(first + n % 26) + result;
                n /= 26;
            }
            return result;
        }

        private static string Roman (int n)
        {
            if (n is < 1 or > 3999)
                return n.ToString (System.Globalization.CultureInfo.InvariantCulture);
            (int Value, string Numeral)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
            var result = new System.Text.StringBuilder ();
            foreach (var (value, numeral) in table)
                while (n >= value) {
                    result.Append (numeral);
                    n -= value;
                }
            return result.ToString ();
        }
    }
}
