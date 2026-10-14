using System.Globalization;
using System.Text.RegularExpressions;

namespace Majorsilence.Recital.Layout.Css
{
    /// <summary>Parsing for the CSS value syntax the cascade understands.</summary>
    internal static partial class CssValues
    {
        /// <summary>
        /// A length or percentage. em/ex/ch use <paramref name="em"/>, rem uses <paramref name="rem"/>,
        /// and absolute units are scaled by <paramref name="scale"/> (the reader's font size choice).
        /// </summary>
        public static CssLength? Length (string value, float em, float rem, float scale, bool allowAuto = true)
        {
            value = value.Trim ().ToLowerInvariant ();

            if (value == "auto" || value == "none")
                return allowAuto ? CssLength.Auto : null;

            if (value == "0")
                return CssLength.Zero;

            var match = Number ().Match (value);
            if (!match.Success)
                return null;

            var number = float.Parse (match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);

            return match.Groups[2].Value switch {
                "px" => CssLength.Px (number * scale),
                "pt" => CssLength.Px (number * 4f / 3f * scale),
                "pc" => CssLength.Px (number * 16f * scale),
                "in" => CssLength.Px (number * 96f * scale),
                "cm" => CssLength.Px (number * 96f / 2.54f * scale),
                "mm" => CssLength.Px (number * 96f / 25.4f * scale),
                "q" => CssLength.Px (number * 96f / 101.6f * scale),
                "em" => CssLength.Px (number * em),
                "rem" => CssLength.Px (number * rem),
                "ex" or "ch" => CssLength.Px (number * em * 0.5f),
                "%" => CssLength.Percent (number),
                // Viewport units have no meaning inside a paginated column.
                "vw" or "vh" or "vmin" or "vmax" => CssLength.Zero,
                "" => number == 0 ? CssLength.Zero : null,
                _ => null,
            };
        }

        public static float? FontSize (string value, float parent, float rem, float medium, float scale)
        {
            var keyword = value.Trim ().ToLowerInvariant ();

            float? factor = keyword switch {
                "xx-small" => 3f / 5f,
                "x-small" => 3f / 4f,
                "small" => 8f / 9f,
                "medium" => 1f,
                "large" => 6f / 5f,
                "x-large" => 3f / 2f,
                "xx-large" => 2f,
                "xxx-large" => 3f,
                _ => null,
            };

            if (factor is { } f)
                return medium * f;
            if (keyword == "larger")
                return parent * 1.2f;
            if (keyword == "smaller")
                return parent / 1.2f;

            // Percentages and ems are of the parent's font size.
            return Length (value, parent, rem, scale, allowAuto: false) is { } length
                ? Math.Max (1f, length.Resolve (parent))
                : null;
        }

        public static int? FontWeight (string value, int parent)
        {
            value = value.Trim ().ToLowerInvariant ();

            return value switch {
                "normal" => 400,
                "bold" => 700,
                "bolder" => parent < 350 ? 400 : parent < 550 ? 700 : 900,
                "lighter" => parent < 550 ? 100 : parent < 750 ? 400 : 700,
                _ => int.TryParse (value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp (n, 1, 1000) : null,
            };
        }

        public static List<string> FontFamilies (string value)
            => value.Split (',')
                .Select (f => f.Trim ().Trim ('"', '\'').Trim ())
                .Where (f => f.Length > 0)
                .ToList ();

        /// <summary>ARGB from <c>rgb()</c>/<c>rgba()</c> (AngleSharp normalises colours to these) or <c>#rgb</c>/<c>#rrggbb</c>.</summary>
        public static uint? Color (string value)
        {
            value = value.Trim ().ToLowerInvariant ();

            var rgb = Rgb ().Match (value);
            if (rgb.Success) {
                byte Channel (int group) => (byte)Math.Clamp (float.Parse (rgb.Groups[group].Value, CultureInfo.InvariantCulture), 0, 255);
                var alpha = rgb.Groups[4].Success ? Math.Clamp (float.Parse (rgb.Groups[4].Value, CultureInfo.InvariantCulture), 0, 1) : 1f;
                return (uint)(byte)Math.Round (alpha * 255) << 24 | (uint)Channel (1) << 16 | (uint)Channel (2) << 8 | Channel (3);
            }

            if (value.StartsWith ('#')) {
                var hex = value[1..];
                if (hex.Length == 3)
                    hex = string.Concat (hex.Select (c => $"{c}{c}"));
                if (hex.Length == 6 && uint.TryParse (hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                    return 0xFF000000 | v;
            }

            return value switch {
                "black" => 0xFF000000,
                "white" => 0xFFFFFFFF,
                "transparent" => 0x00000000,
                _ => null,
            };
        }

        public static int? Integer (string value)
            => int.TryParse (value.Trim (), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

        public static float? Number (string value)
            => float.TryParse (value.Trim (), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

        public static BreakRule? Break (string value) => value.Trim ().ToLowerInvariant () switch {
            "always" or "page" or "left" or "right" or "recto" or "verso" => BreakRule.Page,
            "avoid" or "avoid-page" => BreakRule.Avoid,
            "auto" or "column" or "avoid-column" => BreakRule.Auto,
            _ => null,
        };

        [GeneratedRegex (@"^([+-]?(?:\d+\.?\d*|\.\d+)(?:e[+-]?\d+)?)([a-z%]*)$")]
        private static partial Regex Number ();

        [GeneratedRegex (@"^rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+)\s*)?\)$")]
        private static partial Regex Rgb ();
    }
}
