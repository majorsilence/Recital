using System.Text.Json;
using System.Text.Json.Serialization;
using Majorsilence.Recital.Core.Reading;

namespace Majorsilence.Recital.UI.Localization
{
    /// <summary>
    /// The UI's own text, in whichever language <see cref="ReaderPreferences.Language"/> names. Keys are
    /// plain strings (not an enum), looked up in a JSON resource embedded per language
    /// (<c>Resources/strings.&lt;code&gt;.json</c>); a view calls <see cref="Get"/> both when it builds its
    /// controls and again, for the same keys, from a public <c>ApplyLanguage</c> method, so a language change
    /// re-reads every string without rebuilding the control tree (the same shape <c>ThemePainter</c> uses for
    /// colours). A missing key or language falls back to English, then to the key itself, so a half-finished
    /// translation never shows blank text.
    /// </summary>
    internal static class Strings
    {
        private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> cache = new (StringComparer.Ordinal);
        private static string current = "en";

        /// <summary>The language <see cref="Get"/> reads from; set from <see cref="ReaderPreferences.Language"/>
        /// wherever the app applies preferences (the same points <c>ThemePainter.Apply</c> runs from).</summary>
        public static string CurrentLanguage {
            get => current;
            set => current = string.IsNullOrEmpty (value) ? "en" : value;
        }

        /// <summary>A <see cref="ReaderTheme"/>'s display name, translated (the name a <c>ReaderTheme</c> record
        /// itself carries, like its colours, is a fixed English identifier — see <see cref="ThemeKind"/>).</summary>
        public static string ThemeName (ThemeKind kind) => Get ("Theme" + kind);

        /// <summary>The current language's text for <paramref name="key"/>, or English's, or the key itself.</summary>
        public static string Get (string key)
        {
            if (Load (current).TryGetValue (key, out var value))
                return value;
            if (current != "en" && Load ("en").TryGetValue (key, out var fallback))
                return fallback;
            return key;
        }

        private static IReadOnlyDictionary<string, string> Load (string language)
        {
            if (cache.TryGetValue (language, out var loaded))
                return loaded;

            var assembly = typeof (Strings).Assembly;
            var name = $"Majorsilence.Recital.UI.Resources.strings.{language}.json";
            IReadOnlyDictionary<string, string> result;
            using (var stream = assembly.GetManifestResourceStream (name)) {
                result = stream is null
                    ? new Dictionary<string, string> (StringComparer.Ordinal)
                    : JsonSerializer.Deserialize (stream, StringsJsonContext.Default.DictionaryStringString)
                        ?? new Dictionary<string, string> (StringComparer.Ordinal);
            }
            cache[language] = result;
            return result;
        }
    }

    [JsonSerializable (typeof (Dictionary<string, string>))]
    internal sealed partial class StringsJsonContext : JsonSerializerContext
    {
    }
}
