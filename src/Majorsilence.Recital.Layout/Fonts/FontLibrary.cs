namespace Majorsilence.Recital.Layout.Fonts
{
    /// <summary>
    /// Resolves CSS font families to faces. Starts with the bundled DejaVu faces (serif, sans-serif,
    /// monospace); a book's <c>@font-face</c> fonts are added to a per-book copy.
    /// Resolution never looks at the machine's installed fonts, so layout is the same everywhere.
    /// </summary>
    public sealed class FontLibrary
    {
        public const string Serif = "DejaVu Serif";
        public const string SansSerif = "DejaVu Sans";
        public const string Monospace = "DejaVu Sans Mono";

        private static readonly Lazy<FontLibrary> bundled = new (LoadBundled);

        private readonly Dictionary<string, List<FontFace>> families;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, bool), FontFace> resolved = new ();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int, bool), FontFace?> fallbacks = new ();

        private FontLibrary (Dictionary<string, List<FontFace>> families)
        {
            this.families = families;
        }

        /// <summary>The bundled faces. Shared, never modified; use <see cref="CreateChild"/> to add fonts.</summary>
        public static FontLibrary Bundled => bundled.Value;

        public FontFace DefaultFace => Resolve ([Serif], 400, false);

        public IEnumerable<string> FamilyNames => families.Keys;

        /// <summary>A copy that shares this library's faces and can take more (a book's embedded fonts).</summary>
        public FontLibrary CreateChild ()
            => new (families.ToDictionary (f => f.Key, f => f.Value.ToList (), StringComparer.OrdinalIgnoreCase));

        public void Add (FontFace face)
        {
            resolved.Clear ();
            fallbacks.Clear ();
            if (!families.TryGetValue (face.Family, out var faces))
                families[face.Family] = faces = [];
            faces.Add (face);
        }

        public bool HasFamily (string family) => families.ContainsKey (MapGeneric (family));

        /// <summary>
        /// CSS font matching, simplified: the first listed family that exists, then the face with the
        /// right style closest in weight (CSS's rule: for 400 and below prefer lighter, above prefer
        /// heavier). Unknown families fall through to the next; the final fallback is the bundled serif.
        /// </summary>
        public FontFace Resolve (IReadOnlyList<string> requested, int weight, bool italic)
            => resolved.GetOrAdd ((string.Join ('\u0001', requested), weight, italic), _ => ResolveUncached (requested, weight, italic));

        private FontFace ResolveUncached (IReadOnlyList<string> requested, int weight, bool italic)
        {
            foreach (var name in requested) {
                if (families.TryGetValue (MapGeneric (name), out var faces) && faces.Count > 0)
                    return Closest (faces, weight, italic);
            }

            return Closest (families[Serif], weight, italic);
        }

        /// <summary>A face that has a glyph for <paramref name="codepoint"/>, for when the styled face lacks it.</summary>
        public FontFace? FallbackFor (int codepoint, int weight, bool italic)
            => fallbacks.GetOrAdd ((codepoint, weight, italic), key => FallbackUncached (key.Item1, key.Item2, key.Item3));

        private FontFace? FallbackUncached (int codepoint, int weight, bool italic)
        {
            foreach (var family in new[] { Serif, SansSerif, Monospace }) {
                var face = Closest (families[family], weight, italic);
                if (face.HasGlyph (codepoint))
                    return face;
            }

            foreach (var faces in families.Values) {
                var face = Closest (faces, weight, italic);
                if (face.HasGlyph (codepoint))
                    return face;
            }

            return null;
        }

        private static string MapGeneric (string family) => family.Trim ().Trim ('"', '\'').ToLowerInvariant () switch {
            "serif" or "cursive" or "fantasy" or "ui-serif" => Serif,
            "sans-serif" or "system-ui" or "ui-sans-serif" or "-apple-system" => SansSerif,
            "monospace" or "ui-monospace" => Monospace,
            _ => family.Trim ().Trim ('"', '\''),
        };

        private static FontFace Closest (List<FontFace> faces, int weight, bool italic)
        {
            var styled = faces.Where (f => f.Italic == italic).ToList ();
            if (styled.Count == 0)
                styled = faces;

            return styled
                .OrderBy (f => WeightDistance (f.Weight, weight))
                .ThenBy (f => f.Weight)
                .First ();
        }

        private static int WeightDistance (int available, int wanted)
        {
            if (available == wanted)
                return 0;

            // Prefer the direction CSS prefers by giving the other direction a large penalty.
            var lighter = available < wanted;
            var preferLighter = wanted <= 400;
            return Math.Abs (available - wanted) + (lighter == preferLighter ? 0 : 1000);
        }

        private static FontLibrary LoadBundled ()
        {
            var families = new Dictionary<string, List<FontFace>> (StringComparer.OrdinalIgnoreCase);
            var assembly = typeof (FontLibrary).Assembly;

            void Add (string family, string file, int weight, bool italic)
            {
                using var stream = assembly.GetManifestResourceStream ("Majorsilence.Recital.Layout.Fonts." + file)
                    ?? throw new InvalidOperationException ($"Bundled font {file} is missing.");
                using var buffer = new MemoryStream ();
                stream.CopyTo (buffer);

                var face = FontFace.Load (family, weight, italic, buffer.ToArray ())
                    ?? throw new InvalidOperationException ($"Bundled font {file} did not load.");

                if (!families.TryGetValue (family, out var faces))
                    families[family] = faces = [];
                faces.Add (face);
            }

            Add (Serif, "DejaVuSerif.ttf", 400, false);
            Add (Serif, "DejaVuSerif-Bold.ttf", 700, false);
            Add (Serif, "DejaVuSerif-Italic.ttf", 400, true);
            Add (Serif, "DejaVuSerif-BoldItalic.ttf", 700, true);
            Add (SansSerif, "DejaVuSans.ttf", 400, false);
            Add (SansSerif, "DejaVuSans-Bold.ttf", 700, false);
            Add (SansSerif, "DejaVuSans-Oblique.ttf", 400, true);
            Add (SansSerif, "DejaVuSans-BoldOblique.ttf", 700, true);
            Add (Monospace, "DejaVuSansMono.ttf", 400, false);
            Add (Monospace, "DejaVuSansMono-Bold.ttf", 700, false);

            return new FontLibrary (families);
        }
    }
}
