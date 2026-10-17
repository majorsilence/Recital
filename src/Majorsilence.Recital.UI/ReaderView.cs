using System.Drawing;
using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Epub;
using SkiaSharp;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// The reading screen: a toolbar, the page, a status bar, and a side panel for the contents, the
    /// highlights and notes, or the typography settings. Selecting text shows a <see cref="HighlightBar"/>. Wide windows show the side panel in a <see cref="SplitContainer"/> beside the
    /// page; narrow ones (phones) overlay it, and hide the toolbar and status bar until the middle of the
    /// page is tapped. The bars then cover the page rather than shrink it, so showing them never
    /// re-paginates.
    /// </summary>
    public class ReaderView : UserControl
    {
        /// <summary>Below this width (logical px) the narrow, phone layout is used.</summary>
        public const int NarrowWidth = 640;

        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds (1.5);

        private readonly LibraryService library;
        private readonly LibraryBook book;
        private readonly UiOperations operations;
        private readonly ToolStrip tool_strip = new () { Dock = DockStyle.None };
        private readonly StatusStrip status_strip = new () { Dock = DockStyle.None, SizingGrip = false };
        private readonly ToolStripStatusLabel chapter_label = new () { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel page_label = new ();
        private readonly ToolStripStatusLabel percent_label = new ();
        private readonly ToolStripButton contents_button = new ("Contents") { CheckOnClick = false };
        private readonly ToolStripButton typography_button = new ("Aa");
        private readonly ToolStripButton notes_button = new ("Notes") { ToolTipText = "Highlights, notes and bookmarks" };
        private readonly ToolStripButton bookmark_button = new ("Bookmark") { ToolTipText = "Bookmark this page" };
        private readonly ToolStripLabel title_label = new ();
        private readonly Panel body = new () { Dock = DockStyle.None };
        private readonly SplitContainer split = new () {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            Panel1Collapsed = true,
        };
        private readonly Panel overlay = new () { Dock = DockStyle.None, Visible = false };
        private readonly TreeView contents = new () { Dock = DockStyle.Fill, ShowLines = false, FullRowSelect = true, HideSelection = false };
        private readonly TypographyPanel typography = new () { Dock = DockStyle.Fill };
        private readonly AnnotationsPanel notes = new () { Dock = DockStyle.Fill };
        private readonly HighlightBar highlight_bar = new ();
        private readonly NoteEditor note_editor = new ();
        private IReadOnlyList<Annotation> annotations = [];
        private SelectedPassage? selection;
        // The highlight the note editor is writing for: an existing one, or a selection not saved yet.
        private Annotation? note_target;
        private SelectedPassage? note_selection;
        private readonly Dictionary<string, SKImage?> images = new (StringComparer.Ordinal);
        private readonly Majorsilence.Forms.Timer save_timer = new () { Interval = (int)SaveDelay.TotalMilliseconds };
        private ReadingSession? session;
        private ReaderPreferences preferences;
        private Control? side_content;
        private bool chrome_visible;
        private bool relayout_pending;
        private bool relayout_running;
        private bool save_pending;
        private bool closed;

        public ReaderView (LibraryService library, LibraryBook book, ReaderPreferences preferences, UiOperations operations)
        {
            this.library = library;
            this.book = book;
            this.preferences = preferences;
            this.operations = operations;

            PageView = new BookPageView { Dock = DockStyle.Fill };

            var back = new ToolStripButton ("‹ Library");
            back.Click += (_, _) => CloseRequested?.Invoke (this, EventArgs.Empty);
            contents_button.Click += (_, _) => ToggleSide (contents);
            typography_button.Click += (_, _) => ToggleSide (typography);
            notes_button.Click += (_, _) => ToggleSide (notes);
            bookmark_button.Click += (_, _) => operations.Run (ToggleBookmarkAsync);
            title_label.Text = book.Title;

            var themes = new ToolStripDropDownButton ("Theme");
            foreach (var theme in ReaderTheme.All) {
                var item = new ToolStripMenuItem (theme.Name);
                item.Click += (_, _) => ChangePreferences (this.preferences with { Theme = theme.Kind });
                themes.DropDownItems.Add (item);
            }

            tool_strip.Items.AddRange ([back, new ToolStripSeparator (), contents_button, notes_button, typography_button, themes, bookmark_button, new ToolStripSeparator (), title_label]);
            status_strip.Items.AddRange ([chapter_label, page_label, percent_label]);

            split.Panel2.Controls.Add (PageView);
            body.Controls.Add (split);
            Controls.Add (body);
            Controls.Add (overlay);
            Controls.Add (highlight_bar);
            Controls.Add (note_editor);
            Controls.Add (tool_strip);
            Controls.Add (status_strip);

            PageView.TurnRequested += (_, e) => operations.Run (() => TurnAsync (e.Turn));
            PageView.CenterTapped += (_, _) => ToggleChrome ();
            PageView.SizeChanged += (_, _) => RequestRelayout ();
            PageView.ImageSource = Image;
            PageView.SelectionMade += (_, e) => OnSelectionMade (e);
            PageView.SelectionCleared += (_, _) => {
                selection = null;
                if (highlight_bar.Target is null)
                    highlight_bar.HideBar ();
            };
            PageView.HighlightTapped += (_, e) => {
                if (e.Highlight.Tag is Annotation annotation) {
                    selection = null;
                    highlight_bar.ShowFor (annotation);
                    PlaceBar (e.Bounds);
                }
            };
            highlight_bar.ActionChosen += (_, e) => operations.Run (() => OnHighlightActionAsync (e));
            note_editor.Saved += (_, text) => operations.Run (() => SaveNoteAsync (text));
            note_editor.Cancelled += (_, _) => CloseNoteEditor ();

            notes.OpenRequested += (_, a) => operations.Run (() => GoToAsync (a));
            notes.EditNoteRequested += (_, a) => EditNote (a, null);
            notes.DeleteRequested += (_, a) => operations.Run (() => DeleteAnnotationAsync (a));
            notes.ExportRequested += (_, _) => operations.Run (ExportToFileAsync);
            notes.CopyRequested += (_, _) => operations.Run (() => Clipboard.SetTextAsync (ExportMarkdown ()));
            // FRAMEWORK-GAP: GAP-012: SaveFileDialog gives a path only, which phones and the browser cannot write to.
            notes.CanExportToFile = OperatingSystem.IsWindows () || OperatingSystem.IsMacOS () || (OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid ());

            contents.NodeMouseClick += (_, e) => {
                if (e.Node?.Tag is TocEntry entry)
                    operations.Run (() => GoToAsync (entry));
            };
            contents.KeyDown += (_, e) => {
                if (e.KeyCode == Keys.Enter && contents.SelectedNode?.Tag is TocEntry entry) {
                    e.Handled = true;
                    operations.Run (() => GoToAsync (entry));
                }
            };

            typography.Preferences = preferences;
            typography.PreferencesChanged += (_, p) => ChangePreferences (p);

            save_timer.Tick += (_, _) => {
                save_timer.Stop ();
                operations.Run (() => SaveProgressAsync (CancellationToken.None));
            };

            ApplyTheme ();
        }

        public LibraryBook Book => book;
        public BookPageView PageView { get; }
        public TreeView Contents => contents;
        public TypographyPanel Typography => typography;
        public AnnotationsPanel Notes => notes;
        public HighlightBar HighlightBar => highlight_bar;
        public NoteEditor NoteEditor => note_editor;

        /// <summary>The book's live highlights, notes and bookmarks, in reading order.</summary>
        public IReadOnlyList<Annotation> Annotations => annotations;

        /// <summary>The passage selected on the page, waiting for a colour or a note.</summary>
        public SelectedPassage? Selection => selection;
        public ReadingSession? Session => session;
        public ReaderPreferences Preferences => preferences;
        public bool IsNarrow => Width < NarrowWidth;

        /// <summary>Whether the toolbar and status bar show: always in the wide layout, and in the narrow one
        /// when toggled on or while the side panel is open (its buttons close it).</summary>
        public bool ChromeVisible => !IsNarrow || chrome_visible || side_content is not null;

        /// <summary>The panel beside (or over) the page: the contents, the typography panel, or null.</summary>
        public Control? SideContent => side_content;

        public string StatusText => $"{chapter_label.Text} | {page_label.Text} | {percent_label.Text}";

        public event EventHandler? CloseRequested;
        public event EventHandler<ReaderPreferences>? PreferencesChanged;

        /// <summary>Opens the book at the newest saved position from any device.</summary>
        public async Task OpenAsync (CancellationToken ct = default)
        {
            var epub = await library.OpenBookAsync (book.Id, ct);
            try {
                var progress = await library.GetProgressAsync (book.Id, ct);
                session = await ReadingSession.OpenAsync (epub, CurrentLayoutSettings () ?? preferences.ToLayoutSettings (600, 800), progress?.Locator, ct);
            } catch {
                await epub.DisposeAsync ();
                throw;
            }

            if (closed) {
                await session.DisposeAsync ();
                session = null;
                return;
            }

            FillContents ();
            annotations = await library.GetAnnotationsAsync (book.Id, ct);
            await FillNotesAsync ();
            ShowCurrent ();
            // The view may have been resized while the book opened.
            RequestRelayout ();
            PageView.Focus ();
        }

        /// <summary>Saves the position and releases the book.</summary>
        public async Task CloseAsync ()
        {
            if (closed)
                return;
            closed = true;
            save_timer.Stop ();
            if (session is null)
                return;
            await SaveProgressAsync (CancellationToken.None);
            await session.DisposeAsync ();
            session = null;
            foreach (var image in images.Values)
                image?.Dispose ();
            images.Clear ();
        }

        public Task TurnAsync (PageTurn turn) => session is null || closed ? Task.CompletedTask : TurnCoreAsync (turn);

        private async Task TurnCoreAsync (PageTurn turn)
        {
            var moved = turn switch {
                PageTurn.Forward => await session!.NextPageAsync (),
                PageTurn.Back => await session!.PreviousPageAsync (),
                _ => await EdgeAsync (turn == PageTurn.ChapterEnd),
            };
            if (moved)
                PositionChanged ();
        }

        private async Task<bool> EdgeAsync (bool end)
        {
            await session!.ChapterEdgeAsync (end);
            return true;
        }

        public async Task GoToAsync (TocEntry entry)
        {
            if (session is null || closed || !await session.GoToAsync (entry))
                return;
            PositionChanged ();
            if (IsNarrow)
                ToggleSide (null);
            PageView.Focus ();
        }

        /// <summary>Applies new preferences: a theme change repaints, a typography change re-paginates.</summary>
        public void ChangePreferences (ReaderPreferences next)
        {
            if (next == preferences)
                return;
            var relayout = next.AffectsLayout (preferences);
            preferences = next;
            if (typography.Preferences != next)
                typography.Preferences = next;
            ApplyTheme ();
            if (relayout)
                RequestRelayout ();
            PreferencesChanged?.Invoke (this, next);
        }

        public void ToggleChrome ()
        {
            chrome_visible = !chrome_visible;
            ArrangeChildren ();
        }

        /// <summary>Shows <paramref name="content"/> in the side panel, or hides the panel when it is already showing it (or null).</summary>
        public void ToggleSide (Control? content)
        {
            if (content is null || side_content == content) {
                side_content?.Parent?.Controls.Remove (side_content);
                side_content = null;
            } else {
                side_content?.Parent?.Controls.Remove (side_content);
                side_content = content;
            }
            contents_button.Checked = side_content == contents;
            typography_button.Checked = side_content == typography;
            notes_button.Checked = side_content == notes;
            ArrangeChildren ();
            if (side_content == contents)
                SelectCurrentContentsEntry ();
        }

        protected override void OnSizeChanged (EventArgs e)
        {
            base.OnSizeChanged (e);
            ArrangeChildren ();
        }

        /// <summary>Lays out the bars, the page and the side panel for the current width (see the class remarks).</summary>
        private void ArrangeChildren ()
        {
            var width = ClientSize.Width;
            var height = ClientSize.Height;
            var narrow = IsNarrow;
            var bars = ChromeVisible;
            var top = tool_strip.PreferredSize.Height;
            var bottom = status_strip.PreferredSize.Height;

            tool_strip.Visible = bars;
            status_strip.Visible = bars;
            tool_strip.SetBounds (0, 0, width, top);
            status_strip.SetBounds (0, height - bottom, width, bottom);

            var bodyBounds = narrow ? new Rectangle (0, 0, width, height) : new Rectangle (0, top, width, Math.Max (0, height - top - bottom));
            body.Bounds = bodyBounds;

            if (narrow) {
                split.Panel1Collapsed = true;
                if (side_content is not null) {
                    if (side_content.Parent != overlay)
                        overlay.Controls.Add (side_content);
                    // Under the toolbar, so the toolbar's buttons still close it.
                    var overlayTop = bars ? top : 0;
                    overlay.SetBounds (0, overlayTop, width, Math.Max (0, height - overlayTop - (bars ? bottom : 0)));
                    overlay.Visible = true;
                    overlay.BringToFront ();
                } else {
                    overlay.Visible = false;
                }
            } else {
                overlay.Visible = false;
                if (side_content is not null && side_content.Parent != split.Panel1)
                    split.Panel1.Controls.Add (side_content);
                split.Panel1Collapsed = side_content is null;
                if (side_content is not null)
                    split.SplitterDistance = Math.Min (300, Math.Max (180, width / 4));
            }

            tool_strip.BringToFront ();
            status_strip.BringToFront ();
            if (note_editor.Visible) {
                note_editor.PlaceIn (body.Bounds);
                note_editor.BringToFront ();
            }
            if (highlight_bar.Visible)
                highlight_bar.BringToFront ();
        }

        private void ApplyTheme ()
        {
            var theme = ReaderTheme.For (preferences.Theme);
            PageView.BackColor = Colors.FromArgb (theme.PageBackground);
            PageView.ForeColor = Colors.FromArgb (theme.PageText);
            PageView.OverrideTextColor = theme.OverrideBookColors;
        }

        private LayoutSettings? CurrentLayoutSettings ()
        {
            var size = PageView.ClientSize;
            return size.Width < 100 || size.Height < 100 ? null : preferences.ToLayoutSettings (size.Width, size.Height);
        }

        /// <summary>Re-paginates for the current size and preferences, coalescing bursts (a window drag) into the latest.</summary>
        private void RequestRelayout ()
        {
            if (session is null || closed)
                return;
            relayout_pending = true;
            if (!relayout_running)
                operations.Run (RelayoutAsync);
        }

        private async Task RelayoutAsync ()
        {
            relayout_running = true;
            try {
                while (relayout_pending && session is not null && !closed) {
                    relayout_pending = false;
                    if (CurrentLayoutSettings () is not { } settings || settings == session.Settings)
                        continue;
                    await session.ChangeSettingsAsync (settings);
                    ShowCurrent ();
                }
            } finally {
                relayout_running = false;
            }
        }

        private void PositionChanged ()
        {
            ShowCurrent ();
            save_pending = true;
            save_timer.Stop ();
            save_timer.Start ();
        }

        private async Task SaveProgressAsync (CancellationToken ct)
        {
            if (!save_pending || session is null)
                return;
            save_pending = false;
            await library.SaveProgressAsync (book.Id, session.Locator, ct);
        }

        private void ShowCurrent ()
        {
            if (session is null)
                return;

            // Start decoding the page's images now rather than when it is first painted.
            foreach (var item in session.Page.Items.OfType<Layout.ImageItem> ())
                Image (item.Path);
            if (PageView.Page != session.Page)
                highlight_bar.HideBar ();
            PageView.ShowPage (session.Page, session.Settings);
            RefreshMarks ();
            chapter_label.Text = session.CurrentTocEntry ()?.Title ?? book.Title;
            page_label.Text = string.Format (CultureInfo.CurrentCulture, "Page {0} of {1}", session.PageIndex + 1, session.Chapter.Pages.Count);
            percent_label.Text = session.Locator.Percent.ToString ("P0", CultureInfo.CurrentCulture);
            if (side_content == contents)
                SelectCurrentContentsEntry ();
        }

        // ── Highlights, notes and bookmarks ──────────────────────────────────────

        /// <summary>Highlights the selected passage in <paramref name="color"/>, optionally with a note.</summary>
        public async Task<Annotation?> HighlightSelectionAsync (string color = HighlightColors.Default, string? note = null)
        {
            if (selection is not { } current || session is null)
                return null;
            var saved = await library.SaveAnnotationAsync (book.Id, ReadingSession.NewHighlight (current, color, note));
            selection = null;
            PageView.ClearSelection ();
            highlight_bar.HideBar ();
            await ReloadAnnotationsAsync ();
            return saved;
        }

        /// <summary>Changes a highlight's colour and/or note (null leaves it as it is).</summary>
        public async Task ChangeAnnotationAsync (Annotation annotation, string? color = null, string? note = null)
        {
            var changed = annotation with { Color = color ?? annotation.Color, Note = note ?? annotation.Note };
            if (changed != annotation)
                await library.SaveAnnotationAsync (book.Id, changed);
            highlight_bar.HideBar ();
            await ReloadAnnotationsAsync ();
        }

        public async Task DeleteAnnotationAsync (Annotation annotation)
        {
            await library.DeleteAnnotationAsync (book.Id, annotation.Id);
            highlight_bar.HideBar ();
            await ReloadAnnotationsAsync ();
        }

        /// <summary>Bookmarks the page, or removes its bookmark.</summary>
        public async Task ToggleBookmarkAsync ()
        {
            if (session is null || closed)
                return;
            if (session.BookmarkOnPage (annotations) is { } existing)
                await library.DeleteAnnotationAsync (book.Id, existing.Id);
            else
                await library.SaveAnnotationAsync (book.Id, session.NewBookmark ());
            await ReloadAnnotationsAsync ();
        }

        public async Task GoToAsync (Annotation annotation)
        {
            if (session is null || closed || !await session.GoToAsync (annotation))
                return;
            PositionChanged ();
            if (IsNarrow)
                ToggleSide (null);
            PageView.Focus ();
        }

        /// <summary>Re-reads the annotations (after a change here, or another device's arriving by sync).</summary>
        public async Task ReloadAnnotationsAsync ()
        {
            if (closed)
                return;
            annotations = await library.GetAnnotationsAsync (book.Id);
            await FillNotesAsync ();
            RefreshMarks ();
        }

        /// <summary>Everything in the Notes panel as Markdown.</summary>
        public string ExportMarkdown ()
            => AnnotationExport.ToMarkdown (book.Title, book.Authors, annotations, a => chapter_titles.GetValueOrDefault (a.Id));

        private IReadOnlyDictionary<Guid, string?> chapter_titles = new Dictionary<Guid, string?> ();

        private async Task ExportToFileAsync ()
        {
            using var dialog = new SaveFileDialog {
                Title = "Export highlights and notes",
                Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*",
                DefaultExt = "md",
                FileName = SafeFileName (book.Title) + " - notes.md",
            };
            if (await dialog.ShowDialogAsync () != DialogResult.OK || string.IsNullOrEmpty (dialog.FileName))
                return;
            await File.WriteAllTextAsync (dialog.FileName, ExportMarkdown ());
        }

        private static string SafeFileName (string name)
        {
            var invalid = Path.GetInvalidFileNameChars ().Concat (['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet ();
            var safe = new string (name.Select (c => invalid.Contains (c) ? ' ' : c).ToArray ()).Trim ();
            return safe.Length == 0 ? "Book" : safe.Length > 80 ? safe[..80].TrimEnd () : safe;
        }

        private async Task FillNotesAsync ()
        {
            if (session is null)
                return;
            chapter_titles = await session.ChapterTitlesAsync (annotations);
            notes.Show (annotations, a => chapter_titles.GetValueOrDefault (a.Id));
        }

        private void RefreshMarks ()
        {
            if (session is null)
                return;
            PageView.Highlights = session.HighlightsOnPage (annotations)
                .Select (h => new PageHighlight (h.Annotation, h.Rects, HighlightColors.ArgbOf (h.Annotation.Color), h.Annotation.HasNote))
                .ToList ();
            var bookmarked = session.BookmarkOnPage (annotations) is not null;
            PageView.Bookmarked = bookmarked;
            bookmark_button.Checked = bookmarked;
            bookmark_button.ToolTipText = bookmarked ? "Remove this page's bookmark" : "Bookmark this page";
        }

        private void OnSelectionMade (PageSelectionEventArgs e)
        {
            selection = session?.Select (e.Start, e.End);
            if (selection is null) {
                PageView.ClearSelection ();
                return;
            }
            highlight_bar.ShowFor (null);
            PlaceBar (e.Bounds);
        }

        /// <summary>Puts the highlight bar just above the passage (in page view coordinates), or below it when there is no room.</summary>
        private void PlaceBar (Rectangle passage)
        {
            var size = highlight_bar.ContentSize;
            var origin = PointToClient (PageView.PointToScreen (Point.Empty));
            var x = Math.Clamp (origin.X + passage.X + passage.Width / 2 - size.Width / 2, 4, Math.Max (4, ClientSize.Width - size.Width - 4));
            var y = origin.Y + passage.Y - size.Height - 8;
            if (y < origin.Y + 4)
                y = origin.Y + passage.Bottom + 8;
            highlight_bar.SetBounds (x, Math.Min (y, Math.Max (0, ClientSize.Height - size.Height)), size.Width, size.Height);
            highlight_bar.BringToFront ();
        }

        private async Task OnHighlightActionAsync (HighlightActionEventArgs e)
        {
            var target = highlight_bar.Target;
            var text = target?.Text ?? selection?.Text;
            switch (e.Action) {
            case HighlightAction.Color when target is not null:
                await ChangeAnnotationAsync (target, color: e.Color);
                break;
            case HighlightAction.Color:
                await HighlightSelectionAsync (e.Color ?? HighlightColors.Default);
                break;
            case HighlightAction.Note:
                EditNote (target, target is null ? selection : null);
                break;
            case HighlightAction.Copy:
                if (text is not null)
                    await Clipboard.SetTextAsync (text);
                DismissSelection ();
                break;
            case HighlightAction.Delete when target is not null:
                await DeleteAnnotationAsync (target);
                break;
            default:
                DismissSelection ();
                break;
            }
        }

        private void DismissSelection ()
        {
            selection = null;
            highlight_bar.HideBar ();
            PageView.ClearSelection ();
        }

        /// <summary>Opens the note editor for an existing highlight, or for a selection (saved as a highlight with the note).</summary>
        public void EditNote (Annotation? target, SelectedPassage? pending)
        {
            if (target is null && pending is null)
                return;
            note_target = target;
            note_selection = pending;
            highlight_bar.HideBar ();
            note_editor.PlaceIn (body.Bounds);
            note_editor.Edit (target?.Text ?? pending!.Text, target?.Note);
        }

        private async Task SaveNoteAsync (string text)
        {
            var note = string.IsNullOrWhiteSpace (text) ? null : text.Trim ();
            var target = note_target;
            var pending = note_selection;
            CloseNoteEditor ();
            if (target is not null) {
                await library.SaveAnnotationAsync (book.Id, target with { Note = note });
                await ReloadAnnotationsAsync ();
            } else if (pending is not null) {
                selection = pending;
                await HighlightSelectionAsync (HighlightColors.Default, note);
            }
        }

        private void CloseNoteEditor ()
        {
            note_target = null;
            note_selection = null;
            note_editor.Visible = false;
            PageView.ClearSelection ();
            PageView.Focus ();
        }

        private void FillContents ()
        {
            contents.Nodes.Clear ();
            var entries = session!.Book.TableOfContents;
            if (entries.Count == 0) {
                // No table of contents: list the spine instead, so every chapter can still be reached.
                entries = session.Book.Spine.Where (s => s.Linear)
                    .Select (s => new TocEntry { Title = $"Section {s.Index + 1}", SpineIndex = s.Index, Path = s.Content.Path })
                    .ToList ();
            }
            foreach (var entry in entries)
                contents.Nodes.Add (Node (entry));
            contents.ExpandAll ();

            static TreeNode Node (TocEntry entry)
            {
                var node = new TreeNode (entry.Title) { Tag = entry };
                foreach (var child in entry.Children)
                    node.Nodes.Add (Node (child));
                return node;
            }
        }

        private void SelectCurrentContentsEntry ()
        {
            if (session?.CurrentTocEntry () is not { } current)
                return;
            foreach (var node in AllNodes (contents.Nodes)) {
                if (node.Tag == current) {
                    contents.SelectedNode = node;
                    node.EnsureVisible ();
                    return;
                }
            }

            static IEnumerable<TreeNode> AllNodes (IEnumerable<TreeNode> nodes)
            {
                foreach (var node in nodes) {
                    yield return node;
                    foreach (var child in AllNodes (node.Nodes))
                        yield return child;
                }
            }
        }

        /// <summary>Images for the page view, decoded on first use; null (a placeholder) until loaded.</summary>
        private SKImage? Image (string path)
        {
            if (images.TryGetValue (path, out var image))
                return image;
            images[path] = null;
            if (session is { } current)
                operations.Run (() => LoadImageAsync (current, path));
            return null;
        }

        private async Task LoadImageAsync (ReadingSession owner, string path)
        {
            byte[] bytes;
            try {
                bytes = await owner.Book.ReadResourceAsync (path);
            } catch (Exception ex) when (ex is FileNotFoundException or EpubException) {
                return;
            }
            var decoded = await Task.Run (() => SKImage.FromEncodedData (bytes));
            if (closed || owner != session) {
                decoded?.Dispose ();
                return;
            }
            images[path] = decoded;
            PageView.Invalidate ();
        }

        protected override void Dispose (bool disposing)
        {
            if (disposing) {
                save_timer.Dispose ();
                foreach (var image in images.Values)
                    image?.Dispose ();
                images.Clear ();
            }
            base.Dispose (disposing);
        }
    }
}
