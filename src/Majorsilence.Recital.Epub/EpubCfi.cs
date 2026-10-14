using System.Globalization;
using System.Text;

namespace Majorsilence.Recital.Epub
{
    /// <summary>One CFI path step: an even index selects a child element, an odd one the text between elements.</summary>
    public sealed record CfiStep (int Index, string? Id = null)
    {
        public bool IsElement => Index % 2 == 0;
    }

    /// <summary>
    /// A character offset, with its optional bracketed assertion kept verbatim (escaped) so it round-trips:
    /// a text-location assertion (<c>pre,post</c>) and/or a side bias (<c>;s=a</c> / <c>;s=b</c>).
    /// </summary>
    public sealed record CfiOffset (int Character, string? Assertion = null)
    {
        /// <summary>'a' (after) or 'b' (before) from a <c>;s=</c> parameter, if present.</summary>
        public char? SideBias {
            get {
                if (Assertion is null)
                    return null;
                var at = Assertion.IndexOf (";s=", StringComparison.Ordinal);
                return at >= 0 && at + 3 < Assertion.Length ? Assertion[at + 3] : null;
            }
        }
    }

    /// <summary>
    /// A location in a book: a sequence of steps, where <see cref="Indirections"/> marks the steps after
    /// which a "!" moves into the referenced document, ending in an optional character offset. For an
    /// EPUB, the steps before the one indirection select the spine itemref, and those after it select a
    /// node in that content document.
    /// </summary>
    public sealed class CfiPoint : IComparable<CfiPoint>, IEquatable<CfiPoint>
    {
        public CfiPoint (IReadOnlyList<CfiStep> steps, IReadOnlyList<int> indirections, CfiOffset? offset)
        {
            if (steps.Count == 0)
                throw new ArgumentException ("A CFI needs at least one step.", nameof (steps));
            foreach (var at in indirections)
                if (at < 1 || at > steps.Count)
                    throw new ArgumentException ("Indirections must follow a step.", nameof (indirections));

            Steps = steps;
            Indirections = indirections;
            Offset = offset;
        }

        public IReadOnlyList<CfiStep> Steps { get; }

        /// <summary>Step counts after which a "!" occurs (each between 1 and <c>Steps.Count</c>).</summary>
        public IReadOnlyList<int> Indirections { get; }

        public CfiOffset? Offset { get; }

        /// <summary>Steps before the first indirection: for an EPUB, spine element then itemref.</summary>
        public IEnumerable<CfiStep> PackageSteps => Indirections.Count == 0 ? Steps : Steps.Take (Indirections[0]);

        /// <summary>Steps after the first indirection: the path within the content document.</summary>
        public IEnumerable<CfiStep> ContentSteps => Indirections.Count == 0 ? [] : Steps.Skip (Indirections[0]);

        public int CompareTo (CfiPoint? other) => other is null ? 1 : EpubCfi.Compare (Tokens (), Offset, other.Tokens (), other.Offset);

        public bool Equals (CfiPoint? other) => other is not null && CompareTo (other) == 0;

        public override bool Equals (object? obj) => Equals (obj as CfiPoint);

        public override int GetHashCode ()
        {
            var hash = new HashCode ();
            foreach (var token in Tokens ())
                hash.Add (token);
            hash.Add (Offset?.Character ?? -1);
            return hash.ToHashCode ();
        }

        public override string ToString () => EpubCfi.Wrap (EpubCfi.FormatTokens (Tokens (withIds: true), 0, Offset));

        public static bool operator == (CfiPoint? a, CfiPoint? b) => a is null ? b is null : a.Equals (b);
        public static bool operator != (CfiPoint? a, CfiPoint? b) => !(a == b);
        public static bool operator < (CfiPoint a, CfiPoint b) => a.CompareTo (b) < 0;
        public static bool operator > (CfiPoint a, CfiPoint b) => a.CompareTo (b) > 0;
        public static bool operator <= (CfiPoint a, CfiPoint b) => a.CompareTo (b) <= 0;
        public static bool operator >= (CfiPoint a, CfiPoint b) => a.CompareTo (b) >= 0;

