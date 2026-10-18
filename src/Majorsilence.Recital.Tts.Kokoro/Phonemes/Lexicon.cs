using System.IO.Compression;
using System.Text.Json;

namespace Majorsilence.Recital.Tts.Kokoro.Phonemes
{
    /// <summary>What the word after the one being looked up starts with: some words are said differently before a vowel.</summary>
    internal readonly record struct WordContext (bool? FutureVowel, bool FutureTo);

    /// <summary>
    /// English pronunciations from misaki's dictionaries (Apache-2.0), with misaki's rules for what they lack:
    /// regular -s, -ed and -ing forms, initialisms spelled out, and the stress a capital adds. A C# port of the
    /// <c>Lexicon</c> class in misaki/en.py, without the part-of-speech tagger: a word that is said differently
    /// as noun and verb gets its default reading.
    /// </summary>
    internal sealed class Lexicon
    {
        internal const char Primary = 'ˈ';
        internal const char Secondary = 'ˌ';
        internal const string Vowels = "AIOQWYaiuæɑɒɔəɛɜɪʊʌᵻ";
        internal const string Consonants = "bdfhjklmnpstvwzðŋɡɹɾʃʒʤʧθ";
        private const string Diphthongs = "AIOQWYʤʧ";
        private const string UsTaus = "AIOWYiuæɑəɛɪɹʊʌ";

        private static readonly Dictionary<string, string> Symbols = new () { ["%"] = "percent", ["&"] = "and", ["+"] = "plus", ["@"] = "at" };

        private readonly Dictionary<string, Entry> golds;
        private readonly Dictionary<string, Entry> silvers;

        /// <summary>A dictionary entry: one pronunciation, or one per part of speech with a default.</summary>
        private sealed record Entry (string? Default, Dictionary<string, string?>? ByTag);

        private Lexicon (Dictionary<string, Entry> golds, Dictionary<string, Entry> silvers)
        {
            this.golds = golds;
            this.silvers = silvers;
        }

        /// <summary>Loads the gzipped gold (hand-checked) and silver (generated) dictionaries.</summary>
        public static Lexicon Load (Stream gold, Stream silver) => new (Read (gold), Read (silver));

        private static Dictionary<string, Entry> Read (Stream gzipped)
        {
            using var json = new GZipStream (gzipped, CompressionMode.Decompress);
            using var document = JsonDocument.Parse (json);
            var entries = new Dictionary<string, Entry> (StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject ()) {
                var value = property.Value;
                entries[property.Name] = value.ValueKind == JsonValueKind.Object
                    ? new Entry (null, value.EnumerateObject ().ToDictionary (p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString (), StringComparer.Ordinal))
                    : new Entry (value.GetString (), null);
            }
            return Grow (entries);
        }

        // misaki's grow_dictionary: "word" also answers for "Word", and "Word" for "word", unless listed itself.
        private static Dictionary<string, Entry> Grow (Dictionary<string, Entry> entries)
        {
            var grown = new Dictionary<string, Entry> (entries.Count * 2, StringComparer.Ordinal);
            foreach (var (key, value) in entries) {
                if (key.Length < 2)
                    continue;
                var lower = key.ToLowerInvariant ();
                if (key == lower) {
                    var capital = Capitalize (key);
                    if (capital != key)
                        grown[capital] = value;
                } else if (key == Capitalize (lower)) {
                    grown[lower] = value;
                }
            }
            foreach (var (key, value) in entries)
                grown[key] = value;
            return grown;
        }

        internal static string Capitalize (string s) => s.Length == 0 ? s : char.ToUpperInvariant (s[0]) + s[1..].ToLowerInvariant ();

        public bool Contains (string word) => golds.ContainsKey (word) || silvers.ContainsKey (word);

        /// <summary>The pronunciation of one word token, or null when only the fallback network can help.</summary>
        public string? Word (string word, WordContext ctx)
        {
            word = word.Replace ('‘', '\'').Replace ('’', '\'');
            double? stress = NoUpper (word) ? null : NoLower (word) ? 2 : 0.5;
            return GetWord (word, null, stress, ctx);
        }

