using System.Xml.Linq;

namespace Majorsilence.Recital.Epub
{
    /// <summary>The parsed package document (OPF): metadata, manifest and spine.</summary>
    public sealed class EpubPackage
    {
        internal static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
        internal static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

        /// <summary>Archive path of the package document.</summary>
        public required string Path { get; init; }

        /// <summary>The package's <c>version</c> attribute ("2.0", "3.0", ...).</summary>
        public required string Version { get; init; }

        public required EpubMetadata Metadata { get; init; }
        public required IReadOnlyList<ManifestItem> Manifest { get; init; }
        public required IReadOnlyList<SpineItem> Spine { get; init; }

        /// <summary>The spine element's CFI step under the package element (6 when it is the third child element).</summary>
        public required int SpineCfiStep { get; init; }

        /// <summary>The manifest item of the EPUB 2 NCX the spine's <c>toc</c> attribute names, if any.</summary>
        public ManifestItem? Ncx { get; init; }

        /// <summary>The EPUB 3 navigation document (manifest item with the <c>nav</c> property), if any.</summary>
        public ManifestItem? Nav { get; init; }

        /// <summary>EPUB 2 guide references (type, href), kept for cover detection and "start reading" hints.</summary>
        public IReadOnlyList<(string Type, string Href)> Guide { get; init; } = [];

        public ManifestItem? FindById (string id) => Manifest.FirstOrDefault (m => m.Id == id);
        public ManifestItem? FindByPath (string path) => Manifest.FirstOrDefault (m => m.Path == path);

        public int? SpineIndexOf (string path)
        {
            foreach (var item in Spine)
                if (item.Content.Path == path || item.Item.Path == path)
                    return item.Index;
            return null;
        }

        internal static EpubPackage Parse (XDocument document, string path, Func<string, bool> exists, List<EpubWarning> warnings)
        {
            var package = document.Root ?? throw new EpubException ($"'{path}' is empty.");
            var directory = EpubPaths.Directory (path);

            var manifest = ParseManifest (package, directory, exists, warnings);
            var byId = new Dictionary<string, ManifestItem> (StringComparer.Ordinal);
            foreach (var item in manifest)
                byId.TryAdd (item.Id, item);

            var spineElement = package.Element (Opf + "spine");
            var spineCfiStep = 0;

            if (spineElement is null) {
                warnings.Add (new EpubWarning ("no-spine", "The package has no spine."));
            } else {
                spineCfiStep = CfiStepOf (spineElement);
            }

            var spine = ParseSpine (spineElement, byId, warnings);

            var ncxId = spineElement?.Attribute ("toc")?.Value;
            var ncx = ncxId is not null && byId.TryGetValue (ncxId, out var ncxItem) ? ncxItem : null;
            ncx ??= manifest.FirstOrDefault (m => m.MediaType == "application/x-dtbncx+xml");

            var nav = manifest.FirstOrDefault (m => m.Properties.Contains ("nav"));

            var guide = package.Element (Opf + "guide")?.Elements (Opf + "reference")
                .Select (r => (Type: r.Attribute ("type")?.Value ?? string.Empty, Href: r.Attribute ("href")?.Value ?? string.Empty))
                .Where (r => r.Href.Length > 0)
                .ToList () ?? [];

            return new EpubPackage {
                Path = path,
                Version = package.Attribute ("version")?.Value ?? "2.0",
                Metadata = ParseMetadata (package, warnings),
                Manifest = manifest,
                Spine = spine,
                SpineCfiStep = spineCfiStep,
                Ncx = ncx,
                Nav = nav,
                Guide = guide,
            };
        }

        /// <summary>An element's CFI step among its parent's child elements: 2, 4, 6, ...</summary>
        private static int CfiStepOf (XElement element)
            => 2 * (element.ElementsBeforeSelf ().Count () + 1);

