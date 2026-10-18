using System.Globalization;

namespace Majorsilence.Recital.Tts.Kokoro.Phonemes
{
    /// <summary>English number names: cardinals, ordinals and years ("nineteen ninety-nine"), without "and".</summary>
    internal static class NumberWords
    {
        private static readonly string[] Ones = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
            "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
        private static readonly (long Value, string Name)[] Scales = [(1_000_000_000_000_000_000, "quintillion"), (1_000_000_000_000_000, "quadrillion"),
            (1_000_000_000_000, "trillion"), (1_000_000_000, "billion"), (1_000_000, "million"), (1_000, "thousand")];

        public static string Cardinal (long n)
        {
            if (n < 0)
                return "minus " + Cardinal (-n);
            if (n < 20)
                return Ones[n];
            if (n < 100)
                return Tens[n / 10] + (n % 10 == 0 ? "" : "-" + Ones[n % 10]);
            if (n < 1000)
                return Ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + Cardinal (n % 100));
            foreach (var (value, name) in Scales)
                if (n >= value)
                    return Cardinal (n / value) + " " + name + (n % value == 0 ? "" : " " + Cardinal (n % value));
            throw new ArgumentOutOfRangeException (nameof (n));
        }

        public static string Ordinal (long n)
        {
            var words = Cardinal (n);
            var split = Math.Max (words.LastIndexOf (' '), words.LastIndexOf ('-')) + 1;
            var last = words[split..];
            var ordinal = last switch {
                "one" => "first", "two" => "second", "three" => "third", "five" => "fifth", "eight" => "eighth",
                "nine" => "ninth", "twelve" => "twelfth",
                _ when last.EndsWith ('y') => last[..^1] + "ieth",
                _ => last + "th",
            };
            return words[..split] + ordinal;
        }

        /// <summary>A four-digit year as said: 1999 "nineteen ninety-nine", 1905 "nineteen oh five", 2005 "two thousand five".</summary>
        public static string Year (int year)
        {
            var (high, low) = (year / 100, year % 100);
            if (high % 10 == 0 && low < 10)   // 2000, 2005, 1000
                return Cardinal (year);
            if (low == 0)
                return Cardinal (high) + " hundred";
            return Cardinal (high) + " " + (low < 10 ? "oh " + Ones[low] : Cardinal (low));
        }

        public static bool TryParse (string digits, out long value)
            => long.TryParse (digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
