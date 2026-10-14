using System.Collections.Concurrent;
using HarfBuzzSharp;
using SkiaSharp;

namespace Majorsilence.Recital.Layout.Fonts
{
    /// <summary>
    /// One font file. Shaping and every metric come from HarfBuzz reading the file's own tables, so they
    /// are identical on every platform; the Skia typeface exists only for drawing.
    /// </summary>
    public sealed class FontFace : IDisposable
    {
        private readonly Blob blob;
        private readonly Face face;
        private readonly HarfBuzzSharp.Font font;
        private readonly ConcurrentDictionary<int, bool> has_glyph = new ();
        private readonly Lazy<SKTypeface> typeface;

        private FontFace (string family, int weight, bool italic, byte[] data, Blob blob, Face face, HarfBuzzSharp.Font font)
        {
            Family = family;
            Weight = weight;
            Italic = italic;
            Data = data;
            this.blob = blob;
            this.face = face;
            this.font = font;

            UnitsPerEm = face.UnitsPerEm;
            font.TryGetHorizontalFontExtents (out var extents);
            Ascender = extents.Ascender / (float)UnitsPerEm;
            Descender = -extents.Descender / (float)UnitsPerEm;
            LineGap = extents.LineGap / (float)UnitsPerEm;

            typeface = new Lazy<SKTypeface> (() => SKTypeface.FromData (SKData.CreateCopy (Data)) ?? SKTypeface.Default);
        }

        public string Family { get; }
        public int Weight { get; }
        public bool Italic { get; }
        public byte[] Data { get; }
        public int UnitsPerEm { get; }

        /// <summary>Ascent above the baseline, in ems (positive).</summary>
        public float Ascender { get; }

        /// <summary>Descent below the baseline, in ems (positive).</summary>
        public float Descender { get; }

        public float LineGap { get; }

        /// <summary>CSS <c>line-height: normal</c> for this face, in ems.</summary>
        public float NormalLineHeight => Ascender + Descender + LineGap;

        /// <summary>For drawing only. Never consulted by layout.</summary>
        public SKTypeface Typeface => typeface.Value;

        /// <summary>Loads a font file; null when HarfBuzz finds no glyphs in it (not a font, or WOFF2).</summary>
        public static FontFace? Load (string family, int weight, bool italic, byte[] data)
        {
            // HarfBuzz keeps reading the font's memory for the face's lifetime, so it must not move: copy
            // it to unmanaged memory the blob owns and frees. (Blob.FromStream did not hold up: after a GC,
            // shaping read garbage glyph ids and advances.)
            var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal (data.Length);
            System.Runtime.InteropServices.Marshal.Copy (data, 0, memory, data.Length);
            var blob = new Blob (memory, data.Length, MemoryMode.ReadOnly, () => System.Runtime.InteropServices.Marshal.FreeHGlobal (memory));
            var face = new Face (blob, 0);

            if (face.GlyphCount == 0) {
                face.Dispose ();
                blob.Dispose ();
                return null;
            }

            var font = new HarfBuzzSharp.Font (face);
            // OpenType functions: advances and kerning straight from the font's tables.
            font.SetFunctionsOpenType ();
            return new FontFace (family, weight, italic, data, blob, face, font);
        }

        private bool[]? has_ascii_glyph;

        public bool HasGlyph (int codepoint)
        {
            if (codepoint < 128) {
                // Built whole, then published: readers on other threads never see a half-filled table.
                var table = Volatile.Read (ref has_ascii_glyph);
                if (table is null) {
                    table = new bool[128];
                    for (var c = 0; c < 128; c++)
                        table[c] = font.TryGetGlyph ((uint)c, out var g) && g != 0;
                    Volatile.Write (ref has_ascii_glyph, table);
                }
                return table[codepoint];
            }

            return has_glyph.GetOrAdd (codepoint, static (c, f) => f.TryGetGlyph ((uint)c, out var glyph) && glyph != 0, font);
        }

        /// <summary>
        /// Shapes <c>text[start..start+length]</c> with the whole string as context. Advances are in font
        /// units; clusters index into <paramref name="text"/>.
        /// </summary>
        internal (GlyphInfo[] Infos, GlyphPosition[] Positions) Shape (string text, int start, int length, string? language, Feature[] features)
        {
            using var buffer = new HarfBuzzSharp.Buffer ();
            buffer.AddUtf16 (text, start, length);
            // Script from the text; direction forced left-to-right (bidi is not supported yet).
            buffer.Direction = Direction.LeftToRight;
            buffer.GuessSegmentProperties ();
            if (language is { Length: > 0 })
                buffer.Language = new Language (language);

            // HarfBuzz's Font is immutable once set up, and shaping only reads it.
            font.Shape (buffer, features);
            return (buffer.GlyphInfos, buffer.GlyphPositions);
        }

        public override string ToString () => $"{Family} {Weight}{(Italic ? " italic" : string.Empty)}";

        public void Dispose ()
        {
            font.Dispose ();
            face.Dispose ();
            blob.Dispose ();
            if (typeface.IsValueCreated)
                typeface.Value.Dispose ();
        }
    }
}