        private static List<ManifestItem> ParseManifest (XElement package, string directory, Func<string, bool> exists, List<EpubWarning> warnings)
        {
            var items = new List<ManifestItem> ();
            var seen = new HashSet<string> (StringComparer.Ordinal);
            var manifest = package.Element (Opf + "manifest");

            if (manifest is null) {
                warnings.Add (new EpubWarning ("no-manifest", "The package has no manifest."));
                return items;
            }

            foreach (var element in manifest.Elements (Opf + "item")) {
                var id = element.Attribute ("id")?.Value;
                var href = element.Attribute ("href")?.Value;

                if (string.IsNullOrEmpty (id) || string.IsNullOrEmpty (href)) {
                    warnings.Add (new EpubWarning ("manifest-item-invalid", $"Manifest item without id or href skipped: {element}"));
                    continue;
                }

                if (!seen.Add (id)) {
                    warnings.Add (new EpubWarning ("manifest-duplicate-id", $"Duplicate manifest id '{id}'; the first one is used."));
                    continue;
                }

                var (path, _) = EpubPaths.Resolve (directory, href);
                var itemExists = path is not null && exists (path);

                if (path is not null && !itemExists)
                    warnings.Add (new EpubWarning ("manifest-missing-file", $"Manifest item '{id}' points at '{path}', which is not in the archive."));

                items.Add (new ManifestItem {
                    Id = id,
                    Href = href,
                    Path = path,
                    MediaType = (element.Attribute ("media-type")?.Value ?? string.Empty).Trim ().ToLowerInvariant (),
                    Properties = new HashSet<string> (SplitTokens (element.Attribute ("properties")?.Value), StringComparer.Ordinal),
                    FallbackId = element.Attribute ("fallback")?.Value,
                    Exists = itemExists,
                });
            }

            return items;
        }

        private static List<SpineItem> ParseSpine (XElement? spineElement, Dictionary<string, ManifestItem> byId, List<EpubWarning> warnings)
        {
            var spine = new List<SpineItem> ();
            if (spineElement is null)
                return spine;

            var step = 0;

            // Every itemref counts toward the CFI step, including ones skipped below, so a step always
            // means the same itemref as in the package document itself.
            foreach (var itemref in spineElement.Elements ()) {
                step += 2;

                if (itemref.Name != EpubPackage.Opf + "itemref")
                    continue;

                var idref = itemref.Attribute ("idref")?.Value;
                if (idref is null || !byId.TryGetValue (idref, out var item)) {
                    warnings.Add (new EpubWarning ("spine-unknown-idref", $"Spine itemref '{idref}' names no manifest item; skipped."));
                    continue;
                }

                var content = ResolveFallback (item, byId);
                if (content is null) {
                    warnings.Add (new EpubWarning ("spine-not-xhtml", $"Spine item '{idref}' ({item.MediaType}) has no XHTML fallback; skipped."));
                    continue;
                }

                if (!content.Exists) {
                    warnings.Add (new EpubWarning ("spine-missing-file", $"Spine item '{idref}' has no file in the archive; skipped."));
                    continue;
                }

                spine.Add (new SpineItem {
                    Index = spine.Count,
                    Item = item,
                    Content = content,
                    Linear = itemref.Attribute ("linear")?.Value != "no",
                    ItemRefId = itemref.Attribute ("id")?.Value,
                    CfiStep = step,
                });
            }

            return spine;
        }

        /// <summary>The item itself if it is XHTML, else the first XHTML item along its fallback chain.</summary>
        private static ManifestItem? ResolveFallback (ManifestItem item, Dictionary<string, ManifestItem> byId)
        {
            var visited = new HashSet<string> (StringComparer.Ordinal);
            var current = item;

            while (current is not null && visited.Add (current.Id)) {
                if (current.IsXhtml || current.MediaType.Length == 0 && current.Href.EndsWith ("html", StringComparison.OrdinalIgnoreCase))
                    return current;

                current = current.FallbackId is not null && byId.TryGetValue (current.FallbackId, out var next) ? next : null;
            }

            return null;
        }