        private string? GetWord (string word, string? tag, double? stress, WordContext ctx)
        {
            if (SpecialCase (word, tag, stress, ctx) is { } special)
                return special;

            var lower = word.ToLowerInvariant ();
            if (word.Length > 1 && IsAlpha (word.Replace ("'", "")) && word != lower
                && !golds.ContainsKey (word) && !silvers.ContainsKey (word)
                && (NoLower (word) || NoUpper (word[1..]))
                && (golds.ContainsKey (lower) || silvers.ContainsKey (lower)
                    || StemS (lower, tag, stress, ctx) is not null || StemEd (lower, tag, stress, ctx) is not null || StemIng (lower, tag, stress, ctx) is not null))
                word = lower;

            if (IsKnown (word))
                return Lookup (word, tag, stress, ctx);
            if (word.EndsWith ("s'", StringComparison.Ordinal) && IsKnown (word[..^2] + "'s"))
                return Lookup (word[..^2] + "'s", tag, stress, ctx);
            if (word.EndsWith ('\'') && IsKnown (word[..^1]))
                return Lookup (word[..^1], tag, stress, ctx);
            return StemS (word, tag, stress, ctx)
                ?? StemEd (word, tag, stress, ctx)
                ?? StemIng (word, tag, stress ?? 0.5, ctx);
        }

        // The small words whose reading depends on what follows, and symbols said as words. With no tagger, "a" is
        // the article and "I" the pronoun.
        private string? SpecialCase (string word, string? tag, double? stress, WordContext ctx)
        {
            if (Symbols.TryGetValue (word, out var symbol))
                return Lookup (symbol, null, null, ctx);
            var trimmed = word.Trim ('.');
            if (trimmed.Contains ('.') && IsAlpha (word.Replace (".", "")) && word.Split ('.').Max (p => p.Length) < 3)
                return Initialism (word);
            switch (word) {
            case "a" or "A":
                return "ɐ";
            case "am" or "Am" or "AM":
                return ctx.FutureVowel is null || word != "am" || stress > 0 ? Default ("am") : "ɐm";
            case "an" or "An" or "AN":
                return "ɐn";
            case "I":
                return $"{Secondary}I";
            case "to" or "To" or "TO":
                return ctx.FutureVowel switch { null => Default ("to"), false => "tə", true => "tʊ" };
            case "in" or "In" or "IN":
                return (ctx.FutureVowel is null ? Primary.ToString () : "") + "ɪn";
            case "the" or "The" or "THE":
                return ctx.FutureVowel == true ? "ði" : "ðə";
            case "vs" or "vs." or "Vs" or "Vs.":
                return Lookup ("versus", null, null, ctx);
            case "used" or "Used" or "USED":
                return golds["used"].ByTag is { } forms && ctx.FutureTo && forms.TryGetValue ("VBD", out var past) ? past : Default ("used");
            }
            return null;
        }

        private string? Default (string word) => golds.TryGetValue (word, out var entry) ? entry.Default ?? entry.ByTag?["DEFAULT"] : null;

        private bool IsKnown (string word)
        {
            if (golds.ContainsKey (word) || Symbols.ContainsKey (word) || silvers.ContainsKey (word))
                return true;
            if (!IsAlpha (word) || !word.All (IsLexiconChar))
                return false;
            if (word.Length == 1)
                return true;
            if (NoLower (word) && golds.ContainsKey (word.ToLowerInvariant ()))
                return true;
            // Capitals after the first letter: an initialism, spelled out.
            return NoLower (word[1..]);
        }

        private string? Lookup (string word, string? tag, double? stress, WordContext? ctx)
        {
            var spellOut = false;
            if (NoLower (word) && !golds.ContainsKey (word)) {
                word = word.ToLowerInvariant ();
                spellOut = tag == "NNP";
            }
            string? ps = null;
            if (golds.TryGetValue (word, out var entry) || (!spellOut && silvers.TryGetValue (word, out entry)))
                ps = Pronunciation (entry, tag, ctx);
            if (ps is null || (spellOut && !ps.Contains (Primary))) {
                if (Initialism (word) is { } spelled)
                    return spelled;
            }
            return ApplyStress (ps, stress);
        }

