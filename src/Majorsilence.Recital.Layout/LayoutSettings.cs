namespace Majorsilence.Recital.Layout
{
    /// <summary>
    /// Everything a pagination depends on: the viewport and the reader's typography choices. Lengths are
    /// CSS pixels. Pages are cached per (chapter, settings), so this is a value type with equality.
    /// </summary>
    public sealed record LayoutSettings
    {
        public float PageWidth { get; init; } = 600;
        public float PageHeight { get; init; } = 800;
        public float MarginTop { get; init; } = 32;
        public float MarginBottom { get; init; } = 32;
        public float MarginLeft { get; init; } = 32;
        public float MarginRight { get; init; } = 32;

        /// <summary>
        /// The reader's base font size: what CSS <c>medium</c> (and so <c>1rem</c>) means. Absolute lengths
        /// in book CSS (px, pt, ...) scale with it relative to the CSS default of 16px, so a book that pins
        /// its body to 12pt still honours the reader's choice.
        /// </summary>
        public float FontSize { get; init; } = 18;

        /// <summary>Replaces every line-height with this factor; null keeps the book's.</summary>
        public float? LineHeight { get; init; }

        /// <summary>Replaces the font family of all text that is not monospace; null keeps the book's.</summary>
        public string? FontFamily { get; init; }

        /// <summary>True forces justified body text, false forces left-aligned; null keeps the book's.</summary>
        public bool? Justify { get; init; }

        /// <summary>Whether a book's own embedded fonts (<c>@font-face</c>) are used.</summary>
        public bool PublisherFonts { get; init; } = true;

        /// <summary>
        /// Whether the reader's theme is dark: the book's <c>@media (prefers-color-scheme: dark)</c> rules
        /// apply (Standard Ebooks inverts its black-on-transparent title pages there, for example).
        /// </summary>
        public bool DarkMode { get; init; }

        public int Widows { get; init; } = 2;
        public int Orphans { get; init; } = 2;

        public float ContentWidth => Math.Max (1, PageWidth - MarginLeft - MarginRight);
        public float ContentHeight => Math.Max (1, PageHeight - MarginTop - MarginBottom);

        /// <summary>Factor applied to absolute CSS lengths (see <see cref="FontSize"/>).</summary>
        public float Scale => FontSize / 16f;

        /// <summary>The settings that affect styling and line shaping (everything except page geometry).</summary>
        internal (float FontSize, float? LineHeight, string? FontFamily, bool? Justify, bool PublisherFonts, bool DarkMode) TypographyKey
            => (FontSize, LineHeight, FontFamily, Justify, PublisherFonts, DarkMode);
    }
}
