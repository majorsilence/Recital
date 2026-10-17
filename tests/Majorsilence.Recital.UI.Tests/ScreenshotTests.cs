using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>
    /// Renders the README's screenshots into docs/screenshots, from Project Gutenberg books. Opt-in, since it
    /// writes to the source tree:
    /// <c>RECITAL_SCREENSHOTS=1 dotnet test tests/Majorsilence.Recital.UI.Tests --filter ScreenshotTests</c>.
    /// </summary>
    public class ScreenshotTests
    {
        [Fact (Timeout = 120_000)]
        public async Task Library ()
        {
            var (storage, device) = await LibraryWithAsync (UiTest.MobyDick, UiTest.Sherlock, UiTest.PgFrankenstein, UiTest.Alice);
            byte[] png;
            using (var ui = new UiTest ())
                png = UiTest.Capture (ui.OpenMainForm (storage, device));
            await SaveAsync ("library.png", png);
        }

        [Fact (Timeout = 120_000)]
        public async Task SepiaReaderWithContents ()
        {
            var (storage, device) = await LibraryWithAsync (UiTest.MobyDick);
            byte[] png;
            using (var ui = new UiTest ()) {
                var form = ui.OpenMainForm (storage, device, 1100, 760);
                OpenAtChapter (form, "CHAPTER 1. Loomings.");
                form.Reader!.ChangePreferences (form.Preferences with { Theme = ThemeKind.Sepia });
                UiTest.WaitIdle (form);
                form.Reader.ToggleSide (form.Reader.Contents);
                UiTest.WaitIdle (form);
                png = UiTest.Capture (form);
            }
            await SaveAsync ("reader-sepia.png", png);
        }

        [Fact (Timeout = 120_000)]
        public async Task DarkPhoneReader ()
        {
            var (storage, device) = await LibraryWithAsync (UiTest.Sherlock);
            byte[] png;
            using (var ui = new UiTest ()) {
                var form = ui.OpenMainForm (storage, device, 400, 760);
                OpenAtChapter (form, "I.");
                form.Reader!.ChangePreferences (form.Preferences with { Theme = ThemeKind.Dark });
                UiTest.WaitIdle (form);
                png = UiTest.Capture (form);
            }
            await SaveAsync ("reader-dark-phone.png", png);
        }

        private static async Task<(Storage.InMemoryStorageProvider, Core.Library.DeviceIdentity)> LibraryWithAsync (params string[] books)
        {
            Assert.SkipUnless (Environment.GetEnvironmentVariable ("RECITAL_SCREENSHOTS") == "1", "Set RECITAL_SCREENSHOTS=1 to render the README screenshots.");
            return await UiTest.LibraryWithAsync (books);
        }

        private static void OpenAtChapter (MainForm form, string title)
        {
            form.Operations.Run (() => form.OpenBookAsync (form.Library!.Books[0]));
            UiTest.WaitIdle (form);
            var entry = ReadingSession.Flatten (form.Reader!.Session!.Book.TableOfContents).First (e => e.Title == title);
            form.Operations.Run (() => form.Reader.GoToAsync (entry));
            UiTest.WaitIdle (form);
        }

        // Called after the UiTest is disposed: its synchronization context is only pumped by WaitIdle, so a
        // continuation posted to it from this await never ran and the test hung.
        private static Task SaveAsync (string name, byte[] png)
        {
            var directory = Path.GetFullPath (Path.Combine (PixelBaseline.SourceDirectory (), "..", "..", "docs", "screenshots"));
            Directory.CreateDirectory (directory);
            return File.WriteAllBytesAsync (Path.Combine (directory, name), png, TestContext.Current.CancellationToken);
        }
    }
}
