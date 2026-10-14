using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using HarfBuzzSharp;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout.Boxes;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Fonts;

namespace Majorsilence.Recital.Layout.Text
{
    /// <summary>
    /// A paragraph's text after whitespace processing, mapped back to its DOM text nodes, shaped once
    /// (independently of the line width) and broken into lines on demand for any width.
    /// </summary>
    internal sealed class ShapedParagraph
    {
        private const char ForcedBreak = '\n';
        private const char SoftHyphen = '­';
        private const float SmallCapsScale = 0.75f;

        private readonly ParagraphBox box;
        private readonly FontLibrary fonts;

        // Per character of Text.
        private readonly INode[] source_node;
        private readonly int[] source_offset;
        private readonly int[] segment_of;
        private readonly bool[] collapsible;
        private readonly double[] prefix;
        private readonly bool[] break_after;
        private readonly bool[] no_wrap;

        private readonly List<Segment> segments = [];
        private readonly List<Glyph> glyphs = [];
        private readonly FontFace strut_face;

        private ShapedParagraph (ParagraphBox box, FontLibrary fonts, string text, INode[] nodes, int[] offsets, ComputedStyle[] styles, bool[] collapsible)
        {
            this.box = box;
            this.fonts = fonts;
            Text = text;
            source_node = nodes;
            source_offset = offsets;
            this.collapsible = collapsible;
            segment_of = new int[text.Length];
            prefix = new double[text.Length + 1];
            break_after = new bool[text.Length];
            no_wrap = new bool[text.Length];
            strut_face = fonts.Resolve (box.Style.FontFamilies, box.Style.FontWeight, box.Style.Italic);

            Shape (styles);
            FindBreaks (styles);
        }

        public string Text { get; }

        public ComputedStyle Style => box.Style;

        /// <summary>The block element whose text this is (null for an anonymous paragraph's root).</summary>
        public IElement? Element => box.Element;

        public static ShapedParagraph Create (ParagraphBox box, FontLibrary fonts)
        {
            var text = new StringBuilder ();
            var nodes = new List<INode> ();
            var offsets = new List<int> ();
            var styles = new List<ComputedStyle> ();
            var spaces = new List<bool> ();
            var afterSpace = true;

            void Append (char c, INode node, int offset, ComputedStyle style, bool isCollapsibleSpace)
            {
                text.Append (c);
                nodes.Add (node);
                offsets.Add (offset);
                styles.Add (style);
                spaces.Add (isCollapsibleSpace);
            }

            void TrimTrailingSpace ()
            {
                while (spaces.Count > 0 && spaces[^1]) {
                    var last = spaces.Count - 1;
                    text.Length--;
                    nodes.RemoveAt (last);
                    offsets.RemoveAt (last);
                    styles.RemoveAt (last);
                    spaces.RemoveAt (last);
                }
            }

            foreach (var item in box.Items) {
                if (item is LineBreakItem br) {
                    TrimTrailingSpace ();
                    Append (ForcedBreak, br.Element, 0, br.Style, false);
                    afterSpace = true;
                    continue;
                }

                var textItem = (TextItem)item;
                var data = textItem.Node.Data;
                var style = textItem.Style;

                for (var i = 0; i < data.Length; i++) {
                    var c = data[i];

                    if (style.WhiteSpace is WhiteSpace.Pre or WhiteSpace.PreWrap) {
                        if (c == '\r')
                            continue;
                        Append (c == '\t' ? ' ' : c, textItem.Node, i, style, false);
                        afterSpace = false;
                        continue;
                    }

                    if (c == '\n' && style.WhiteSpace == WhiteSpace.PreLine) {
                        TrimTrailingSpace ();
                        Append (ForcedBreak, textItem.Node, i, style, false);
                        afterSpace = true;
                        continue;
                    }

                    if (c is ' ' or '\t' or '\n' or '\r' or '\f') {
                        if (!afterSpace)
                            Append (' ', textItem.Node, i, style, true);
                        afterSpace = true;
                        continue;
                    }

                    var previous = text.Length > 0 ? text[^1] : ' ';
                    Append (Transform (c, previous, style.TextTransform), textItem.Node, i, style, false);
                    afterSpace = false;
                }
            }

            TrimTrailingSpace ();

            return new ShapedParagraph (box, fonts, text.ToString (), [.. nodes], [.. offsets], [.. styles], [.. spaces]);
        }