        private static EpubMetadata ParseMetadata (XElement package, List<EpubWarning> warnings)
        {
            var metadata = package.Element (Opf + "metadata");

            if (metadata is null) {
                warnings.Add (new EpubWarning ("no-metadata", "The package has no metadata."));
                return new EpubMetadata ();
            }

            // EPUB 2 sometimes wraps everything in dc-metadata / x-metadata.
            var all = metadata.Descendants ().ToList ();

            // EPUB 3 <meta refines="#id" property="...">value</meta>; a property can repeat (several roles).
            var refinements = all
                .Where (e => e.Name == Opf + "meta" && e.Attribute ("refines") is not null && e.Attribute ("property") is not null)
                .GroupBy (e => e.Attribute ("refines")!.Value.TrimStart ('#'))
                .ToDictionary (
                    g => g.Key,
                    g => g.GroupBy (e => e.Attribute ("property")!.Value).ToDictionary (p => p.Key, p => p.Select (e => e.Value.Trim ()).ToList ()));

            List<string> RefinedAll (XElement element, string property)
            {
                var id = element.Attribute ("id")?.Value;
                return id is not null && refinements.TryGetValue (id, out var props) && props.TryGetValue (property, out var values) ? values : [];
            }

            string? Refined (XElement element, string property) => RefinedAll (element, property).FirstOrDefault ();

            List<XElement> Dc (string name) => all.Where (e => e.Name == EpubPackage.Dc + name && e.Value.Trim ().Length > 0).ToList ();

            var titles = Dc ("title");
            var mainTitle = titles.FirstOrDefault (t => Refined (t, "title-type") == "main") ?? titles.FirstOrDefault ();

            EpubCreator Creator (XElement e)
            {
                var roles = RefinedAll (e, "role");
                if (roles.Count == 0 && e.Attribute (Opf + "role")?.Value is { Length: > 0 } epub2Role)
                    roles = [epub2Role];

                return new EpubCreator (e.Value.Trim (), Refined (e, "file-as") ?? e.Attribute (Opf + "file-as")?.Value, roles);
            }

            var identifiers = Dc ("identifier");
            var uniqueId = package.Attribute ("unique-identifier")?.Value;
            var unique = identifiers.FirstOrDefault (i => uniqueId is not null && i.Attribute ("id")?.Value == uniqueId)
                ?? identifiers.FirstOrDefault ();

            if (uniqueId is not null && identifiers.All (i => i.Attribute ("id")?.Value != uniqueId))
                warnings.Add (new EpubWarning ("unique-identifier-missing", $"unique-identifier '{uniqueId}' names no dc:identifier."));

            var modified = all.FirstOrDefault (e => e.Name == Opf + "meta" && e.Attribute ("property")?.Value == "dcterms:modified")?.Value.Trim ();
            var languages = Dc ("language").Select (e => e.Value.Trim ()).ToList ();

            return new EpubMetadata {
                Title = mainTitle?.Value.Trim () ?? string.Empty,
                Titles = titles.Select (t => t.Value.Trim ()).ToList (),
                Creators = Dc ("creator").Select (Creator).ToList (),
                Contributors = Dc ("contributor").Select (Creator).ToList (),
                Language = languages.FirstOrDefault (),
                Languages = languages,
                UniqueIdentifier = unique?.Value.Trim (),
                Identifiers = identifiers.Select (i => i.Value.Trim ()).ToList (),
                Publisher = Dc ("publisher").FirstOrDefault ()?.Value.Trim (),
                Date = Dc ("date").FirstOrDefault ()?.Value.Trim (),
                Modified = modified,
                Description = Dc ("description").FirstOrDefault ()?.Value.Trim (),
                Subjects = Dc ("subject").Select (s => s.Value.Trim ()).ToList (),
            };
        }

        private static string[] SplitTokens (string? value)
            => value?.Split ((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
    }
}
