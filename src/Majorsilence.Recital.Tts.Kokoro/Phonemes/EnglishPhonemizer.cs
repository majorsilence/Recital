using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Majorsilence.Recital.Tts.Kokoro.Phonemes
{
    /// <summary>Phonemes for a text, with where each word's phonemes start (for estimating when it is spoken).</summary>
    /// <param name="Words">Each spoken word: its first character in the text and its first phoneme.</param>
    public sealed record PhonemizedText (string Phonemes, IReadOnlyList<(int CharIndex, int PhonemeIndex)> Words);

    /// <summary>
    /// American English text to the phonemes Kokoro reads (misaki's alphabet): dictionary first, then the regular
    /// endings and numbers, then the letters-to-phonemes network for names and rare words. Follows misaki/en.py's
    /// <c>G2P</c> with a plain tokenizer in place of spaCy.
    /// </summary>
    public sealed partial class EnglishPhonemizer
    {
        private const string Punctuation = ";:,.!?—…\"“”()";
        private const string NonQuotePunctuation = ";:,.!?—…";
        private static readonly Dictionary<char, (string Major, string Minor)> Currencies = new () { ['$'] = ("dollar", "cent"), ['£'] = ("pound", "pence"), ['€'] = ("euro", "cent") };

        // Titles and abbreviations written with a dot that is not a full stop.
        private static readonly HashSet<string> Abbreviations = new (StringComparer.Ordinal) {
            "Mr", "Mrs", "Ms", "Messrs", "Dr", "St", "Jr", "Sr", "Prof", "Rev", "Gen", "Col", "Capt", "Lt", "Sgt", "Mt", "No", "vs", "etc", "Esq", "Hon",
        };

        private readonly Lexicon lexicon;
        private readonly GraphemeNetwork network;
        // Names come back again and again in a book; the network's answer for each is kept.
        private readonly Dictionary<string, string> guesses = new (StringComparer.Ordinal);

        private EnglishPhonemizer (Lexicon lexicon, GraphemeNetwork network)
        {
            this.lexicon = lexicon;
            this.network = network;
        }

        /// <summary>Loads the American dictionaries and network shipped inside this assembly (a few hundred milliseconds).</summary>
        public static EnglishPhonemizer LoadAmerican () => Load ("us", british: false);

        /// <summary>Loads the British ones, for the British voices (bf_, bm_): misaki's gb dictionaries and its gb network.</summary>
        public static EnglishPhonemizer LoadBritish () => Load ("gb", british: true);

        private static EnglishPhonemizer Load (string accent, bool british)
        {
            var assembly = typeof (EnglishPhonemizer).Assembly;
            Stream Open (string name) => assembly.GetManifestResourceStream ("Majorsilence.Recital.Tts.Kokoro.Data." + name)
                ?? throw new InvalidOperationException ($"Missing resource {name}.");
            using var gold = Open ($"{accent}_gold.json.gz");
            using var silver = Open ($"{accent}_silver.json.gz");
            using var weights = Open ($"g2p_en_{accent}.safetensors");
            var network = british ? GraphemeNetwork.LoadBritish (weights) : GraphemeNetwork.LoadAmerican (weights);
            return new EnglishPhonemizer (Lexicon.Load (gold, silver, british), network);
        }

        /// <summary>The letters-to-phonemes network alone (for tests that check it against the dictionary).</summary>
        internal string? Guess (string word) => network.Phonemize (word);

        internal bool InDictionary (string word) => lexicon.Contains (word);

        private enum Kind { Word, Number, Currency, Punctuation, Dash, Space, Other }

        private sealed class Token (Kind kind, string text, int start)
        {
            public Kind Kind { get; } = kind;
            public string Text { get; } = text;
            public int Start { get; } = start;
            public string Phonemes { get; set; } = "";
            public string? Currency { get; set; }
        }

        // Initialisms with dots, words (inner apostrophes and hyphens split later), numbers with their endings,
        // currency signs, dashes, punctuation, spaces; anything else is skipped.
        [GeneratedRegex (@"(?<word>(?:\p{L}\.){2,}|[\p{L}\p{M}]+(?:['’][\p{L}\p{M}]+)*)|(?<number>\d[\d,]*(?:\.\d+)*(?:st|nd|rd|th|s|'s|ed|ing)?|\.\d+)|(?<currency>[$£€])|(?<dash>\s+[-–]\s+|--+|[—–])|(?<punct>\.\.\.|[;:,.!?…""“”()])|(?<space>\s+)|(?<other>.)", RegexOptions.CultureInvariant)]
        private static partial Regex TokenPattern ();

        public PhonemizedText Phonemize (string text)
        {
            text = text.Normalize (NormalizationForm.FormKC);
            var tokens = new List<Token> ();
            foreach (Match m in TokenPattern ().Matches (text)) {
                var kind = m.Groups["word"].Success ? Kind.Word : m.Groups["number"].Success ? Kind.Number : m.Groups["currency"].Success ? Kind.Currency
                    : m.Groups["dash"].Success ? Kind.Dash : m.Groups["punct"].Success ? Kind.Punctuation : m.Groups["space"].Success ? Kind.Space : Kind.Other;
                tokens.Add (new Token (kind, m.Value, m.Index));
            }

            // A currency sign belongs to the number after it; the dot after "Mr" or "Dr" ends no sentence.
            for (var i = 0; i + 1 < tokens.Count; i++) {
                if (tokens[i].Kind == Kind.Currency && tokens[i + 1].Kind == Kind.Number)
                    tokens[i + 1].Currency = tokens[i].Text;
                if (tokens[i].Kind == Kind.Word && tokens[i + 1].Text == "." && Abbreviations.Contains (tokens[i].Text))
                    tokens[i + 1] = new Token (Kind.Other, ".", tokens[i + 1].Start);
            }

            // Right to left, as misaki does: a word's reading can depend on whether the next starts with a vowel.
            var ctx = new WordContext (null, false);
            for (var i = tokens.Count - 1; i >= 0; i--) {
                var token = tokens[i];
                token.Phonemes = token.Kind switch {
                    Kind.Word => WordPhonemes (token.Text, ctx),
                    Kind.Number => NumberPhonemes (token.Text, token.Currency) ?? "",
                    Kind.Punctuation => token.Text == "..." ? "…" : token.Text.Replace ('"', '”'),
                    Kind.Dash => "—",
                    _ => "",
                };
                if (token.Kind is Kind.Word or Kind.Number or Kind.Punctuation or Kind.Dash)
                    ctx = Next (ctx, token);
            }

            var phonemes = new StringBuilder ();
            var words = new List<(int, int)> ();
            foreach (var token in tokens) {
                switch (token.Kind) {
                case Kind.Word or Kind.Number when token.Phonemes.Length > 0:
                    if (phonemes.Length > 0 && !char.IsWhiteSpace (phonemes[^1]) && !"(“".Contains (phonemes[^1]))
                        phonemes.Append (' ');
                    words.Add ((token.Start, phonemes.Length));
                    phonemes.Append (token.Phonemes);
                    break;
                case Kind.Punctuation:
                    // Opening marks take a space before them; closing ones attach to the word before.
                    if (token.Phonemes is "(" or "“" && phonemes.Length > 0 && !char.IsWhiteSpace (phonemes[^1]))
                        phonemes.Append (' ');
                    phonemes.Append (token.Phonemes);
                    break;
                case Kind.Dash:
                    if (phonemes.Length > 0 && !char.IsWhiteSpace (phonemes[^1]))
                        phonemes.Append (' ');
                    phonemes.Append ("— ");
                    break;
                case Kind.Space when phonemes.Length > 0 && !char.IsWhiteSpace (phonemes[^1]):
                    phonemes.Append (' ');
                    break;
                }
            }

            // Kokoro v1.0 was trained on misaki's older alphabet: the flap as T, the glottal stop as t.
            var result = phonemes.Replace ('ɾ', 'T').Replace ('ʔ', 't').ToString ();
            var trimmed = result.TrimEnd ();
            var lead = trimmed.Length - trimmed.TrimStart ().Length;
            return new PhonemizedText (trimmed.TrimStart (), [.. words.Select (w => (w.Item1, w.Item2 - lead))]);
        }

        private static WordContext Next (WordContext ctx, Token token)
        {
            bool? vowel = ctx.FutureVowel;
            foreach (var c in token.Phonemes) {
                if (NonQuotePunctuation.Contains (c)) {
                    vowel = null;
                    break;
                }
                if (Lexicon.Vowels.Contains (c)) {
                    vowel = true;
                    break;
                }
                if (Lexicon.Consonants.Contains (c)) {
                    vowel = false;
                    break;
                }
            }
            return new WordContext (vowel, token.Text is "to" or "To" or "TO");
        }

        private string WordPhonemes (string word, WordContext ctx)
        {
            if (lexicon.Word (word, ctx) is { } known)
                return known;
            // Hyphenated: each part on its own. (A contraction the dictionary lacks goes to the network whole.)
            var parts = word.Split ('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
                return string.Join (" ", parts.Select (p => WordPhonemes (p, ctx)).Where (p => p.Length > 0));
            var plain = RemoveDiacritics (word).Replace ('’', '\'');
            if (plain != word && lexicon.Word (plain, ctx) is { } unaccented)
                return unaccented;
            lock (guesses) {
                if (guesses.TryGetValue (plain, out var cached))
                    return cached;
            }
            var guess = network.Phonemize (plain) ?? "";
            lock (guesses) {
                if (guesses.Count > 20_000)
                    guesses.Clear ();
                guesses[plain] = guess;
            }
            return guess;
        }

        private static string RemoveDiacritics (string word)
        {
            var decomposed = word.Normalize (NormalizationForm.FormD);
            var kept = new StringBuilder (decomposed.Length);
            foreach (var c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory (c) != UnicodeCategory.NonSpacingMark)
                    kept.Append (c);
            return kept.ToString ().Normalize (NormalizationForm.FormC);
        }

        // misaki's get_number, for a number that stands alone (no part-of-speech flags).
        private string? NumberPhonemes (string word, string? currency)
        {
            string? suffix = null;
            foreach (var s in new[] { "'s", "ing", "ed", "st", "nd", "rd", "th", "s" })
                if (word.EndsWith (s, StringComparison.Ordinal)) {
                    suffix = s;
                    word = word[..^s.Length];
                    break;
                }

            var parts = new List<string?> ();
            void Say (string words)
            {
                foreach (var w in words.Split ([' ', '-'], StringSplitOptions.RemoveEmptyEntries))
                    parts.Add (lexicon.NumberWord (w, w == "point" ? -2 : null));
            }

            var digits = word.Replace (",", "");
            var isInteger = digits.Length > 0 && digits.All (char.IsAsciiDigit);
            if (isInteger && suffix is "st" or "nd" or "rd" or "th" && NumberWords.TryParse (digits, out var ordinal)) {
                Say (NumberWords.Ordinal (ordinal));
                suffix = null;
            } else if (word.Length == 4 && isInteger && currency is null && NumberWords.TryParse (word, out var year)) {
                Say (NumberWords.Year ((int) year));
            } else if (word.Count (c => c == '.') > 1) {
                // A version or section number: each part on its own.
                foreach (var part in digits.Split ('.', StringSplitOptions.RemoveEmptyEntries)) {
                    if (part[0] == '0' || (part.Length != 2 && part[1..].Any (c => c != '0')))
                        foreach (var d in part)
                            Say (NumberWords.Cardinal (d - '0'));
                    else if (NumberWords.TryParse (part, out var n))
                        Say (NumberWords.Cardinal (n));
                }
            } else if (currency is { } sign && Currencies.TryGetValue (sign[0], out var units) && IsMoney (digits)) {
                var split = digits.Split ('.');
                if (!NumberWords.TryParse (split[0].Length == 0 ? "0" : split[0], out var major))
                    return null;
                long minor = 0;
                if (split.Length > 1 && !NumberWords.TryParse (split[1], out minor))
                    return null;
                var amounts = new List<(long Amount, string Unit)> { (major, units.Major) };
                if (minor != 0)
                    amounts.Add ((minor, units.Minor));
                if (major == 0 && minor != 0)
                    amounts.RemoveAt (0);
                for (var i = 0; i < amounts.Count; i++) {
                    if (i > 0)
                        parts.Add (lexicon.NumberWord ("and"));
                    Say (NumberWords.Cardinal (amounts[i].Amount));
                    var unit = lexicon.NumberWord (amounts[i].Unit);
                    parts.Add (amounts[i].Amount != 1 && amounts[i].Unit != "pence" ? Lexicon.AddS (unit, lexicon.British) : unit);
                }
            } else if (isInteger && NumberWords.TryParse (digits, out var cardinal)) {
                Say (NumberWords.Cardinal (cardinal));
            } else if (digits.Contains ('.')) {
                var dot = digits.IndexOf ('.');
                if (dot > 0 && NumberWords.TryParse (digits[..dot], out var whole))
                    Say (NumberWords.Cardinal (whole));
                Say ("point");
                foreach (var d in digits[(dot + 1)..])
                    Say (NumberWords.Cardinal (d - '0'));
            }

            if (parts.Count == 0 || parts.Any (p => p is null))
                return null;
            var result = string.Join (" ", parts);
            return suffix switch {
                "s" or "'s" => Lexicon.AddS (result, lexicon.British),
                "ed" => Lexicon.AddEd (result, lexicon.British),
                "ing" => Lexicon.AddIng (result, lexicon.British),
                _ => result,
            };
        }

        private static bool IsMoney (string digits)
        {
            var dots = digits.Count (c => c == '.');
            return dots == 0 || (dots == 1 && digits.Split ('.')[1].Length < 3);
        }
    }
}