        private static char Transform (char c, char previous, TextTransform transform) => transform switch {
            TextTransform.Uppercase => char.ToUpperInvariant (c),
            TextTransform.Lowercase => char.ToLowerInvariant (c),
            TextTransform.Capitalize when !char.IsLetterOrDigit (previous) && previous is not ('\'' or '’') => char.ToUpperInvariant (c),
            _ => c,
        };

        public DomPosition? PositionAt (int index)
        {
            if (Text.Length == 0)
                return null;
            index = Math.Clamp (index, 0, Text.Length - 1);
            return new DomPosition (source_node[index], source_offset[index]);
        }

        /// <summary>The position just after the character before <paramref name="end"/>.</summary>
        public DomPosition? PositionAfter (int end)
        {
            if (Text.Length == 0)
                return null;
            var last = Math.Clamp (end - 1, 0, Text.Length - 1);
            return source_node[last] is IText
                ? new DomPosition (source_node[last], source_offset[last] + 1)
                : new DomPosition (source_node[last], 0);
        }

        // ── Shaping ──────────────────────────────────────────────────────────────

        private void Shape (ComputedStyle[] styles)
        {
            // What HarfBuzz sees: small-caps lowercase letters set as capitals (in a smaller size), forced
            // breaks as spaces. Same length as Text, so clusters still index Text.
            var shaping = new StringBuilder (Text);

            var i = 0;
            while (i < Text.Length) {
                var style = styles[i];
                var face = FaceFor (i, style, out var length);
                var small = style.SmallCaps && char.IsLower (Text[i]);
                var start = i;
                i += length;

                // Extend while the style, the face and (for small caps only) the letter case are unchanged.
                while (i < Text.Length && ReferenceEquals (styles[i], style) && (!style.SmallCaps || char.IsLower (Text[i]) == small)) {
                    var next = FaceFor (i, style, out var nextLength);
                    if (!ReferenceEquals (next, face))
                        break;
                    i += nextLength;
                }

                for (var k = start; k < i; k++) {
                    if (small)
                        shaping[k] = char.ToUpperInvariant (Text[k]);
                    else if (Text[k] == ForcedBreak)
                        shaping[k] = ' ';
                }

                var size = small ? style.FontSize * SmallCapsScale : style.FontSize;
                var shift = style.VerticalAlign switch {
                    VerticalAlign.Super => style.FontSize * 0.4f,
                    VerticalAlign.Sub => -style.FontSize * 0.24f,
                    _ => 0f,
                };

                segments.Add (new Segment (start, i, face, size, style, shift));
            }

            var text = shaping.ToString ();

            for (var s = 0; s < segments.Count; s++) {
                var segment = segments[s];
                for (var k = segment.Start; k < segment.End; k++)
                    segment_of[k] = s;

                Feature[] features = segment.Style.LetterSpacing != 0 ? [new Feature (new Tag ('l', 'i', 'g', 'a'), 0)] : [];
                var (infos, positions) = segment.Face.Shape (text, segment.Start, segment.End - segment.Start, null, features);
                var unit = segment.Size / segment.Face.UnitsPerEm;

                for (var g = 0; g < infos.Length; g++) {
                    var cluster = (int)infos[g].Cluster;
                    var advance = positions[g].XAdvance * unit + segment.Style.LetterSpacing;

                    if (Text[cluster] == ' ')
                        advance += segment.Style.WordSpacing;
                    if (Text[cluster] is ForcedBreak or SoftHyphen)
                        advance = 0;

                    glyphs.Add (new Glyph ((ushort)infos[g].Codepoint, cluster, advance, positions[g].XOffset * unit, positions[g].YOffset * unit, s));
                    prefix[cluster + 1] += advance;
                }
            }

            for (var k = 0; k < Text.Length; k++)
                prefix[k + 1] += prefix[k];
        }

        private ComputedStyle? last_style;
        private FontFace? last_face;