        private static string? Pronunciation (Entry entry, string? tag, WordContext? ctx)
        {
            if (entry.ByTag is not { } forms)
                return entry.Default;
            if (ctx is { FutureVowel: null } && forms.ContainsKey ("None"))
                tag = "None";
            else if (tag is null || !forms.ContainsKey (tag))
                tag = ParentTag (tag);
            return tag is not null && forms.TryGetValue (tag, out var form) ? form : forms["DEFAULT"];
        }

        private static string? ParentTag (string? tag) => tag switch {
            null => null,
            _ when tag.StartsWith ("VB", StringComparison.Ordinal) => "VERB",
            _ when tag.StartsWith ("NN", StringComparison.Ordinal) => "NOUN",
            _ when tag.StartsWith ("ADV", StringComparison.Ordinal) || tag.StartsWith ("RB", StringComparison.Ordinal) => "ADV",
            _ when tag.StartsWith ("ADJ", StringComparison.Ordinal) || tag.StartsWith ("JJ", StringComparison.Ordinal) => "ADJ",
            _ => tag,
        };

        // Each letter's name, with the last one stressed: "BBC", "U.S.".
        private string? Initialism (string word)
        {
            var letters = new System.Text.StringBuilder ();
            foreach (var c in word) {
                if (!char.IsLetter (c))
                    continue;
                if (!golds.TryGetValue (char.ToUpperInvariant (c).ToString (), out var letter) || letter.Default is null)
                    return null;
                letters.Append (letter.Default);
            }
            var ps = ApplyStress (letters.ToString (), 0)!;
            var last = ps.LastIndexOf (Secondary);
            return last < 0 ? ps : ps[..last] + Primary + ps[(last + 1)..];
        }

        // ── Regular endings (misaki's stem_s, stem_ed, stem_ing) ────────────────────────────────────

        internal static string? AddS (string? stem) => string.IsNullOrEmpty (stem) ? null
            : "ptkfθ".Contains (stem[^1]) ? stem + "s"
            : "szʃʒʧʤ".Contains (stem[^1]) ? stem + "ᵻz"
            : stem + "z";

        private string? StemS (string word, string? tag, double? stress, WordContext ctx)
        {
            if (word.Length < 3 || !word.EndsWith ('s'))
                return null;
            string stem;
            if (!word.EndsWith ("ss", StringComparison.Ordinal) && IsKnown (word[..^1]))
                stem = word[..^1];
            else if ((word.EndsWith ("'s", StringComparison.Ordinal) || (word.Length > 4 && word.EndsWith ("es", StringComparison.Ordinal) && !word.EndsWith ("ies", StringComparison.Ordinal))) && IsKnown (word[..^2]))
                stem = word[..^2];
            else if (word.Length > 4 && word.EndsWith ("ies", StringComparison.Ordinal) && IsKnown (word[..^3] + "y"))
                stem = word[..^3] + "y";
            else
                return null;
            return AddS (Lookup (stem, tag, stress, ctx));
        }

        internal static string? AddEd (string? stem)
        {
            if (string.IsNullOrEmpty (stem))
                return null;
            var last = stem[^1];
            if ("pkfθʃsʧ".Contains (last))
                return stem + "t";
            if (last == 'd')
                return stem + "ᵻd";
            if (last != 't')
                return stem + "d";
            if (stem.Length < 2)
                return stem + "ɪd";
            return UsTaus.Contains (stem[^2]) ? stem[..^1] + "ɾᵻd" : stem + "ᵻd";
        }