        /// <summary>Steps and indirections as one sequence: a step's index, or -1 for "!".</summary>
        internal List<int> Tokens () => Tokens (withIds: false).Select (t => t.Index).ToList ();

        internal List<CfiStep> Tokens (bool withIds)
        {
            var tokens = new List<CfiStep> (Steps.Count + Indirections.Count);
            var next = 0;

            for (var i = 0; i < Steps.Count; i++) {
                tokens.Add (withIds ? Steps[i] : Steps[i] with { Id = null });
                while (next < Indirections.Count && Indirections[next] == i + 1) {
                    tokens.Add (EpubCfi.Indirection);
                    next++;
                }
            }

            return tokens;
        }
    }

    /// <summary>
    /// An EPUB Canonical Fragment Identifier (EPUB CFI 1.1): a point, or a range between two points.
    ///
    /// Supports path steps with id assertions, indirection ("!"), character offsets with text-location
    /// and side-bias assertions, and ranges. Temporal ("~") and spatial ("@") offsets are rejected.
    /// Points order in document order; character offsets count UTF-16 code units, as the DOM does.
    /// </summary>
    public sealed class EpubCfi : IComparable<EpubCfi>, IEquatable<EpubCfi>
    {
        internal static readonly CfiStep Indirection = new (-1);

        private EpubCfi (CfiPoint start, CfiPoint? end)
        {
            Start = start;
            End = end;
        }

        public CfiPoint Start { get; }

        /// <summary>The end of a range; null for a point.</summary>
        public CfiPoint? End { get; }

        public bool IsRange => End is not null;

        public static EpubCfi Point (CfiPoint point) => new (point, null);

        public static EpubCfi Range (CfiPoint start, CfiPoint end)
            => start.CompareTo (end) <= 0 ? new (start, end) : new (end, start);

        public static EpubCfi Parse (string text)
            => TryParse (text, out var cfi, out var error) ? cfi : throw new FormatException (error);

        public static bool TryParse (string text, out EpubCfi cfi) => TryParse (text, out cfi, out _);

        public static bool TryParse (string text, out EpubCfi cfi, out string error)
        {
            cfi = null!;
            text = text.Trim ();

            const string prefix = "epubcfi(";
            if (!text.StartsWith (prefix, StringComparison.Ordinal) || !text.EndsWith (')')) {
                error = "A CFI looks like epubcfi(...).";
                return false;
            }

            var reader = new CfiReader (text[prefix.Length..^1]);

            try {
                var parent = reader.ReadPath (requireStep: true);

                if (reader.AtEnd) {
                    cfi = new EpubCfi (parent.ToPoint (), null);
                    error = string.Empty;
                    return true;
                }

                reader.Expect (',');
                if (parent.Offset is not null)
                    throw new FormatException ("A range's parent path cannot end in an offset.");
                var start = reader.ReadPath (requireStep: false);
                reader.Expect (',');
                var end = reader.ReadPath (requireStep: false);

                if (!reader.AtEnd)
                    throw new FormatException ($"Unexpected '{reader.Rest}'.");

                cfi = Range (parent.Append (start).ToPoint (), parent.Append (end).ToPoint ());
                error = string.Empty;
                return true;
            } catch (FormatException ex) {
                error = $"Invalid CFI '{text}': {ex.Message}";
                return false;
            }
        }