        /// <summary>The face for the character at <paramref name="index"/>: the styled one, or a fallback that has the glyph.</summary>
        private FontFace FaceFor (int index, ComputedStyle style, out int length)
        {
            var codepoint = Text.ConvertToUtf32OrSelf (index, out length);

            // Consecutive characters almost always share a style object.
            if (!ReferenceEquals (style, last_style)) {
                last_style = style;
                last_face = fonts.Resolve (style.FontFamilies, style.FontWeight, style.Italic);
            }
            var face = last_face!;

            if (IsIgnorable (codepoint) || face.HasGlyph (codepoint))
                return face;

            return fonts.FallbackFor (codepoint, style.FontWeight, style.Italic) ?? face;
        }

        private static bool IsIgnorable (int codepoint)
            => codepoint is ' ' or ForcedBreak or SoftHyphen or 0x200B or 0x200C or 0x200D or 0x2060 or 0xFEFF
            || CharUnicodeInfo.GetUnicodeCategory (codepoint) is UnicodeCategory.Control or UnicodeCategory.Format;

        // ── Line breaking ────────────────────────────────────────────────────────

        /// <summary>
        /// Break opportunities (a simplified UAX #14): after spaces, after hyphens and dashes inside words,
        /// at soft hyphens, after zero-width spaces, around em dashes, between CJK characters; never inside
        /// nowrap/pre text or before a non-breaking character.
        /// </summary>
        private void FindBreaks (ComputedStyle[] styles)
        {
            for (var i = 0; i < Text.Length; i++)
                no_wrap[i] = styles[i].WhiteSpace is WhiteSpace.NoWrap or WhiteSpace.Pre;

            for (var i = 0; i < Text.Length - 1; i++) {
                var c = Text[i];
                var next = Text[i + 1];

                // Whether text may wrap at a point is up to the character before it: a space in normal
                // text before a nowrap span is a break opportunity; anything inside the span is not.
                if (no_wrap[i])
                    continue;
                if (next is ' ' or ' ' or ' ' or '⁠' or ' ' || IsClosingPunctuation (next))
                    continue;

                break_after[i] = c switch {
                    ' ' or '​' or SoftHyphen => true,
                    '-' or '‐' or '–' => i > 0 && char.IsLetterOrDigit (Text[i - 1]) && char.IsLetter (next),
                    '—' => true,
                    _ => next == '—' && char.IsLetter (c) || IsCjk (c) || IsCjk (next) && !char.IsWhiteSpace (c),
                };
            }
        }

        private static bool IsCjk (char c) => c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '가' and <= '힯';

        private static bool IsClosingPunctuation (char c) => c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '”' or '’' or '、' or '。' or '」' or '』' or '，' or '．' or '！' or '？';

        /// <summary>Width of [start, end) not counting trailing collapsible spaces and forced breaks.</summary>
        private float ContentWidth (int start, int end, out int contentEnd)
        {
            contentEnd = end;
            while (contentEnd > start && (collapsible[contentEnd - 1] || Text[contentEnd - 1] == ForcedBreak))
                contentEnd--;

            var width = (float)(prefix[contentEnd] - prefix[start]);
            if (contentEnd > start && Text[contentEnd - 1] == SoftHyphen)
                width += HyphenAdvance (contentEnd - 1);
            return width;
        }

        /// <summary>Greedy (first-fit) line breaking for the given widths.</summary>
        public List<LineRange> BreakLines (float firstWidth, float width)
        {
            var lines = new List<LineRange> ();
            var start = 0;

            while (start < Text.Length) {
                var available = lines.Count == 0 ? firstWidth : width;
                var best = -1;
                var forced = false;
                var end = Text.Length;

                for (var i = start; i < Text.Length; i++) {
                    if (Text[i] == ForcedBreak) {
                        if (best < 0 || ContentWidth (start, i + 1, out _) <= available + 0.01f) {
                            best = i;
                            forced = true;
                        }
                        break;
                    }

                    if (break_after[i]) {
                        if (ContentWidth (start, i + 1, out _) <= available + 0.01f)
                            best = i;
                        else
                            break;
                    } else if (best >= 0 && prefix[i + 1] - prefix[start] > available + 2 * box.Style.FontSize) {
                        // Already well past the line: no later break can fit (trailing spaces, which do not
                        // count, are far narrower than two ems).
                        break;
                    }

                    if (i == Text.Length - 1 && ContentWidth (start, Text.Length, out _) <= available + 0.01f) {
                        best = i;
                        break;
                    }
                }

                if (best >= 0) {
                    end = best + 1;
                } else if (no_wrap[start]) {
                    // Text that must not wrap overflows to its next break opportunity instead.
                    end = start + 1;
                    while (end < Text.Length && !break_after[end - 1] && Text[end - 1] != ForcedBreak)
                        end++;
                } else {
                    // Nothing fits: a word longer than the line. Break it where the line is full (at a
                    // cluster boundary, hyphen included), keeping at least one character so layout always
                    // progresses.
                    end = start + 1;
                    while (end < Text.Length && Text[end] != ForcedBreak && !no_wrap[end] && ContentWidth (start, end + 1, out _) <= available)
                        end++;
                    while (end < Text.Length && end > start + 1 && IsInsideCluster (end))
                        end--;
                }

                var lineWidth = ContentWidth (start, end, out var contentEnd);
                var last = end >= Text.Length;
                lines.Add (new LineRange (start, end, contentEnd, lineWidth, Hyphenated: contentEnd > start && Text[contentEnd - 1] == SoftHyphen, EndsParagraph: last || forced));
                start = end;
            }

            return lines;
        }

