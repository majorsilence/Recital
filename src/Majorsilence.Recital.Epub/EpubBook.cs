using System.Xml.Linq;
using AngleSharp.Dom;

namespace Majorsilence.Recital.Epub
{
    /// <summary>
    /// An open EPUB (2 or 3). Opening is forgiving: anything short of "not a ZIP" or "no package document"
    /// is recorded in <see cref="Warnings"/> and worked around. Holds the archive open until disposed.
    /// </summary>
    public sealed class EpubBook : IAsyncDisposable
    {
        private readonly EpubArchive archive;
        private readonly Dictionary<string, string> encryption;
        private readonly List<EpubWarning> warnings;
        private readonly Lock warningsLock = new ();

        private EpubBook (EpubArchive archive, EpubPackage package, Dictionary<string, string> encryption, List<EpubWarning> warnings)
        {
            this.archive = archive;
            this.encryption = encryption;
            this.warnings = warnings;
            Package = package;
        }

        public EpubPackage Package { get; }
        public EpubMetadata Metadata => Package.Metadata;
        public IReadOnlyList<SpineItem> Spine => Package.Spine;

        public IReadOnlyList<TocEntry> TableOfContents { get; private set; } = [];
        public EpubTocSource TableOfContentsSource { get; private set; }

        /// <summary>The cover image's manifest item, if one could be found.</summary>
        public ManifestItem? Cover { get; private set; }

        public IReadOnlyList<EmbeddedFont> Fonts { get; private set; } = [];

        /// <summary>
        /// True when content documents are encrypted with something other than font obfuscation (real DRM).
        /// Such a book opens, but its text cannot be read.
        /// </summary>
        public bool IsDrmProtected { get; private set; }

        public IReadOnlyList<EpubWarning> Warnings {
            get {
                lock (warningsLock)
                    return warnings.ToList ();
            }
        }

