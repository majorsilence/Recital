namespace Majorsilence.Recital.Layout.Css
{
    public enum Display { Inline, Block, ListItem, None, Table, TableRowGroup, TableRow, TableCell }
    public enum TextAlign { Left, Right, Center, Justify }
    public enum WhiteSpace { Normal, NoWrap, Pre, PreWrap, PreLine }
    public enum TextTransform { None, Uppercase, Lowercase, Capitalize }
    public enum VerticalAlign { Baseline, Super, Sub }
    public enum BreakRule { Auto, Page, Avoid }

    [Flags]
    public enum TextDecoration { None = 0, Underline = 1, LineThrough = 2 }

    /// <summary>
    /// A length after the cascade: pixels (em, rem, pt and so on already converted), a percentage of
    /// the containing block's width, or <c>auto</c>.
    /// </summary>
    public readonly record struct CssLength (float Value, CssLengthKind Kind)
    {
        public static readonly CssLength Zero = new (0, CssLengthKind.Px);
        public static readonly CssLength Auto = new (0, CssLengthKind.Auto);

        public static CssLength Px (float value) => new (value, CssLengthKind.Px);
        public static CssLength Percent (float value) => new (value, CssLengthKind.Percent);

        public bool IsAuto => Kind == CssLengthKind.Auto;

        /// <summary>Pixels, with percentages taken of <paramref name="containing"/> and auto as <paramref name="auto"/>.</summary>
        public float Resolve (float containing, float auto = 0) => Kind switch {
            CssLengthKind.Px => Value,
            CssLengthKind.Percent => Value / 100f * containing,
            _ => auto,
        };

        public override string ToString () => Kind switch {
            CssLengthKind.Px => $"{Value:0.##}px",
            CssLengthKind.Percent => $"{Value:0.##}%",
            _ => "auto",
        };
    }

    public enum CssLengthKind { Px, Percent, Auto }

    /// <summary>
    /// The computed values of the properties the layout engine supports (plan 1.3's reflowable subset).
    /// Created by <see cref="StyleResolver"/>; treat as immutable afterwards.
    /// </summary>
    public sealed class ComputedStyle
    {
        // Inherited.
        public IReadOnlyList<string> FontFamilies { get; set; } = ["serif"];
        public float FontSize { get; set; } = 16;
        public int FontWeight { get; set; } = 400;
        public bool Italic { get; set; }
        public bool SmallCaps { get; set; }

        /// <summary>Line height in pixels, or null for <c>normal</c> (taken from the font).</summary>
        public float? LineHeightPx { get; set; }

        /// <summary>A unitless line-height factor; it is inherited as a factor, not as pixels.</summary>
        public float? LineHeightFactor { get; set; }

        public TextAlign TextAlign { get; set; } = TextAlign.Left;
        public CssLength TextIndent { get; set; } = CssLength.Zero;
        public WhiteSpace WhiteSpace { get; set; } = WhiteSpace.Normal;
        public TextTransform TextTransform { get; set; } = TextTransform.None;
        public float LetterSpacing { get; set; }
        public float WordSpacing { get; set; }
        public uint Color { get; set; } = 0xFF000000;
        public string ListStyleType { get; set; } = "disc";
        public int Widows { get; set; } = 2;
        public int Orphans { get; set; } = 2;
        public bool Hidden { get; set; }

        // Not inherited.
        public Display Display { get; set; } = Display.Inline;
        public CssLength MarginTop { get; set; } = CssLength.Zero;
        public CssLength MarginRight { get; set; } = CssLength.Zero;
        public CssLength MarginBottom { get; set; } = CssLength.Zero;
        public CssLength MarginLeft { get; set; } = CssLength.Zero;
        public CssLength PaddingTop { get; set; } = CssLength.Zero;
        public CssLength PaddingRight { get; set; } = CssLength.Zero;
        public CssLength PaddingBottom { get; set; } = CssLength.Zero;
        public CssLength PaddingLeft { get; set; } = CssLength.Zero;
        public float BorderTopWidth { get; set; }
        public float BorderBottomWidth { get; set; }
        public uint BorderColor { get; set; } = 0xFF000000;
        public CssLength Width { get; set; } = CssLength.Auto;
        public CssLength Height { get; set; } = CssLength.Auto;
        public CssLength MaxWidth { get; set; } = CssLength.Auto;
        public CssLength MaxHeight { get; set; } = CssLength.Auto;
        public VerticalAlign VerticalAlign { get; set; } = VerticalAlign.Baseline;
        public TextDecoration TextDecoration { get; set; }
        public BreakRule BreakBefore { get; set; }
        public BreakRule BreakAfter { get; set; }
        public BreakRule BreakInside { get; set; }

        /// <summary><c>filter: invert(...)</c> of at least 50%: the image is drawn with its colours inverted.</summary>
        public bool InvertImage { get; set; }

        public bool IsBlockLevel => Display is Display.Block or Display.ListItem or Display.Table or Display.TableRowGroup or Display.TableRow or Display.TableCell;

        /// <summary>The used line height in pixels for text set in a face whose normal line height is <paramref name="normalEms"/>.</summary>
        public float UsedLineHeight (float normalEms)
            => LineHeightPx ?? (LineHeightFactor is { } factor ? factor * FontSize : normalEms * FontSize);

        /// <summary>A new style for a child element: inherited properties copied, the rest initial.</summary>
        public ComputedStyle Inherit () => new () {
            FontFamilies = FontFamilies,
            FontSize = FontSize,
            FontWeight = FontWeight,
            Italic = Italic,
            SmallCaps = SmallCaps,
            LineHeightPx = LineHeightPx,
            LineHeightFactor = LineHeightFactor,
            TextAlign = TextAlign,
            TextIndent = TextIndent,
            WhiteSpace = WhiteSpace,
            TextTransform = TextTransform,
            LetterSpacing = LetterSpacing,
            WordSpacing = WordSpacing,
            Color = Color,
            ListStyleType = ListStyleType,
            Widows = Widows,
            Orphans = Orphans,
            Hidden = Hidden,
            BorderColor = Color,
        };
    }
}
