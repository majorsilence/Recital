namespace Majorsilence.Recital.UI.Localization
{
    /// <summary>One language the UI can show, and the resource file (<c>Resources/strings.&lt;Code&gt;.json</c>)
    /// it draws from.</summary>
    /// <param name="Code">The <see cref="ReaderPreferences.Language"/> value this language is stored as.</param>
    /// <param name="NativeName">Shown in the Language menu, in the language's own script, so it reads
    /// whatever language the menu currently shows — the usual convention (a French speaker finds
    /// "Français" without first having to read English).</param>
    internal sealed record SupportedLanguage (string Code, string NativeName);

    /// <summary>Every language Recital's UI has translations for. Machine-translated (2026-11-07); each
    /// wants a native-speaker pass before anyone relies on it for a real release.</summary>
    internal static class SupportedLanguages
    {
        public static readonly IReadOnlyList<SupportedLanguage> All = [
            new ("en", "English"),
            new ("es", "Español"),
            new ("fr", "Français"),
            new ("de", "Deutsch"),
            new ("zh-Hans", "简体中文"),
            new ("sw", "Kiswahili"),
            new ("pt", "Português"),
            new ("ja", "日本語"),
            new ("ru", "Русский"),
            new ("hi", "हिन्दी"),
            new ("ar", "العربية"),
        ];
    }
}
