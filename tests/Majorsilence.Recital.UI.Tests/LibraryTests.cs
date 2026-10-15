using System.Text;
using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using Majorsilence.Recital.Core.Library;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class LibraryTests
    {
        [Fact]
        public async Task ShowsEveryBookAndSearchFilters ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync (UiTest.Alice, UiTest.SePride, UiTest.Frankenstein);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (storage, device);
            var library = form.Library!;

            Assert.Equal (3, library.List.Items.Count);

            library.SearchText = "shelley";
            UiTest.WaitIdle (form);
            Assert.Equal (["Frankenstein"], library.Shown.Select (b => b.Title));

            library.SearchText = "";
            library.Sort = LibrarySort.Title;
            Assert.Equal (["Alice's Adventures in Wonderland", "Frankenstein", "Pride and Prejudice"], library.Shown.Select (b => b.Title));
        }

        [Fact]
        public async Task ImportsFromTheFilePickerAndReportsBadFiles ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync ();
            var junk = Path.Combine (Path.GetTempPath (), $"recital-{Guid.NewGuid ():N}.epub");
            await File.WriteAllTextAsync (junk, "not an epub", Encoding.UTF8, TestContext.Current.CancellationToken);
            using var ui = new UiTest ();
            try {
                var form = ui.OpenMainForm (storage, device);
                Assert.Empty (form.Library!.Books);

                HeadlessRenderer.OpenFileResponse = request => {
                    Assert.True (request.AllowMultiple);
                    return [UiTest.Corpus (UiTest.Alice), junk];
                };
                var strip = form.Library.Controls.OfType<ToolStrip> ().First (s => s is not StatusStrip);
                UiTest.ClickItem (form, strip, strip.Items.Cast<ToolStripItem> ().First (i => i.Text == "Import…"));
                UiTest.WaitIdle (form);

                Assert.Equal ("Alice's Adventures in Wonderland", Assert.Single (form.Library.Books).Title);
                Assert.Contains ("Added 1 book.", form.Library.StatusText, StringComparison.Ordinal);
                Assert.Contains ("not a readable EPUB", form.Library.StatusText, StringComparison.Ordinal);
            } finally {
                HeadlessRenderer.OpenFileResponse = null;
                File.Delete (junk);
            }
        }

        [Fact]
        public async Task DoubleClickingACoverOpensTheBook ()
        {
            var (storage, device) = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = ui.OpenMainForm (storage, device);
            var list = form.Library!.List;
            UiTest.Capture (form);
            var tile = list.GetItemRect (0);
            Assert.False (tile.IsEmpty);

            var p = UiTest.PointIn (form, list, tile.X + tile.Width / 2, tile.Y + 40);
            HeadlessRenderer.Click (form, p.X, p.Y);
            HeadlessRenderer.Click (form, p.X, p.Y);
            UiTest.WaitIdle (form);

            Assert.NotNull (form.Reader?.Session);
        }
    }
}
