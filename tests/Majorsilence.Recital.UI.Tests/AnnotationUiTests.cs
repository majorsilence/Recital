using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Forms.Headless;
using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Layout;
using Xunit;

namespace Majorsilence.Recital.UI.Tests
{
    public class AnnotationUiTests
    {
        /// <summary>
        /// Opens Moby-Dick at chapter 1. The UiTest is made by the test itself: the culture it sets would not
        /// flow back out of an async helper, and CI's invariant culture formats "3%" as "3 %".
        /// </summary>
        private static MainForm OpenMobyDick (UiTest ui, (Storage.InMemoryStorageProvider Storage, Core.Library.DeviceIdentity Device) library, int width = 1000, int height = 700)
        {
            var form = ui.OpenMainForm (library.Storage, library.Device, width, height);
            form.Operations.Run (() => form.OpenBookAsync (form.Library!.Books[0]));
            UiTest.WaitIdle (form);
            var entry = ReadingSession.Flatten (form.Reader!.Session!.Book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.");
            form.Operations.Run (() => form.Reader.GoToAsync (entry));
            UiTest.WaitIdle (form);
            return form;
        }

        private static List<TextLine> TextLines (ReaderView reader)
            => reader.Session!.Page.Lines.Where (l => l.Text.Trim ().Length > 20).ToList ();

        /// <summary>A point on <paramref name="line"/>, <paramref name="fraction"/> of the way along, in the page view's coordinates.</summary>
        private static Point On (ReaderView reader, TextLine line, float fraction)
        {
            var settings = reader.Session!.Settings;
            return new Point ((int)(settings.MarginLeft + line.X + line.Width * fraction), (int)(settings.MarginTop + line.Y + line.Height / 2));
        }

        private static void Drag (MainForm form, Control view, Point from, Point to)
        {
            var a = UiTest.PointIn (form, view, from.X, from.Y);
            var b = UiTest.PointIn (form, view, to.X, to.Y);
            HeadlessRenderer.MouseMove (form, a.X, a.Y);
            HeadlessRenderer.MouseDown (form, a.X, a.Y);
            HeadlessRenderer.MouseMove (form, a.X + 8, a.Y, MouseButtons.Left);
            HeadlessRenderer.MouseMove (form, b.X, b.Y, MouseButtons.Left);
            HeadlessRenderer.MouseUp (form, b.X, b.Y);
            UiTest.WaitIdle (form);
        }

        private static void Choose (MainForm form, HighlightBar bar, string name)
        {
            Assert.True (bar.Visible, "The highlight bar shows.");
            UiTest.ClickItem (form, bar, bar.Items.Cast<ToolStripItem> ().First (i => i.Name == name || i.Text == name));
            UiTest.WaitIdle (form);
        }

        [Fact]
        public async Task HoldingThenDraggingHighlightsAPassageThatOutlivesAResize ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            using (var ui = new UiTest ()) {
                var form = OpenMobyDick (ui, library);
                var reader = form.Reader!;
                reader.PageView.SelectHoldTime = TimeSpan.Zero;
                var lines = TextLines (reader);

                Drag (form, reader.PageView, On (reader, lines[2], 0.25f), On (reader, lines[4], 0.6f));
                Assert.NotNull (reader.Selection);
                var text = reader.Selection.Text;
                Assert.Contains (lines[3].Text.Trim (), text, StringComparison.Ordinal);

                Choose (form, reader.HighlightBar, "color-green");
                var highlight = Assert.Single (reader.Annotations);
                Assert.Equal ("green", highlight.Color);
                Assert.Equal (text, highlight.Text);
                Assert.Null (reader.Selection);
                Assert.False (reader.HighlightBar.Visible);
                Assert.Equal (3, Assert.Single (reader.PageView.Highlights).Rects.Count);

                // Narrower: the same words, on different lines.
                UiTest.Resize (form, 420, 760);
                UiTest.WaitIdle (form);
                var (start, end) = reader.Session!.Locate (highlight)!.Value;
                Assert.Equal (text, reader.Session.Chapter.Document.GetText (start, end));
                Assert.NotEmpty (reader.PageView.Highlights);
            }
        }

        [Fact]
        public async Task ADragWithoutHoldingStillTurnsThePage ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            using (var ui = new UiTest ()) {
                var form = OpenMobyDick (ui, library);
                var reader = form.Reader!;
                reader.PageView.SelectHoldTime = TimeSpan.FromHours (1);
                var before = reader.Session!.Locator;
                var line = TextLines (reader)[3];
                Drag (form, reader.PageView, On (reader, line, 0.8f), On (reader, line, 0.2f));
                Assert.Null (reader.Selection);
                Assert.NotEqual (before, reader.Session.Locator);
            }
        }