        private bool IsInsideCluster (int index)
        {
            // A character inside a ligature or a combining sequence has no glyph advance of its own.
            return char.IsLowSurrogate (Text[index]) || CharUnicodeInfo.GetUnicodeCategory (Text[index]) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
        }

        private readonly Dictionary<(FontFace, float), (ushort Glyph, float Advance)> hyphens = [];

        private float HyphenAdvance (int index) => Hyphen (index).Advance;

        private (ushort Glyph, float Advance) Hyphen (int index)
        {
            var segment = segments[segment_of[index]];
            var key = (segment.Face, segment.Size);
            if (!hyphens.TryGetValue (key, out var hyphen)) {
                var (infos, positions) = segment.Face.Shape ("-", 0, 1, null, []);
                hyphen = ((ushort)infos[0].Codepoint, positions[0].XAdvance * segment.Size / segment.Face.UnitsPerEm);
                hyphens[key] = hyphen;
            }
            return hyphen;
        }

        // ── Building lines ───────────────────────────────────────────────────────

        /// <summary>Positions one line's glyphs: alignment, justification, the hyphen, the list marker.</summary>
        public TextLine BuildLine (LineRange range, float available, float indent, bool first)
        {
            var align = box.Style.TextAlign;
            var justify = align == TextAlign.Justify && !range.EndsParagraph;
            var extra = 0f;

            if (justify) {
                var spaces = 0;
                for (var k = range.Start; k < range.ContentEnd; k++)
                    if (Text[k] == ' ')
                        spaces++;
                if (spaces > 0)
                    extra = Math.Max (0, available - range.Width) / spaces;
            }

            var x = indent + align switch {
                TextAlign.Right => Math.Max (0, available - range.Width),
                TextAlign.Center => Math.Max (0, (available - range.Width) / 2),
                _ => 0,
            };

            var runs = new List<GlyphRun> ();
            var runGlyphs = new List<ushort> ();
            var runX = new List<float> ();
            var runClusters = new List<int> ();
            var runSegment = -1;

            void FlushRun (float endX)
            {
                if (runGlyphs.Count == 0)
                    return;
                var segment = segments[runSegment];
                runs.Add (new GlyphRun (segment.Face, segment.Size, [.. runGlyphs], [.. runX], [.. runClusters], segment.Shift, segment.Style.Color, segment.Style.TextDecoration) { EndX = endX });
                runGlyphs.Clear ();
                runX.Clear ();
                runClusters.Clear ();
            }

            foreach (var glyph in GlyphsIn (range.Start, range.ContentEnd)) {
                var c = Text[glyph.Cluster];
                if (c is ForcedBreak or SoftHyphen)
                    continue;

                if (glyph.Segment != runSegment) {
                    FlushRun (x);
                    runSegment = glyph.Segment;
                }

                runGlyphs.Add (glyph.Id);
                runX.Add (x + glyph.OffsetX);
                runClusters.Add (glyph.Cluster);
                x += glyph.Advance;
                if (c == ' ')
                    x += extra;
            }

            if (range.Hyphenated) {
                var (hyphenGlyph, hyphenAdvance) = Hyphen (range.ContentEnd - 1);
                if (segment_of[range.ContentEnd - 1] != runSegment) {
                    FlushRun (x);
                    runSegment = segment_of[range.ContentEnd - 1];
                }
                runGlyphs.Add (hyphenGlyph);
                runX.Add (x);
                runClusters.Add (-1);
                x += hyphenAdvance;
            }

            FlushRun (x);

            if (first && box.Marker is { } marker)
                runs.Insert (0, MarkerRun (marker, indent));

            var (baseline, height) = Metrics (range);
            var line = new TextLine (this, range.Start, Math.Max (range.ContentEnd, Math.Min (range.End, range.Start + 1)), runs, baseline) {
                // The whole column: glyph positions include the first line's indent.
                Width = available + indent,
                Height = height,
            };
            return line;
        }

