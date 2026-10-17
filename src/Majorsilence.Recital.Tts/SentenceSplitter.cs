namespace Majorsilence.Recital.Tts
{
    /// <summary>
    /// Rule-based sentence boundaries: a terminator (. ! ? …), any closing quotes or brackets, then space and
    /// something that starts a sentence. Abbreviations (Mr., Dr., St., e.g.) and initials (J. R. R.) do not end one.
    /// </summary>
    public static class SentenceSplitter
    {
        private static readonly HashSet<string> Abbreviations = new (StringComparer.OrdinalIgnoreCase) {
            "mr", "mrs", "ms", "dr", "st", "jr", "sr", "prof", "rev", "gen", "col", "capt", "lt", "sgt", "mt", "ft",
            "vs", "etc", "e.g", "i.e", "cf", "viz", "no", "vol", "ch", "p", "pp", "fig", "esq", "messrs", "mme", "mlle",
        };

        public static IReadOnlyList<SpeechSentence> Split (string text)
        {
            var sentences = new List<SpeechSentence> ();
            var start = 0;
            for (var i = 0; i < text.Length; i++) {
                if (text[i] is not ('.' or '!' or '?' or '…'))
                    continue;
                var end = i + 1;
                while (end < text.Length && text[end] is '.' or '!' or '?' or '…')
                    end++;
                while (end < text.Length && text[end] is '"' or '”' or '’' or '\'' or ')' or ']' or '»')
                    end++;
                if (end < text.Length && !char.IsWhiteSpace (text[end]))
                    continue;
                var next = end;
                while (next < text.Length && char.IsWhiteSpace (text[next]))
                    next++;
                if (next < text.Length && !StartsSentence (text[next]))
                    continue;
                if (text[i] == '.' && IsAbbreviation (text, start, i))
                    continue;
                sentences.Add (new SpeechSentence (start, end));
                start = next;
                i = next - 1;
            }
            if (start < text.Length && text[start..].Trim ().Length > 0)
                sentences.Add (new SpeechSentence (start, text.Length));
            return sentences;
        }

        private static bool StartsSentence (char c)
            => char.IsUpper (c) || char.IsDigit (c) || c is '"' or '“' or '‘' or '\'' or '(' or '[' or '«' or '—' or '–';

        /// <summary>Whether the word ending at the period at <paramref name="dot"/> is an abbreviation or an initial.</summary>
        private static bool IsAbbreviation (string text, int sentenceStart, int dot)
        {
            var wordStart = dot;
            while (wordStart > sentenceStart && !char.IsWhiteSpace (text[wordStart - 1]) && text[wordStart - 1] is not ('(' or '"' or '“'))
                wordStart--;
            var word = text[wordStart..dot];
            if (word.Length == 1 && char.IsUpper (word[0]))
                return true; // An initial: "J. R. R. Tolkien".
            return Abbreviations.Contains (word);
        }
    }
}