        [Fact]
        public async Task TappingAHighlightLetsYouAddANoteAndDeleteIt ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            using (var ui = new UiTest ()) {
                var form = OpenMobyDick (ui, library);
                var reader = form.Reader!;
                var line = TextLines (reader)[1];
                var word = reader.Session!.Page.WordAt (line.X + line.Width / 2, line.Y + 1)!.Value;
                reader.PageView.Select (word.Start, word.End);
                form.Operations.Run (async () => await reader.HighlightSelectionAsync ("blue"));
                UiTest.WaitIdle (form);
                var highlight = Assert.Single (reader.Annotations);

                // Tap it: the bar offers Delete; Note… opens the editor.
                var rect = reader.PageView.RectsOf (word.Start, word.End)[0];
                UiTest.Click (form, reader.PageView, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                UiTest.WaitIdle (form);
                Assert.Equal (highlight.Id, reader.HighlightBar.Target?.Id);
                Choose (form, reader.HighlightBar, "Note…");
                Assert.True (reader.NoteEditor.Visible);
                reader.NoteEditor.NoteText = "The whale's first mention?";
                var save = reader.NoteEditor.Controls.OfType<FlowLayoutPanel> ().Single ().Controls.OfType<Button> ().First (b => b.Text == "Save");
                UiTest.Click (form, save, 4, 4);
                UiTest.WaitIdle (form);
                Assert.False (reader.NoteEditor.Visible);
                Assert.Equal ("The whale's first mention?", Assert.Single (reader.Annotations).Note);
                Assert.True (Assert.Single (reader.PageView.Highlights).HasNote);

                UiTest.Click (form, reader.PageView, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                UiTest.WaitIdle (form);
                Choose (form, reader.HighlightBar, "Delete");
                Assert.Empty (reader.Annotations);
                Assert.Empty (reader.PageView.Highlights);
            }
        }

        [Fact]
        public async Task ALongPressSelectsTheWord ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            using (var ui = new UiTest ()) {
                var form = OpenMobyDick (ui, library, 400, 760);
                var reader = form.Reader!;
                var line = TextLines (reader)[2];
                var p = UiTest.PointIn (form, reader.PageView, On (reader, line, 0.5f).X, On (reader, line, 0.5f).Y);
                HeadlessRenderer.LongPress (form, p.X, p.Y);
                UiTest.WaitIdle (form);
                Assert.NotNull (reader.Selection);
                Assert.Matches (@"^[\p{L}'’-]+$", reader.Selection.Text);
                Assert.True (reader.HighlightBar.Visible);
                Choose (form, reader.HighlightBar, "✕");
                Assert.Null (reader.Selection);
                Assert.Empty (reader.Annotations);
            }
        }

        [Fact]
        public async Task BookmarksAndHighlightsAreListedAndExported ()
        {
            var library = await UiTest.LibraryWithAsync (UiTest.MobyDick);
            var path = Path.Combine (Path.GetTempPath (), "recital-export-" + Guid.NewGuid ().ToString ("N") + ".md");
            using (var ui = new UiTest ()) {
                var form = OpenMobyDick (ui, library);
                var reader = form.Reader!;
                var toolbar = reader.Controls.OfType<ToolStrip> ().First (s => s is not StatusStrip and not HighlightBar);
                UiTest.ClickItem (form, toolbar, toolbar.Items.Cast<ToolStripItem> ().First (i => i.Text == "Bookmark"));
                UiTest.WaitIdle (form);
                Assert.True (reader.PageView.Bookmarked);

                var lines = TextLines (reader);
                var start = reader.Session!.Page.WordAt (lines[0].X + 1, lines[0].Y + 1)!.Value.Start;
                var end = reader.Session.Page.WordAt (lines[1].X + lines[1].Width - 2, lines[1].Y + 1)!.Value.End;
                reader.PageView.Select (start, end);
                form.Operations.Run (async () => await reader.HighlightSelectionAsync ("yellow", "Ishmael's mood"));
                UiTest.WaitIdle (form);

                UiTest.ClickItem (form, toolbar, toolbar.Items.Cast<ToolStripItem> ().First (i => i.Text == "Notes"));
                UiTest.WaitIdle (form);
                Assert.Same (reader.Notes, reader.SideContent);
                var chapter = Assert.Single (reader.Notes.Tree.Nodes.Cast<TreeNode> ());
                Assert.Equal ("CHAPTER 1. Loomings.", chapter.Text);
                Assert.Equal (2, chapter.Nodes.Count);

                PixelBaseline.AssertMatches ("Reader.Annotations", UiTest.Capture (form));

                HeadlessRenderer.SaveFileResponse = _ => path;
                try {
                    var notesBar = reader.Notes.Controls.OfType<ToolStrip> ().Single ();
                    UiTest.ClickItem (form, notesBar, notesBar.Items.Cast<ToolStripItem> ().First (i => i.Text == "Export…"));
                    UiTest.WaitIdle (form);
                } finally {
                    HeadlessRenderer.SaveFileResponse = null;
                }

                // Removing the bookmark from the toolbar.
                UiTest.ClickItem (form, toolbar, toolbar.Items.Cast<ToolStripItem> ().First (i => i.Text == "Bookmark"));
                UiTest.WaitIdle (form);
                Assert.False (reader.PageView.Bookmarked);
                Assert.Single (reader.Annotations);
            }

            try {
                var markdown = File.ReadAllText (path);
                Assert.StartsWith ("# Moby Dick; Or, The Whale", markdown, StringComparison.Ordinal);
                Assert.Contains ("## CHAPTER 1. Loomings.", markdown, StringComparison.Ordinal);
                Assert.Contains ("- Bookmark (", markdown, StringComparison.Ordinal);
                Assert.Contains ("Ishmael's mood", markdown, StringComparison.Ordinal);
            } finally {
                File.Delete (path);
            }
        }
    }
}
