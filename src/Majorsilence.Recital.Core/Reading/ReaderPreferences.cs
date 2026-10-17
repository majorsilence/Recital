using System.Globalization;
using Majorsilence.Recital.Layout;

namespace Majorsilence.Recital.Core.Reading
{
    public enum ThemeKind
    {
        Light,
        Sepia,
        Dark,
        Black,
    }

    public enum MarginSize
    {
        Narrow,
        Normal,
        Wide,
    }

    /// <summary>
    /// The reader's typography and theme choices. Synced per key (<c>settings/&lt;deviceId&gt;.json</c>), so
    /// each value round-trips through a string.
    /// </summary>
    public sealed record ReaderPreferences
    {
        public const float MinFontSize = 10;
        public const float MaxFontSize = 48;

        public ThemeKind Theme { get; init; } = ThemeKind.Light;
        public float FontSize { get; init; } = 18;

        /// <summary>A generic family ("serif", "sans-serif", "monospace"), or null for the book's own.</summary>
        public string? FontFamily { get; init; }

        /// <summary>Line height as a multiple of the font size, or null for the book's own.</summary>
        public float? LineHeight { get; init; }

        /// <summary>True: justified; false: left-aligned; null: as the book says.</summary>
        public bool? Justify { get; init; }

        public MarginSize Margins { get; init; } = MarginSize.Normal;
        public bool PublisherFonts { get; init; } = true;

        /// <summary>Reading-aloud speed, 1 = normal.</summary>
        public float TtsRate { get; init; } = 1;

        /// <summary>Reading-aloud voice as <c>engine:voice</c> (a voice belongs to one engine), or null for the default.</summary>
        public string? TtsVoice { get; init; }

        /// <summary>
        /// Page geometry and typography for a viewport. Margins grow with the viewport, and on wide ones the
        /// text column is held to a readable measure (about 36 em) rather than running the full width.
        /// </summary>
        public LayoutSettings ToLayoutSettings (float width, float height)
        {
            var factor = Margins switch { MarginSize.Narrow => 0.5f, MarginSize.Wide => 1.75f, _ => 1f };
            var side = Math.Clamp (width * 0.05f, 16, 64) * factor;
            var measure = FontSize * 36;
            if (width - 2 * side > measure)
                side = (width - measure) / 2;
            var vertical = Math.Clamp (height * 0.04f, 16, 48) * Math.Max (0.75f, factor);

            return new LayoutSettings {
                PageWidth = MathF.Floor (width),
                PageHeight = MathF.Floor (height),
                MarginLeft = MathF.Round (side),
                MarginRight = MathF.Round (side),
                MarginTop = MathF.Round (vertical),
                MarginBottom = MathF.Round (vertical),
                FontSize = Math.Clamp (FontSize, MinFontSize, MaxFontSize),
                FontFamily = FontFamily,
                LineHeight = LineHeight,
                Justify = Justify,
                PublisherFonts = PublisherFonts,
                DarkMode = IsDark,
            };
        }

        /// <summary>Whether the theme is a dark one, which books can style for (<c>prefers-color-scheme</c>).</summary>
        public bool IsDark => Theme is ThemeKind.Dark or ThemeKind.Black;

        /// <summary>
        /// Whether a change from <paramref name="other"/> needs the book laid out again: any typography
        /// change, or a theme change between light and dark (books style for the colour scheme).
        /// </summary>
        public bool AffectsLayout (ReaderPreferences other)
            => this with { Theme = other.Theme, TtsRate = other.TtsRate, TtsVoice = other.TtsVoice } != other || IsDark != other.IsDark;

        public IReadOnlyDictionary<string, string> ToValues () => new Dictionary<string, string> {
            ["theme"] = Theme.ToString ().ToLowerInvariant (),
            ["fontSize"] = FontSize.ToString (CultureInfo.InvariantCulture),
            ["fontFamily"] = FontFamily ?? "",
            ["lineHeight"] = LineHeight?.ToString (CultureInfo.InvariantCulture) ?? "",
            ["justify"] = Justify switch { true => "justify", false => "left", null => "" },
            ["margins"] = Margins.ToString ().ToLowerInvariant (),
            ["publisherFonts"] = PublisherFonts ? "true" : "false",
            ["ttsRate"] = TtsRate.ToString (CultureInfo.InvariantCulture),
            ["ttsVoice"] = TtsVoice ?? "",
        };

        /// <summary>Reads stored values; a missing or unreadable one keeps its default.</summary>
        public static ReaderPreferences FromValues (IReadOnlyDictionary<string, string> values)
        {
            var result = new ReaderPreferences ();
            string? Get (string key) => values.TryGetValue (key, out var v) ? v : null;

            if (Enum.TryParse<ThemeKind> (Get ("theme"), ignoreCase: true, out var theme))
                result = result with { Theme = theme };
            if (float.TryParse (Get ("fontSize"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                result = result with { FontSize = Math.Clamp (size, MinFontSize, MaxFontSize) };
            if (Get ("fontFamily") is { } family)
                result = result with { FontFamily = family is "serif" or "sans-serif" or "monospace" ? family : null };
            if (Get ("lineHeight") is { } lineHeight)
                result = result with { LineHeight = float.TryParse (lineHeight, NumberStyles.Float, CultureInfo.InvariantCulture, out var lh) && lh is >= 0.8f and <= 3f ? lh : null };
            if (Get ("justify") is { } justify)
                result = result with { Justify = justify switch { "justify" => true, "left" => false, _ => null } };
            if (Enum.TryParse<MarginSize> (Get ("margins"), ignoreCase: true, out var margins))
                result = result with { Margins = margins };
            if (Get ("publisherFonts") is { } publisher)
                result = result with { PublisherFonts = publisher != "false" };
            if (float.TryParse (Get ("ttsRate"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                result = result with { TtsRate = Math.Clamp (rate, 0.5f, 3f) };
            if (Get ("ttsVoice") is { Length: > 0 } voice)
                result = result with { TtsVoice = voice };

            return result;
        }
    }

    /// <summary>Colours for one theme, as 0xAARRGGBB.</summary>
    /// <param name="OverrideBookColors">Draw all text in <paramref name="PageText"/>, ignoring the book's colours (dark themes).</param>
    public sealed record ReaderTheme (ThemeKind Kind, string Name, uint PageBackground, uint PageText, uint ChromeBackground, uint ChromeText, uint Accent, bool OverrideBookColors)
    {
        public static readonly ReaderTheme Light = new (ThemeKind.Light, "Light", 0xFFFFFFFF, 0xFF1C1C1C, 0xFFF2F2F2, 0xFF1C1C1C, 0xFF2F6FB0, false);
        public static readonly ReaderTheme Sepia = new (ThemeKind.Sepia, "Sepia", 0xFFF5ECD7, 0xFF4B3A2A, 0xFFEADFC4, 0xFF4B3A2A, 0xFF9A5B2A, true);
        public static readonly ReaderTheme Dark = new (ThemeKind.Dark, "Dark", 0xFF202124, 0xFFD8D8D8, 0xFF2B2C30, 0xFFE0E0E0, 0xFF7FB2E5, true);
        public static readonly ReaderTheme Black = new (ThemeKind.Black, "Black", 0xFF000000, 0xFFBDBDBD, 0xFF0D0D0D, 0xFFC8C8C8, 0xFF7FB2E5, true);

        public static IReadOnlyList<ReaderTheme> All { get; } = [Light, Sepia, Dark, Black];

        public static ReaderTheme For (ThemeKind kind) => All.First (t => t.Kind == kind);
    }
}
