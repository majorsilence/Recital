using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Epub;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class ReaderTests
    {
        private static MainForm OpenBook (UiTest ui, (Storage.InMemoryStorageProvider, DeviceIdentity) library, string title, int width = 1000, int height = 700)
        {
            var form = ui.OpenMainForm (library.Item1, library.Item2, width, height);
            var book = form.Library!.Books.Single (b => b.Title.Contains (title, StringComparison.Ordinal));
            form.Operations.Run (() => form.OpenBookAsync (book));
            UiTest.WaitIdle (form);
            Assert.NotNull (form.Reader?.Session);
            return form;
        }

        private static ReadingSession Session (MainForm form) => form.Reader!.Session!;

        /// <summary>The position being read is on the page showing (the plan's "position preserved by CFI").</summary>
        private static void AssertShowing (MainForm form, ReadingLocator locator)
        {
            var session = Session (form);
            Assert.Equal (locator, session.Locator);
            var position = session.Chapter.Document.Resolve (EpubCfi.Parse (locator.Cfi!));
            Assert.NotNull (position);
            Assert.Same (session.Page, session.Chapter.PageOf (position.Value));
            Assert.Same (session.Page, form.Reader!.PageView.Page);
            Assert.Equal (session.Settings.PageWidth, form.Reader.PageView.ClientSize.Width);
        }

        [Fact]
        public async Task OpenPageForwardTenTimesSwitchThemeAndResizeKeepsThePosition ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.SePride);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Pride");

            var seen = new HashSet<ReadingLocator> { Session (form).Locator };
            for (var i = 0; i < 10; i++) {
                UiTest.Key (form, Keys.Right);
                UiTest.WaitIdle (form);
                Assert.True (seen.Add (Session (form).Locator), "Every key press shows a new page.");
            }
            var locator = Session (form).Locator;
            Assert.True (Session (form).SpineIndex > 1, "Ten pages go past the title page and imprint.");

            // Theme: through the typography panel's theme list, as a reader would.
            UiTest.ClickItem (form, Toolbar (form), ToolbarItem (form, "Aa"));
            UiTest.WaitIdle (form);
            Assert.Same (form.Reader!.Typography, form.Reader.SideContent);
            var themeCombo = form.Reader.Typography.Controls[0].Controls.OfType<ComboBox> ().First ();
            themeCombo.SelectedIndex = ReaderTheme.All.ToList ().IndexOf (ReaderTheme.Dark);
            UiTest.WaitIdle (form);
            Assert.Equal (ThemeKind.Dark, form.Preferences.Theme);
            Assert.Equal (unchecked ((int)ReaderTheme.Dark.PageBackground), form.Reader.PageView.BackColor.ToArgb ());
            AssertShowing (form, locator);

            // Resize: wider, then a phone.
            UiTest.Resize (form, 1280, 800);
            UiTest.WaitIdle (form);
            AssertShowing (form, locator);

            UiTest.Resize (form, 400, 760);
            UiTest.WaitIdle (form);
            Assert.True (form.Reader.IsNarrow);
            AssertShowing (form, locator);

            // And the position is what gets saved.
            form.Operations.Run (form.CloseBookAsync);
            UiTest.WaitIdle (form);
            var saved = await form.Services!.Library.GetProgressAsync (form.Library!.Books[0].Id, TestContext.Current.CancellationToken);
            Assert.Equal (locator, saved?.Locator);
        }

        private static ToolStrip Toolbar (MainForm form) => form.Reader!.Controls.OfType<ToolStrip> ().First (s => s is not StatusStrip);

        private static ToolStripItem ToolbarItem (MainForm form, string text)
            => Toolbar (form).Items.Cast<ToolStripItem> ().First (i => i.Text?.Trim () == text);

        [Fact]
        public async Task TapZonesTurnPagesAndTheMiddleTogglesTheBarsOnAPhone ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Alice", 400, 760);
            var view = form.Reader!.PageView;
            var start = Session (form).Locator;

            Assert.False (form.Reader.ChromeVisible);
            UiTest.Click (form, view, view.Width - 20, view.Height / 2);
            UiTest.WaitIdle (form);
            Assert.NotEqual (start, Session (form).Locator);

            UiTest.Click (form, view, 20, view.Height / 2);
            UiTest.WaitIdle (form);
            Assert.Equal (start, Session (form).Locator);

            UiTest.Click (form, view, view.Width / 2, view.Height / 2);
            UiTest.WaitIdle (form);
            Assert.True (form.Reader.ChromeVisible);
            Assert.Equal (start, Session (form).Locator);
        }

        [Fact]
        public async Task DraggingAndTheWheelTurnPages ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Alice");
            var view = form.Reader!.PageView;
            var start = Session (form).Locator;

            // Drag right to left, starting in the "back" zone: the drag wins over the tap zone.
            var from = UiTest.PointIn (form, view, 100, 200);
            var to = UiTest.PointIn (form, view, 20, 210);
            HeadlessRenderer.MouseMove (form, from.X, from.Y);
            HeadlessRenderer.MouseDown (form, from.X, from.Y);
            HeadlessRenderer.MouseMove (form, to.X, to.Y, MouseButtons.Left);
            HeadlessRenderer.MouseUp (form, to.X, to.Y);
            UiTest.WaitIdle (form);
            var afterDrag = Session (form).Locator;
            Assert.NotEqual (start, afterDrag);

            var middle = UiTest.PointIn (form, view, view.Width / 2, view.Height / 2);
            HeadlessRenderer.MouseWheel (form, middle.X, middle.Y, -120);
            UiTest.WaitIdle (form);
            Assert.NotEqual (afterDrag, Session (form).Locator);
            HeadlessRenderer.MouseWheel (form, middle.X, middle.Y, 120);
            UiTest.WaitIdle (form);
            Assert.Equal (afterDrag, Session (form).Locator);
        }

        [Fact]
        public async Task SwipingTurnsPagesOnAPhone ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Sherlock);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Sherlock", 400, 760);
            var view = form.Reader!.PageView;
            var start = Session (form).Locator;
            var middle = UiTest.PointIn (form, view, view.Width / 2, view.Height / 2);

            HeadlessRenderer.Swipe (form, middle.X, middle.Y, SwipeDirection.Left, velocityX: -1200);
            UiTest.WaitIdle (form);
            Assert.NotEqual (start, Session (form).Locator);

            HeadlessRenderer.Swipe (form, middle.X, middle.Y, SwipeDirection.Right, velocityX: 1200);
            UiTest.WaitIdle (form);
            Assert.Equal (start, Session (form).Locator);
            Assert.False (form.Reader.ChromeVisible, "A swipe turns the page; it is not a tap on the middle.");
        }

        [Fact]
        public async Task AContentsEntryGoesToItsChapter ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.SePride);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Pride");

            UiTest.ClickItem (form, Toolbar (form), ToolbarItem (form, "Contents"));
            UiTest.WaitIdle (form);
            var tree = form.Reader!.Contents;
            Assert.Same (tree, form.Reader.SideContent);

            var node = tree.Nodes.Cast<TreeNode> ().First (n => n.Text == "V");
            UiTest.Capture (form);
            // On the label: the leading indent is the expand glyph's hit area.
            UiTest.Click (form, tree, node.Bounds.X + node.Bounds.Width / 2, node.Bounds.Y + node.Bounds.Height / 2);
            UiTest.WaitIdle (form);

            var entry = (TocEntry)node.Tag!;
            Assert.Equal (entry.SpineIndex, Session (form).SpineIndex);
            Assert.Equal (0, Session (form).PageIndex);
            Assert.StartsWith ("V |", form.Reader.StatusText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ATypographyChangeRepaginatesAndKeepsThePosition ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Frankenstein);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Frankenstein");
            for (var i = 0; i < 6; i++) {
                UiTest.Key (form, Keys.PageDown);
                UiTest.WaitIdle (form);
            }
            var locator = Session (form).Locator;
            var pages = Session (form).Chapter.Pages.Count;

            form.Reader!.ChangePreferences (form.Preferences with { FontSize = 30 });
            UiTest.WaitIdle (form);

            Assert.Equal (30, Session (form).Settings.FontSize);
            Assert.True (Session (form).Chapter.Pages.Count > pages);
            AssertShowing (form, locator);
        }

        [Fact]
        public async Task ReopeningABookReturnsToWhereYouLeftOff ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.Alice);
            using var ui = new UiTest ();
            var form = OpenBook (ui, library, "Alice");
            for (var i = 0; i < 4; i++) {
                UiTest.Key (form, Keys.Space);
                UiTest.WaitIdle (form);
            }
            var locator = Session (form).Locator;

            UiTest.ClickItem (form, Toolbar (form), ToolbarItem (form, "‹ Library"));
            UiTest.WaitIdle (form);
            Assert.Null (form.Reader);
            Assert.True (form.Library!.Books[0].Percent > 0);

            form.Operations.Run (() => form.OpenBookAsync (form.Library.Books[0]));
            UiTest.WaitIdle (form);
            AssertShowing (form, locator);
        }
    }
}
