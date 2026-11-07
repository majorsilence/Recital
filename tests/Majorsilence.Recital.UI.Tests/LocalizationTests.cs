using System.Reflection;
using System.Text.Json;
using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// The UI's own text (<c>Localization/Strings.cs</c>): every language's resource has the same keys as
    /// English, and switching <see cref="ReaderPreferences.Language"/> actually changes what is on screen,
    /// live, in the reader and after returning to the library.
    /// </summary>
    public class LocalizationTests
    {
        private const string Prefix = "Majorsilence.Recital.UI.Resources.strings.";

        [Fact]
        public void EveryLanguageHasTheSameKeysAsEnglish ()
        {
            var assembly = typeof (MainForm).Assembly;
            var names = assembly.GetManifestResourceNames ()
                .Where (n => n.StartsWith (Prefix, StringComparison.Ordinal) && n.EndsWith (".json", StringComparison.Ordinal))
                .ToList ();

            // Covers GAP-like regressions in the csproj glob itself: each language is its own top-level
            // resource (WithCulture=false), not folded into a satellite assembly under one shared name.
            Assert.True (names.Count >= 11, $"Expected at least 11 language resources, found {names.Count}: {string.Join (", ", names)}");

            var english = Load (assembly, "en");
            foreach (var name in names) {
                var code = name[Prefix.Length..^".json".Length];
                var keys = Load (assembly, code);
                Assert.Empty (english.Keys.Except (keys.Keys));
                Assert.Empty (keys.Keys.Except (english.Keys));
            }
        }

        private static Dictionary<string, string> Load (Assembly assembly, string code)
        {
            using var stream = assembly.GetManifestResourceStream ($"{Prefix}{code}.json")
                ?? throw new InvalidOperationException ($"No resource for '{code}'.");
            return JsonSerializer.Deserialize<Dictionary<string, string>> (stream) ?? [];
        }

        [Fact]
        public async Task SwitchingLanguageRelocalizesTheReaderLiveAndTheLibraryOnReturn ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (library.Storage, library.Device);
            var book = form.Library!.Books[0];
            form.Operations.Run (() => form.OpenBookAsync (book));
            UiTest.WaitIdle (form);
            var reader = form.Reader!;

            Assert.Contains (ToolbarItems (reader), i => i.Text?.Trim () == "Library");
            Assert.DoesNotContain (ToolbarItems (reader), i => i.Text?.Trim () == "Biblioteca");

            reader.ChangePreferences (reader.Preferences with { Language = "es" });
            UiTest.WaitIdle (form);

            // The reader's own chrome, relocalized in place, no rebuild.
            Assert.Contains (ToolbarItems (reader), i => i.Text?.Trim () == "Biblioteca");
            Assert.DoesNotContain (ToolbarItems (reader), i => i.Text?.Trim () == "Library");

            // The language choice is a preference like Theme: it reaches the library too (MainForm.ApplyTheme),
            // without the library having been told about it directly.
            form.Operations.Run (form.CloseBookAsync);
            UiTest.WaitIdle (form);
            var libraryToolbar = form.Library!.Controls.OfType<ToolStrip> ().First (s => s is not StatusStrip);
            Assert.Contains (libraryToolbar.Items.Cast<ToolStripItem> (), i => i.Text?.Trim () == "Importar…");
        }

        private static IEnumerable<ToolStripItem> ToolbarItems (ReaderView reader)
            => reader.Controls.OfType<ToolStrip> ().Where (s => s is not StatusStrip and not HighlightBar and not TtsBar)
                .SelectMany (s => s.Items.Cast<ToolStripItem> ());
    }
}