        public override string ToString ()
        {
            if (End is null)
                return Start.ToString ();

            // Factor out the longest common prefix of steps (and indirections) as the parent path.
            var start = Start.Tokens (withIds: true);
            var end = End.Tokens (withIds: true);
            var common = 0;

            while (common < start.Count && common < end.Count && start[common] == end[common])
                common++;

            // Each local path needs a step or an offset.
            while (common > 0 && (common == start.Count && Start.Offset is null || common == end.Count && End.Offset is null))
                common--;

            // The parent path must end in a step: a trailing "!" moves into the local paths.
            while (common > 0 && start[common - 1] == Indirection)
                common--;

            // ...and must keep at least one step.
            common = Math.Max (common, 1);

            return Wrap (FormatTokens (start.Take (common).ToList (), 0, null)
                + "," + FormatTokens (start, common, Start.Offset)
                + "," + FormatTokens (end, common, End.Offset));
        }

        public int CompareTo (EpubCfi? other)
        {
            if (other is null)
                return 1;

            var byStart = Start.CompareTo (other.Start);
            if (byStart != 0)
                return byStart;

            return (End, other.End) switch {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => End.CompareTo (other.End),
            };
        }

        public bool Equals (EpubCfi? other) => other is not null && CompareTo (other) == 0;

        public override bool Equals (object? obj) => Equals (obj as EpubCfi);

        public override int GetHashCode () => HashCode.Combine (Start, End);

        public static bool operator == (EpubCfi? a, EpubCfi? b) => a is null ? b is null : a.Equals (b);
        public static bool operator != (EpubCfi? a, EpubCfi? b) => !(a == b);
        public static bool operator < (EpubCfi a, EpubCfi b) => a.CompareTo (b) < 0;
        public static bool operator > (EpubCfi a, EpubCfi b) => a.CompareTo (b) > 0;
        public static bool operator <= (EpubCfi a, EpubCfi b) => a.CompareTo (b) <= 0;
        public static bool operator >= (EpubCfi a, EpubCfi b) => a.CompareTo (b) >= 0;

        internal static string Wrap (string body) => "epubcfi(" + body + ")";

        internal static int Compare (List<int> a, CfiOffset? aOffset, List<int> b, CfiOffset? bOffset)
        {
            var count = Math.Min (a.Count, b.Count);

            for (var i = 0; i < count; i++) {
                if (a[i] == b[i])
                    continue;

                // Within one document both sides have their "!" in the same place; if not, put the
                // indirection first so the order stays total.
                if (a[i] == -1)
                    return -1;
                if (b[i] == -1)
                    return 1;
                return a[i].CompareTo (b[i]);
            }

            // An ancestor comes before anything inside it.
            if (a.Count != b.Count)
                return a.Count.CompareTo (b.Count);

            return (aOffset?.Character ?? -1).CompareTo (bOffset?.Character ?? -1);
        }

        internal static string FormatTokens (List<CfiStep> tokens, int from, CfiOffset? offset)
        {
            var builder = new StringBuilder ();

            for (var i = from; i < tokens.Count; i++) {
                if (tokens[i] == Indirection) {
                    builder.Append ('!');
                    continue;
                }

                builder.Append ('/').Append (tokens[i].Index.ToString (CultureInfo.InvariantCulture));
                if (tokens[i].Id is { Length: > 0 } id)
                    builder.Append ('[').Append (Escape (id)).Append (']');
            }

            if (offset is not null) {
                builder.Append (':').Append (offset.Character.ToString (CultureInfo.InvariantCulture));
                if (offset.Assertion is not null)
                    builder.Append ('[').Append (offset.Assertion).Append (']');
            }

            return builder.ToString ();
        }

        /// <summary>Escapes CFI special characters with '^'.</summary>
        public static string Escape (string value)
        {
            var builder = new StringBuilder (value.Length);
            foreach (var c in value) {
                if (c is '^' or '[' or ']' or '(' or ')' or ',' or ';' or '=')
                    builder.Append ('^');
                builder.Append (c);
            }
            return builder.ToString ();
        }

        /// <summary>A parsed (possibly partial, for range locals) path.</summary>
        private sealed class ParsedPath
        {
            public List<CfiStep> Tokens { get; } = [];
            public CfiOffset? Offset { get; set; }