        public static async Task<EpubBook> OpenAsync (string path, CancellationToken ct = default)
        {
            var stream = new FileStream (path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            try {
                return await OpenAsync (stream, leaveOpen: false, ct).ConfigureAwait (false);
            } catch {
                await stream.DisposeAsync ().ConfigureAwait (false);
                throw;
            }
        }

        public static async Task<EpubBook> OpenAsync (Stream stream, bool leaveOpen = false, CancellationToken ct = default)
        {
            var archive = await EpubArchive.OpenAsync (stream, leaveOpen, ct).ConfigureAwait (false);

            try {
                var warnings = new List<EpubWarning> ();
                var packagePath = await FindPackageAsync (archive, warnings, ct).ConfigureAwait (false);

                var encryption = archive.Exists ("META-INF/encryption.xml")
                    ? EpubFonts.ReadEncryption (await ReadXmlAsync (archive, "META-INF/encryption.xml", ct).ConfigureAwait (false))
                    : new Dictionary<string, string> ();

                var opf = await ReadXmlAsync (archive, packagePath, ct).ConfigureAwait (false);
                var package = EpubPackage.Parse (opf, packagePath, p => archive.Find (p, warnings) is not null, warnings);

                var book = new EpubBook (archive, package, encryption, warnings);
                await book.LoadTableOfContentsAsync (ct).ConfigureAwait (false);
                book.Cover = await book.FindCoverAsync (ct).ConfigureAwait (false);
                book.Fonts = book.FindFonts ();
                book.IsDrmProtected = package.Spine.Any (s => s.Content.Path is { } p && book.IsEncrypted (p));

                if (book.IsDrmProtected)
                    warnings.Add (new EpubWarning ("drm", "The book's content is encrypted (DRM) and cannot be read."));
                if (package.Spine.Count == 0)
                    warnings.Add (new EpubWarning ("empty-spine", "The book has no readable content documents."));

                return book;
            } catch {
                await archive.DisposeAsync ().ConfigureAwait (false);
                throw;
            }
        }

        /// <summary>
        /// Reads a resource's bytes, undoing font obfuscation. Throws <see cref="EpubException"/> for a
        /// resource encrypted with real DRM.
        /// </summary>
        public async Task<byte[]> ReadResourceAsync (ManifestItem item, CancellationToken ct = default)
        {
            if (item.Path is null)
                throw new EpubException ($"'{item.Href}' is a remote resource.");

            return await ReadResourceAsync (item.Path, ct).ConfigureAwait (false);
        }

        public async Task<byte[]> ReadResourceAsync (string path, CancellationToken ct = default)
        {
            var actual = archive.Find (path) ?? throw new FileNotFoundException ($"The book has no file '{path}'.", path);
            var bytes = await archive.ReadAsync (actual, ct).ConfigureAwait (false);

            if (!encryption.TryGetValue (actual, out var algorithm))
                return bytes;

            var scheme = EpubFonts.FromAlgorithm (algorithm);
            if (scheme == FontObfuscation.None)
                throw new EpubException ($"'{path}' is encrypted ({algorithm}) and cannot be read.");

            if (!EpubFonts.Deobfuscate (bytes, scheme, Metadata.UniqueIdentifier ?? string.Empty))
                AddWarning (new EpubWarning ("font-key", $"Could not derive the {scheme} obfuscation key for '{path}'; returned as stored."));

            return bytes;
        }

        public bool Exists (string path) => archive.Exists (path);

        /// <summary>Parses the content document of one spine item.</summary>
        public async Task<ContentDocument> LoadContentDocumentAsync (int spineIndex, CancellationToken ct = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative (spineIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual (spineIndex, Spine.Count);

            var spineItem = Spine[spineIndex];
            var path = spineItem.Content.Path!;
            var bytes = await ReadResourceAsync (path, ct).ConfigureAwait (false);

            var documentWarnings = new List<EpubWarning> ();
            var (document, parsedAsXml) = XhtmlLoader.Load (bytes, path, documentWarnings);
            foreach (var warning in documentWarnings)
                AddWarning (warning);

            return new ContentDocument (Package, spineItem, document, parsedAsXml);
        }

        /// <summary>
        /// The spine index a CFI points into, or null when its package steps do not name a spine item.
        /// When the itemref id assertion disagrees with the step, the id wins.
        /// </summary>
        public int? ResolveSpineIndex (EpubCfi cfi) => ResolveSpineIndex (cfi.Start);

        public int? ResolveSpineIndex (CfiPoint point)
        {
            var steps = point.PackageSteps.ToList ();
            if (steps.Count < 2 || steps[0].Index != Package.SpineCfiStep)
                return null;

            var step = steps[1];
            var byStep = Spine.FirstOrDefault (s => s.CfiStep == step.Index);

            if (step.Id is { Length: > 0 } id && byStep?.ItemRefId != id)
                return Spine.FirstOrDefault (s => s.ItemRefId == id)?.Index ?? byStep?.Index;

            return byStep?.Index;
        }

        /// <summary>Loads the document a CFI points into and resolves the position in it.</summary>
        public async Task<(ContentDocument Document, DomPosition Position)?> ResolveAsync (EpubCfi cfi, CancellationToken ct = default)
        {
            if (ResolveSpineIndex (cfi) is not { } index)
                return null;

            var document = await LoadContentDocumentAsync (index, ct).ConfigureAwait (false);
            return document.Resolve (cfi) is { } position ? (document, position) : null;
        }

        public ValueTask DisposeAsync () => archive.DisposeAsync ();

        private bool IsEncrypted (string path)
            => encryption.TryGetValue (path, out var algorithm) && EpubFonts.FromAlgorithm (algorithm) == FontObfuscation.None;

        private void AddWarning (EpubWarning warning)
        {
            lock (warningsLock)
                warnings.Add (warning);
        }

        private static async Task<string> FindPackageAsync (EpubArchive archive, List<EpubWarning> warnings, CancellationToken ct)
        {
            if (archive.Exists ("META-INF/container.xml")) {
                try {
                    var container = await ReadXmlAsync (archive, "META-INF/container.xml", ct).ConfigureAwait (false);
                    var rootfiles = container.Descendants ().Where (e => e.Name.LocalName == "rootfile").ToList ();
                    var rootfile = rootfiles.FirstOrDefault (r => r.Attribute ("media-type")?.Value == "application/oebps-package+xml")
                        ?? rootfiles.FirstOrDefault ();

                    if (rootfile?.Attribute ("full-path")?.Value is { } fullPath
                        && archive.Find (EpubPaths.Normalize (Uri.UnescapeDataString (fullPath)), warnings) is { } found)
                        return found;

                    warnings.Add (new EpubWarning ("container-rootfile", "container.xml names no package document that exists."));
                } catch (System.Xml.XmlException ex) {
                    warnings.Add (new EpubWarning ("container-invalid", $"container.xml is not valid XML: {ex.Message}"));
                }
            } else {
                warnings.Add (new EpubWarning ("container-missing", "META-INF/container.xml is missing."));
            }

            var candidates = archive.Paths.Where (p => p.EndsWith (".opf", StringComparison.OrdinalIgnoreCase)).OrderBy (p => p.Length).ToList ();
            if (candidates.Count == 0)
                throw new EpubException ("Not an EPUB: there is no package document (.opf).");

            warnings.Add (new EpubWarning ("package-guessed", $"Using '{candidates[0]}' as the package document."));
            return candidates[0];
        }

        private static async Task<XDocument> ReadXmlAsync (EpubArchive archive, string path, CancellationToken ct)
        {
            var bytes = await archive.ReadAsync (path, ct).ConfigureAwait (false);
            try {
                return EpubText.ParseXml (bytes);
            } catch (System.Xml.XmlException ex) when (path.EndsWith (".opf", StringComparison.OrdinalIgnoreCase)) {
                throw new EpubException ($"The package document '{path}' is not valid XML: {ex.Message}", ex);
            }
        }

        private async Task LoadTableOfContentsAsync (CancellationToken ct)
        {
            if (Package.Nav is { Exists: true, Path: { } navPath } && !IsEncrypted (navPath)) {
                var bytes = await ReadResourceAsync (navPath, ct).ConfigureAwait (false);
                var (document, _) = XhtmlLoader.Load (bytes, navPath, warnings);
                var entries = TocParser.ParseNav (document, navPath, Package);

                if (entries.Count > 0) {
                    TableOfContents = entries;
                    TableOfContentsSource = EpubTocSource.Nav;
                    return;
                }

                warnings.Add (new EpubWarning ("nav-empty", "The navigation document has no table of contents."));
            }

            if (Package.Ncx is { Exists: true, Path: { } ncxPath } && !IsEncrypted (ncxPath)) {
                try {
                    var entries = TocParser.ParseNcx (await ReadXmlAsync (archive, ncxPath, ct).ConfigureAwait (false), ncxPath, Package);
                    if (entries.Count > 0) {
                        TableOfContents = entries;
                        TableOfContentsSource = EpubTocSource.Ncx;
                        return;
                    }
                } catch (System.Xml.XmlException ex) {
                    warnings.Add (new EpubWarning ("ncx-invalid", $"The NCX is not valid XML: {ex.Message}"));
                }
            }

            warnings.Add (new EpubWarning ("no-toc", "The book has no usable table of contents."));
            TableOfContentsSource = EpubTocSource.None;
        }

        private async Task<ManifestItem?> FindCoverAsync (CancellationToken ct)
        {
            static bool Usable (ManifestItem? item) => item is { Exists: true, IsImage: true };

            // EPUB 3: the cover-image property.
            var cover = Package.Manifest.FirstOrDefault (m => m.Properties.Contains ("cover-image"));
            if (Usable (cover))
                return cover;

            // EPUB 2: <meta name="cover" content="manifest-id"> (some books put an href there instead).
            var opf = await ReadXmlAsync (archive, Package.Path, ct).ConfigureAwait (false);
            var meta = opf.Descendants (EpubPackage.Opf + "meta").FirstOrDefault (m => m.Attribute ("name")?.Value == "cover")?.Attribute ("content")?.Value;
            if (meta is not null) {
                cover = Package.FindById (meta)
                    ?? Package.FindByPath (EpubPaths.Resolve (EpubPaths.Directory (Package.Path), meta).Path ?? string.Empty);
                if (Usable (cover))
                    return cover;
            }

            // EPUB 2 guide: <reference type="cover">, an image or a page holding one.
            foreach (var (type, href) in Package.Guide.Where (g => g.Type.Equals ("cover", StringComparison.OrdinalIgnoreCase))) {
                var (path, _) = EpubPaths.Resolve (EpubPaths.Directory (Package.Path), href);
                var item = path is null ? null : Package.FindByPath (path);

                if (Usable (item))
                    return item;

                if (item is { Exists: true, IsXhtml: true, Path: { } pagePath } && await FirstImageAsync (pagePath, ct).ConfigureAwait (false) is { } image)
                    return image;
            }

            // Last resort: an image whose id or file name says "cover".
            return Package.Manifest.FirstOrDefault (m => Usable (m)
                && (m.Id.Contains ("cover", StringComparison.OrdinalIgnoreCase) || m.Href.Contains ("cover", StringComparison.OrdinalIgnoreCase)));
        }

        private async Task<ManifestItem?> FirstImageAsync (string pagePath, CancellationToken ct)
        {
            var bytes = await ReadResourceAsync (pagePath, ct).ConfigureAwait (false);
            var (document, _) = XhtmlLoader.Load (bytes, pagePath, null);
            var directory = EpubPaths.Directory (pagePath);

            foreach (var element in document.QuerySelectorAll ("img, image")) {
                var src = element.GetAttribute ("src")
                    ?? element.GetAttribute ("http://www.w3.org/1999/xlink", "href")
                    ?? element.GetAttribute ("href");
                if (src is null)
                    continue;

                var (path, _) = EpubPaths.Resolve (directory, src);
                if (path is not null && Package.FindByPath (path) is { Exists: true, IsImage: true } image)
                    return image;
            }

            return null;
        }

        private List<EmbeddedFont> FindFonts ()
            => Package.Manifest
                .Where (m => m.IsFont && m.Exists)
                .Select (m => new EmbeddedFont {
                    Item = m,
                    Obfuscation = m.Path is not null && encryption.TryGetValue (m.Path, out var algorithm) ? EpubFonts.FromAlgorithm (algorithm) : FontObfuscation.None,
                })
                .ToList ();
    }
}
