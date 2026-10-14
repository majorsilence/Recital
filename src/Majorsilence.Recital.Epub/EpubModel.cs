namespace Majorsilence.Recital.Epub
{
    /// <summary>A book that cannot be opened at all: not a ZIP, or no package document anywhere.</summary>
    public sealed class EpubException : Exception
    {
        public EpubException (string message) : base (message)
        {
        }

        public EpubException (string message, Exception inner) : base (message, inner)
        {
        }
    }

    /// <summary>
    /// Something wrong with a book that was worked around rather than failing the open: a missing file,
    /// a broken manifest entry, a content document that needed the forgiving HTML parser, and so on.
    /// </summary>
    public sealed record EpubWarning (string Code, string Message);

    public enum EpubTocSource
    {
        /// <summary>No usable table of contents; show the spine instead.</summary>
        None,
        /// <summary>The EPUB 3 navigation document's <c>nav[epub:type=toc]</c>.</summary>
        Nav,
        /// <summary>The EPUB 2 NCX <c>navMap</c>.</summary>
        Ncx,
    }

    /// <summary>A <c>dc:creator</c> or <c>dc:contributor</c>, with its MARC relator roles (one person can have several).</summary>
    public sealed record EpubCreator (string Name, string? FileAs, IReadOnlyList<string> Roles)
    {
        public string? Role => Roles.Count > 0 ? Roles[0] : null;
    }

    public sealed class EpubMetadata
    {
        public string Title { get; init; } = string.Empty;
        public IReadOnlyList<string> Titles { get; init; } = [];
        public IReadOnlyList<EpubCreator> Creators { get; init; } = [];
        public IReadOnlyList<EpubCreator> Contributors { get; init; } = [];
        public string? Language { get; init; }
        public IReadOnlyList<string> Languages { get; init; } = [];

        /// <summary>The <c>dc:identifier</c> the package's <c>unique-identifier</c> attribute names.</summary>
        public string? UniqueIdentifier { get; init; }

        public IReadOnlyList<string> Identifiers { get; init; } = [];
        public string? Publisher { get; init; }
        public string? Date { get; init; }
        public string? Modified { get; init; }
        public string? Description { get; init; }
        public IReadOnlyList<string> Subjects { get; init; } = [];

        /// <summary>Creators with the author role, or with no role at all (the usual case).</summary>
        public IEnumerable<EpubCreator> Authors => Creators.Where (c => c.Roles.Count == 0 || c.Roles.Contains ("aut"));
    }

    public sealed class ManifestItem
    {
        public required string Id { get; init; }

        /// <summary>The <c>href</c> exactly as written in the package document.</summary>
        public required string Href { get; init; }

        /// <summary>Archive path the href resolves to, or null for a remote resource.</summary>
        public required string? Path { get; init; }

        public required string MediaType { get; init; }
        public IReadOnlySet<string> Properties { get; init; } = new HashSet<string> ();
        public string? FallbackId { get; init; }

        /// <summary>False when the archive has no file at <see cref="Path"/>.</summary>
        public bool Exists { get; init; }

        public bool IsXhtml => MediaType is "application/xhtml+xml" or "text/html";
        public bool IsImage => MediaType.StartsWith ("image/", StringComparison.Ordinal);
        public bool IsFont => EpubFonts.IsFontMediaType (MediaType) || EpubFonts.HasFontExtension (Href);

        public override string ToString () => $"{Id} ({MediaType}) {Href}";
    }

    public sealed class SpineItem
    {
        public required int Index { get; init; }
        public required ManifestItem Item { get; init; }

        /// <summary>
        /// The item to render: <see cref="Item"/>, or the first XHTML item reached through its manifest
        /// <c>fallback</c> chain when <see cref="Item"/> is not XHTML.
        /// </summary>
        public required ManifestItem Content { get; init; }

        public bool Linear { get; init; } = true;

        /// <summary>The <c>itemref</c>'s own <c>id</c>, used as the CFI step assertion.</summary>
        public string? ItemRefId { get; init; }

        /// <summary>This itemref's CFI step under the spine element (2 for the first itemref, 4, 6, ...).</summary>
        public required int CfiStep { get; init; }
    }

    public sealed class TocEntry
    {
        public required string Title { get; init; }

        /// <summary>The link as written, or null for a heading with no link.</summary>
        public string? Href { get; init; }

        /// <summary>Archive path of the target document.</summary>
        public string? Path { get; init; }

        /// <summary>The <c>#fragment</c> within the target, without the '#'.</summary>
        public string? Fragment { get; init; }

        /// <summary>Spine position of the target document, or null when it is not in the spine.</summary>
        public int? SpineIndex { get; init; }

        public IReadOnlyList<TocEntry> Children { get; init; } = [];

        public override string ToString () => Title;
    }

    public sealed class EmbeddedFont
    {
        public required ManifestItem Item { get; init; }

        /// <summary>Font obfuscation that <see cref="EpubBook.ReadResourceAsync(ManifestItem, CancellationToken)"/> undoes, if any.</summary>
        public FontObfuscation Obfuscation { get; init; }
    }

    public enum FontObfuscation
    {
        None,
        /// <summary>IDPF font obfuscation (<c>http://www.idpf.org/2008/embedding</c>): first 1040 bytes.</summary>
        Idpf,
        /// <summary>Adobe font mangling (<c>http://ns.adobe.com/pdf/enc#RC</c>): first 1024 bytes.</summary>
        Adobe,
    }
}