        private string? StemEd (string word, string? tag, double? stress, WordContext ctx)
        {
            if (word.Length < 4 || !word.EndsWith ('d'))
                return null;
            string stem;
            if (!word.EndsWith ("dd", StringComparison.Ordinal) && IsKnown (word[..^1]))
                stem = word[..^1];
            else if (word.Length > 4 && word.EndsWith ("ed", StringComparison.Ordinal) && !word.EndsWith ("eed", StringComparison.Ordinal) && IsKnown (word[..^2]))
                stem = word[..^2];
            else
                return null;
            return AddEd (Lookup (stem, tag, stress, ctx));
        }

        internal static string? AddIng (string? stem) => string.IsNullOrEmpty (stem) ? null
            : stem.Length > 1 && stem[^1] == 't' && UsTaus.Contains (stem[^2]) ? stem[..^1] + "ɾɪŋ"
            : stem + "ɪŋ";

        private string? StemIng (string word, string? tag, double? stress, WordContext ctx)
        {
            if (word.Length < 5 || !word.EndsWith ("ing", StringComparison.Ordinal))
                return null;
            string stem;
            if (word.Length > 5 && IsKnown (word[..^3]))
                stem = word[..^3];
            else if (IsKnown (word[..^3] + "e"))
                stem = word[..^3] + "e";
            else if (word.Length > 5 && DoubledConsonantIng (word) && IsKnown (word[..^4]))
                stem = word[..^4];
            else
                return null;
            return AddIng (Lookup (stem, tag, stress, ctx));
        }

        // misaki: ([bcdgklmnprstvxz])\1ing$|cking$
        private static bool DoubledConsonantIng (string word)
            => word.EndsWith ("cking", StringComparison.Ordinal)
               || (word.Length >= 5 && word[^4] == word[^5] && "bcdgklmnprstvxz".Contains (word[^4]));

        /// <summary>A number word ("twenty", "first", "point"), as <see cref="Lookup"/> would say it.</summary>
        internal string? NumberWord (string word, double? stress = null) => Lookup (word, null, stress, null);

        // ── Stress (misaki's apply_stress) ──────────────────────────────────────────────────────────

        internal static string? ApplyStress (string? ps, double? stress)
        {
            if (ps is null || stress is not { } s)
                return ps;
            var hasPrimary = ps.Contains (Primary);
            var hasAny = hasPrimary || ps.Contains (Secondary);
            if (s < -1)
                return ps.Replace (Primary.ToString (), "").Replace (Secondary.ToString (), "");
            if (s == -1 || (s is 0 or -0.5 && hasPrimary))
                return ps.Replace (Secondary.ToString (), "").Replace (Primary, Secondary);
            if (s is 0 or 0.5 or 1 && !hasAny)
                return ps.Any (c => Vowels.Contains (c)) ? Restress (Secondary + ps) : ps;
            if (s >= 1 && !hasPrimary && hasAny)
                return ps.Replace (Secondary, Primary);
            if (s > 1 && !hasAny)
                return ps.Any (c => Vowels.Contains (c)) ? Restress (Primary + ps) : ps;
            return ps;
        }

        // Moves each stress mark to just before the vowel it belongs to.
        private static string Restress (string ps)
        {
            var placed = new List<(double Position, char C)> ();
            for (var i = 0; i < ps.Length; i++) {
                double position = i;
                if (ps[i] is Primary or Secondary) {
                    var vowel = ps.IndexOfAny (Vowels.ToCharArray (), i);
                    if (vowel >= 0)
                        position = vowel - 0.5;
                }
                placed.Add ((position, ps[i]));
            }
            return new string ([.. placed.OrderBy (p => p.Position).Select (p => p.C)]);
        }

        internal static int StressWeight (string ps) => ps.Sum (c => Diphthongs.Contains (c) ? 2 : 1);

        // Whether a word has no capitals (or no lower-case letters): misaki compares a word with its own case change.
        internal static bool NoUpper (string s) => !s.Any (char.IsUpper);
        internal static bool NoLower (string s) => !s.Any (char.IsLower);

        internal static bool IsAlpha (string s) => s.Length > 0 && s.All (char.IsLetter);

        // misaki's LEXICON_ORDS: the apostrophe, the hyphen and the ASCII letters.
        private static bool IsLexiconChar (char c) => c is '\'' or '-' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
    }
}
