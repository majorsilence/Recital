using System.Xml.Linq;
using AngleSharp.Dom;

namespace Majorsilence.Recital.Epub
{
    internal static class TocParser
    {
        private const string OpsNamespace = "http://www.idpf.org/2007/ops";

        /// <summary>
        /// The EPUB 3 navigation document's <c>nav[epub:type~=toc]</c> (or its first <c>nav</c> when none is
        /// typed): nested <c>ol &gt; li &gt; (a | span) + ol?</c>.
        /// </summary>
        public static List<TocEntry> ParseNav (IDocument document, string navPath, EpubPackage package)
        {
            var navs = document.GetElementsByTagName ("nav").ToList ();
            var toc = navs.FirstOrDefault (n => EpubTypes (n).Contains ("toc")) ?? navs.FirstOrDefault ();
            var list = toc?.Children.FirstOrDefault (c => c.LocalName == "ol");

            return list is null ? [] : ParseNavList (list, EpubPaths.Directory (navPath), navPath, package);
        }

        private static List<TocEntry> ParseNavList (IElement list, string directory, string navPath, EpubPackage package)
        {
            var entries = new List<TocEntry> ();

            foreach (var item in list.Children.Where (c => c.LocalName == "li")) {
                var label = item.Children.FirstOrDefault (c => c.LocalName is "a" or "span");
                var childList = item.Children.FirstOrDefault (c => c.LocalName == "ol");
                var children = childList is null ? [] : ParseNavList (childList, directory, navPath, package);

                if (label is null) {
                    entries.AddRange (children);
                    continue;
                }

                var title = Collapse (label.TextContent);
                if (title.Length == 0)
                    title = label.GetAttribute ("title") ?? string.Empty;

                entries.Add (Entry (title, label.LocalName == "a" ? label.GetAttribute ("href") : null, directory, navPath, package, children));
            }

            return entries;
        }

        /// <summary>The EPUB 2 NCX <c>navMap</c>: nested <c>navPoint</c>s with a label and a content src.</summary>
        public static List<TocEntry> ParseNcx (XDocument ncx, string ncxPath, EpubPackage package)
        {
            XNamespace ns = "http://www.daisy.org/z3986/2005/ncx/";
            var navMap = ncx.Root?.Element (ns + "navMap") ?? ncx.Root?.Elements ().FirstOrDefault (e => e.Name.LocalName == "navMap");

            return navMap is null ? [] : ParseNavPoints (navMap, EpubPaths.Directory (ncxPath), ncxPath, package);
        }

        private static List<TocEntry> ParseNavPoints (XElement parent, string directory, string ncxPath, EpubPackage package)
        {
            var entries = new List<TocEntry> ();

            foreach (var point in parent.Elements ().Where (e => e.Name.LocalName == "navPoint")) {
                var label = point.Elements ().FirstOrDefault (e => e.Name.LocalName == "navLabel")
                    ?.Elements ().FirstOrDefault (e => e.Name.LocalName == "text")?.Value;
                var src = point.Elements ().FirstOrDefault (e => e.Name.LocalName == "content")?.Attribute ("src")?.Value;

                entries.Add (Entry (Collapse (label ?? string.Empty), src, directory, ncxPath, package,
                    ParseNavPoints (point, directory, ncxPath, package)));
            }

            return entries;
        }

        private static TocEntry Entry (string title, string? href, string directory, string selfPath, EpubPackage package, List<TocEntry> children)
        {
            if (string.IsNullOrWhiteSpace (href))
                return new TocEntry { Title = title, Children = children };

            var (path, fragment) = EpubPaths.Resolve (directory, href, selfPath);

            return new TocEntry {
                Title = title,
                Href = href,
                Path = path,
                Fragment = fragment,
                SpineIndex = path is null ? null : package.SpineIndexOf (path),
                Children = children,
            };
        }

        /// <summary>Space-separated <c>epub:type</c> tokens, whether the attribute kept its namespace or not.</summary>
        internal static IEnumerable<string> EpubTypes (IElement element)
            => (element.GetAttribute (OpsNamespace, "type") ?? element.GetAttribute ("epub:type") ?? string.Empty)
                .Split ((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static string Collapse (string text)
            => string.Join (' ', text.Split ((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
