using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    /// <summary>Pixel baselines of each screen (D-006).</summary>
    public class MainFormTests
    {
        [Fact]
        public async Task LibraryMatchesBaseline ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync (UiTest.Alice, UiTest.SePride, UiTest.Frankenstein);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (storage, device);

            PixelBaseline.AssertMatches ("Library", UiTest.Capture (form));
        }

        [Fact]
        public async Task ReaderWithContentsMatchesBaseline ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (storage, device);
            form.Operations.Run (() => form.OpenBookAsync (form.Library!.Books[0]));
            UiTest.WaitIdle (form);
            for (var i = 0; i < 3; i++) {
                UiTest.Key (form, Keys.Right);
                UiTest.WaitIdle (form);
            }
            form.Reader!.ToggleSide (form.Reader.Contents);

            PixelBaseline.AssertMatches ("Reader.Contents", UiTest.Capture (form));
        }

        [Fact]
        public async Task DarkPhoneReaderMatchesBaseline ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync (UiTest.SePride);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (storage, device, 400, 760);
            form.Operations.Run (() => form.OpenBookAsync (form.Library!.Books[0]));
            UiTest.WaitIdle (form);
            form.Reader!.ChangePreferences (form.Preferences with { Theme = ThemeKind.Dark });
            UiTest.WaitIdle (form);
            // The title page: Standard Ebooks inverts its black-on-transparent image in dark mode.
            PixelBaseline.AssertMatches ("Reader.DarkPhone.TitlePage", UiTest.Capture (form));

            UiTest.Key (form, Keys.Right);
            UiTest.Key (form, Keys.Right);
            UiTest.WaitIdle (form);
            form.Reader.ToggleChrome ();
            PixelBaseline.AssertMatches ("Reader.DarkPhone", UiTest.Capture (form));
        }
    }
}
