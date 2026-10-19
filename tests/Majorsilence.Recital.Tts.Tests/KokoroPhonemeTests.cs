using System.IO.Compression;
using System.Text.Json;
using Majorsilence.Recital.Tts.Kokoro.Phonemes;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>The C# port of misaki's English phonemizer and its letters-to-phonemes network.</summary>
    public class KokoroPhonemeTests
    {
        private static readonly Lazy<EnglishPhonemizer> Phonemizer = new (EnglishPhonemizer.LoadAmerican);

        private static readonly Lazy<EnglishPhonemizer> British = new (EnglishPhonemizer.LoadBritish);

        private static string Say (string text) => Phonemizer.Value.Phonemize (text).Phonemes;

        private static string SayBritish (string text) => British.Value.Phonemize (text).Phonemes;

        // The British voices read misaki's gb dictionaries: no flapped t, no r after a vowel, əʊ (Q).
        [Theory]
        [InlineData ("tomato", "təmˈɑːtQ")]
        [InlineData ("water", "wˈɔːtə")]
        public void BritishVoicesReadBritishPronunciations (string word, string expected)
        {
            Assert.Equal (expected, SayBritish (word));
            Assert.NotEqual (expected, Say (word));
        }

        // An ending made by rule, not found in the dictionary: "affret" + ed. American flaps the t (written T, as
        // misaki writes the flap for Kokoro); British does not.
        [Fact]
        public void BritishEndingsDoNotFlap ()
        {
            Assert.EndsWith ("tɪd", SayBritish ("affreted"));
            Assert.EndsWith ("Tᵻd", Say ("affreted"));
        }

        // A name in neither dictionary goes to the British network, which knows no American-only sounds.
        [Fact]
        public void BritishGuessesUseOnlyBritishSounds ()
        {
            var said = SayBritish ("Netherfield Bingley Wickham");
            Assert.DoesNotContain ("ɾ", said);
            Assert.DoesNotContain ("ᵻ", said);
            Assert.DoesNotContain ("ʔ", said);
            Assert.NotEmpty (said.Trim ());
        }

        [Fact]
        public void ReadsDictionaryWordsWithStressAndPunctuation ()
            => Assert.Equal ("ˌɪt ɪz ɐ tɹˈuθ jˌunəvˈɜɹsəli əknˈɑləʤd, ðæt ɐ sˈɪŋɡᵊl mˈæn ɪn pəzˈɛʃən ʌv ɐ ɡˈʊd fˈɔɹʧən, mˈʌst bi ɪn wˈɑnt ʌv ɐ wˈIf.",
                Say ("It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife."));

        // "the" and "to" change before a vowel; the dot after "Mr" is not a full stop.
        [Theory]
        [InlineData ("the apple", "ði ˈæpᵊl")]
        [InlineData ("the pear", "ðə pˈɛɹ")]
        [InlineData ("Mr. Bennet said", "mˈɪstəɹ bˈɛnət sˈɛd")]
        [InlineData ("well-known", "wˈɛl nˈOn")]
        [InlineData ("the U.S. and the CIA", "ðə jˌuˈɛs ænd ðə sˌiˌIˈA")]
        public void HandlesContextAbbreviationsAndInitialisms (string text, string expected) => Assert.Equal (expected, Say (text));

        [Theory]
        [InlineData ("1999", "nˌIntˈin nˈIndi nˈIn")]
        [InlineData ("the 21st", "ðə twˈɛnti fˈɜɹst")]
        [InlineData ("$3.50", "θɹˈi dˈɑləɹz ænd fˈɪfti sˈɛnts")]
        [InlineData ("1,250", "wˈʌn θˈWzᵊnd tˈu hˈʌndɹəd fˈɪfti")]
        [InlineData ("3.14", "θɹˈi pYnt wˈʌn fˈɔɹ")]
        public void SaysNumbers (string text, string expected) => Assert.Equal (expected, Say (text));

        // Names the dictionaries lack come from the network.
        [Theory]
        [InlineData ("Bennet", "bˈɛnət")]
        [InlineData ("Darcy", "dˈɑɹsi")]
        [InlineData ("Bingley", "bˈɪŋli")]
        [InlineData ("Netherfield", "nˈɛðəɹfˌild")]
        [InlineData ("Hertfordshire", "hˈɜɹtfəɹdʃˌɪɹ")]
        public void GuessesNamesTheDictionariesLack (string name, string expected)
        {
            Assert.False (Phonemizer.Value.InDictionary (name));
            Assert.Equal (expected, Say (name));
        }

        [Fact]
        public void MapsEachWordToWhereItsPhonemesStart ()
        {
            const string text = "“My dear Mr. Bennet,” said his lady.";
            var result = Phonemizer.Value.Phonemize (text);

            Assert.Equal (["My", "dear", "Mr", "Bennet", "said", "his", "lady"], result.Words.Select (w => text[w.CharIndex..].Split (' ', '.', ',', '”')[0]));
            Assert.All (result.Words, w => Assert.NotEqual (' ', result.Phonemes[w.PhonemeIndex]));
        }

        // The network reproduces most dictionary words it was trained on: a check that the port runs it right.
        [Fact]
        public void TheNetworkAgreesWithTheDictionaryOnMostWords ()
        {
            using var file = typeof (EnglishPhonemizer).Assembly.GetManifestResourceStream ("Majorsilence.Recital.Tts.Kokoro.Data.us_gold.json.gz")!;
            using var gz = new GZipStream (file, CompressionMode.Decompress);
            using var doc = JsonDocument.Parse (gz);
            var words = doc.RootElement.EnumerateObject ()
                .Where (p => p.Value.ValueKind == JsonValueKind.String && p.Name.All (char.IsAsciiLetterLower))
                .Where ((_, i) => i % 300 == 0).ToList ();
            var exact = words.Count (p => Phonemizer.Value.Guess (p.Name) == p.Value.GetString ());

            Assert.True (exact >= words.Count * 0.6, $"{exact} of {words.Count}");
        }
    }
}