        private IEnumerable<Glyph> GlyphsIn (int start, int end)
        {
            // Glyphs are in logical order, so a binary search finds the first.
            int low = 0, high = glyphs.Count;
            while (low < high) {
                var mid = (low + high) / 2;
                if (glyphs[mid].Cluster < start)
                    low = mid + 1;
                else
                    high = mid;
            }

            for (var g = low; g < glyphs.Count && glyphs[g].Cluster < end; g++)
                yield return glyphs[g];
        }

        private GlyphRun MarkerRun (string marker, float indent)
        {
            var style = box.Style;
            var face = fonts.Resolve (style.FontFamilies, style.FontWeight, style.Italic);
            var (infos, positions) = face.Shape (marker, 0, marker.Length, null, []);
            var unit = style.FontSize / face.UnitsPerEm;

            var width = positions.Sum (p => p.XAdvance * unit);
            var x = indent - width - style.FontSize * 0.5f;
            var xs = new float[infos.Length];
            for (var i = 0; i < infos.Length; i++) {
                xs[i] = x;
                x += positions[i].XAdvance * unit;
            }

            return new GlyphRun (face, style.FontSize, infos.Select (g => (ushort)g.Codepoint).ToArray (), xs, Enumerable.Repeat (-1, infos.Length).ToArray (), 0, style.Color, TextDecoration.None) { EndX = x };
        }

        /// <summary>
        /// CSS inline box model, simplified: every inline box on the line (and the paragraph's own strut)
        /// contributes its line-height, split evenly around its glyph box; the line is tall enough for all.
        /// </summary>
        private (float Baseline, float Height) Metrics (LineRange range)
        {
            var strut = box.Style;
            var aboveBaseline = 0f;
            var belowBaseline = 0f;

            void Include (FontFace face, float size, ComputedStyle style, float shift)
            {
                var lineHeight = style.UsedLineHeight (face.NormalLineHeight) * (size / style.FontSize);
                var ascent = face.Ascender * size;
                var descent = face.Descender * size;
                var halfLeading = (lineHeight - ascent - descent) / 2;
                aboveBaseline = Math.Max (aboveBaseline, halfLeading + ascent + shift);
                belowBaseline = Math.Max (belowBaseline, lineHeight - halfLeading - ascent - shift);
            }

            Include (strut_face, strut.FontSize, strut, 0);

            var end = Math.Max (range.ContentEnd, range.Start + 1);
            var last = -1;
            for (var k = range.Start; k < end && k < Text.Length; k++) {
                var s = segment_of[k];
                if (s == last)
                    continue;
                last = s;
                var segment = segments[s];
                Include (segment.Face, segment.Size, segment.Style, segment.Shift);
            }

            return (aboveBaseline, aboveBaseline + belowBaseline);
        }

        private readonly record struct Segment (int Start, int End, FontFace Face, float Size, ComputedStyle Style, float Shift);

        private readonly record struct Glyph (ushort Id, int Cluster, float Advance, float OffsetX, float OffsetY, int Segment);
    }

    /// <summary>One line's character range: [Start, End) with trailing spaces, [Start, ContentEnd) without.</summary>
    internal readonly record struct LineRange (int Start, int End, int ContentEnd, float Width, bool Hyphenated, bool EndsParagraph);

    internal static class CharExtensions
    {
        public static int ConvertToUtf32OrSelf (this string text, int index, out int length)
        {
            if (char.IsHighSurrogate (text[index]) && index + 1 < text.Length && char.IsLowSurrogate (text[index + 1])) {
                length = 2;
                return char.ConvertToUtf32 (text[index], text[index + 1]);
            }
            length = 1;
            return text[index];
        }
    }
}