            public ParsedPath Append (ParsedPath local)
            {
                var combined = new ParsedPath { Offset = local.Offset };
                combined.Tokens.AddRange (Tokens);
                combined.Tokens.AddRange (local.Tokens);
                return combined;
            }

            public CfiPoint ToPoint ()
            {
                var steps = new List<CfiStep> ();
                var indirections = new List<int> ();

                foreach (var token in Tokens) {
                    if (token == Indirection) {
                        if (steps.Count == 0 || indirections.Contains (steps.Count))
                            throw new FormatException ("'!' must follow a step.");
                        indirections.Add (steps.Count);
                    } else {
                        steps.Add (token);
                    }
                }

                if (steps.Count == 0)
                    throw new FormatException ("A CFI needs at least one step.");
                if (indirections.Count > 0 && indirections[^1] == steps.Count && Offset is null)
                    throw new FormatException ("'!' must be followed by a step or an offset.");

                return new CfiPoint (steps, indirections, Offset);
            }
        }

        private ref struct CfiReader
        {
            private readonly ReadOnlySpan<char> text;
            private int position;

            public CfiReader (string text)
            {
                this.text = text;
                position = 0;
            }

            public readonly bool AtEnd => position >= text.Length;
            public readonly string Rest => text[position..].ToString ();

            public void Expect (char c)
            {
                if (AtEnd || text[position] != c)
                    throw new FormatException (AtEnd ? $"Expected '{c}' at the end." : $"Expected '{c}' at '{Rest}'.");
                position++;
            }

            public ParsedPath ReadPath (bool requireStep)
            {
                var path = new ParsedPath ();

                while (!AtEnd) {
                    var c = text[position];

                    if (c == '/') {
                        position++;
                        var index = ReadInteger ();
                        string? id = null;
                        if (!AtEnd && text[position] == '[')
                            id = Unescape (ReadBracketed ());
                        path.Tokens.Add (new CfiStep (index, id));
                    } else if (c == '!') {
                        position++;
                        path.Tokens.Add (Indirection);
                    } else if (c == ':') {
                        position++;
                        var character = ReadInteger ();
                        string? assertion = null;
                        if (!AtEnd && text[position] == '[')
                            assertion = ReadBracketed ();
                        path.Offset = new CfiOffset (character, assertion);
                        break;
                    } else if (c is '~' or '@') {
                        throw new FormatException ("Temporal (~) and spatial (@) offsets are not supported.");
                    } else {
                        break;
                    }
                }

                if (requireStep && !path.Tokens.Any (t => t != Indirection))
                    throw new FormatException ("A CFI path starts with a step.");
                if (!requireStep && path.Tokens.Count == 0 && path.Offset is null)
                    throw new FormatException ("A range's local path is empty.");

                return path;
            }

            private int ReadInteger ()
            {
                var start = position;
                while (!AtEnd && char.IsAsciiDigit (text[position]))
                    position++;

                if (start == position)
                    throw new FormatException ($"Expected a number at '{Rest}'.");

                var digits = text[start..position];
                if (digits.Length > 1 && digits[0] == '0')
                    throw new FormatException ($"'{digits}' has a leading zero.");

                return int.TryParse (digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                    ? value
                    : throw new FormatException ($"'{digits}' is too large.");
            }

            /// <summary>Reads "[...]" and returns the raw (still escaped) content.</summary>
            private string ReadBracketed ()
            {
                Expect ('[');
                var start = position;

                while (!AtEnd && text[position] != ']') {
                    if (text[position] == '^')
                        position++;
                    position++;
                }

                if (AtEnd)
                    throw new FormatException ("Unclosed '['.");

                var raw = text[start..position].ToString ();
                position++;
                return raw;
            }

            private static string Unescape (string raw)
            {
                var builder = new StringBuilder (raw.Length);
                for (var i = 0; i < raw.Length; i++) {
                    if (raw[i] == '^' && i + 1 < raw.Length)
                        i++;
                    builder.Append (raw[i]);
                }
                return builder.ToString ();
            }
        }
    }
}
