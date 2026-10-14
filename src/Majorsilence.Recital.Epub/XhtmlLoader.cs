using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AngleSharp.Xml.Parser;

namespace Majorsilence.Recital.Epub
{
    /// <summary>
    /// Loads an XHTML content document into an AngleSharp HTML document whose tree is exactly the XML tree.
    ///
    /// The XML parser gets the structure right (self-closing <c>&lt;a id="x"/&gt;</c>, no implied
    /// <c>tbody</c>), which CFIs depend on, but builds generic XML elements. Rebuilding that tree through
    /// the HTML document's DOM API gives real HTML/SVG/MathML element types, which Layout needs for CSS.
    /// Only a document that is not well-formed even after entity conversion goes through the forgiving
    /// HTML parser, whose tree may differ.
    /// </summary>
    internal static class XhtmlLoader
    {
        private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

        public static (IDocument Document, bool ParsedAsXml) Load (byte[] bytes, string path, List<EpubWarning>? warnings)
        {
            var text = EpubText.Decode (bytes);

            try {
                var xml = new XmlParser (new XmlParserOptions { IsSuppressingErrors = false })
                    .ParseDocument (EpubText.ReplaceHtmlEntities (text));
                return (Rebuild (xml), true);
            } catch (Exception ex) when (ex is AngleSharp.Xml.Parser.XmlParseException or DomException) {
                warnings?.Add (new EpubWarning ("xhtml-not-well-formed",
                    $"'{path}' is not well-formed XML ({ex.Message}); parsed as HTML, so its structure may differ from the source."));
                return (new HtmlParser ().ParseDocument (text), false);
            }
        }

        private static IDocument Rebuild (IDocument xml)
        {
            var html = new HtmlParser ().ParseDocument (string.Empty);

            while (html.FirstChild is { } child)
                html.RemoveChild (child);

            if (xml.DocumentElement is { } root)
                html.AppendChild (Import (html, root));

            return html;
        }

        private static INode Import (IDocument document, INode node)
        {
            switch (node) {
                case IElement element: {
                    var qualifiedName = string.IsNullOrEmpty (element.Prefix) ? element.LocalName : element.Prefix + ":" + element.LocalName;
                    var copy = document.CreateElement (NullIfEmpty (element.NamespaceUri), qualifiedName);

                    foreach (var attribute in element.Attributes) {
                        var ns = attribute.Name == "xmlns" || attribute.Name.StartsWith ("xmlns:", StringComparison.Ordinal)
                            ? XmlnsNamespace
                            : NullIfEmpty (attribute.NamespaceUri);
                        copy.SetAttribute (ns, attribute.Name, attribute.Value);
                    }

                    foreach (var child in element.ChildNodes)
                        copy.AppendChild (Import (document, child));

                    return copy;
                }

                // CDATA sections arrive as text; their characters count for CFI offsets like any text.
                case ICharacterData { NodeType: NodeType.Text or NodeType.CharacterData } text:
                    return document.CreateTextNode (text.Data);

                case IComment comment:
                    return document.CreateComment (comment.Data);

                // Processing instructions and anything else carry no content; keep a comment so sibling
                // text chunks stay split exactly where the source splits them.
                default:
                    return document.CreateComment (string.Empty);
            }
        }

        private static string? NullIfEmpty (string? value) => string.IsNullOrEmpty (value) ? null : value;
    }
}
