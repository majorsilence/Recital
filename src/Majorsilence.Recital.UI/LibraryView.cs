using System.Drawing;
using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Forms.Backends;
using Majorsilence.Forms.Drawing;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Sync;

using Font = Majorsilence.Forms.Drawing.Font;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// The library screen: a grid of covers (owner-drawn <see cref="ListView"/> tiles) or a details list,
    /// with search, sort, and import from the OS file picker or by dropping files on it.
    /// </summary>
    public class LibraryView : UserControl
    {
        private static readonly Size CoverSize = new (120, 180);

        private readonly LibraryService library;
        private readonly UiOperations operations;
        private readonly ListView list = new () {
            Dock = DockStyle.Fill,
            View = View.LargeIcon,
            MultiSelect = false,
            OwnerDraw = true,
            FullRowSelect = true,
            TileSize = new Size (CoverSize.Width + 24, CoverSize.Height + 64),
        };
        private readonly ToolStrip tool_strip = new () { Dock = DockStyle.Top };
        private readonly ToolStripTextBox search = new () { ToolTipText = "Search titles and authors" };
        private readonly ToolStripDropDownButton sort_button = new ();
        private readonly ToolStripButton view_button = new ("List");
        private readonly StatusStrip status_strip = new () { Dock = DockStyle.Bottom, SizingGrip = false };
        private readonly ToolStripStatusLabel status = new () { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ToolStripStatusLabel sync_status = new () { TextAlign = ContentAlignment.MiddleRight };
        private readonly ToolStripDropDownButton sync_button = new ("Sync") { Visible = false, Alignment = ToolStripItemAlignment.Right };
        private readonly ToolStripMenuItem sync_now = new ("Sync now");
        private readonly ToolStripMenuItem stop_syncing = new ("Stop syncing");
        private readonly Label empty = new () {
            Text = "Your library is empty.\nImport an EPUB, or drop one here, to start reading.",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill,
            Visible = false,
        };
        private readonly Dictionary<string, Bitmap?> covers = new (StringComparer.Ordinal);
        private IReadOnlyList<LibraryBook> books = [];
        private LibrarySort sort = LibrarySort.Recent;

        public LibraryView (LibraryService library, UiOperations operations)
        {
            this.library = library;
            this.operations = operations;

            var import = new ToolStripButton ("Import…");
            import.Click += (_, _) => operations.Run (ImportFromPickerAsync);
            search.TextChanged += (_, _) => ShowBooks ();
            foreach (var option in Enum.GetValues<LibrarySort> ()) {
                var item = new ToolStripMenuItem (SortName (option)) { Tag = option };
                item.Click += (_, _) => {
                    sort = option;
                    ShowBooks ();
                };
                sort_button.DropDownItems.Add (item);
            }
            view_button.Click += (_, _) => ToggleView ();

            var choose_folder = new ToolStripMenuItem ("Choose sync folder…");
            choose_folder.Click += (_, _) => operations.Run (ChooseSyncFolderAsync);
            sync_now.Click += (_, _) => SyncNowRequested?.Invoke (this, EventArgs.Empty);
            stop_syncing.Click += (_, _) => SyncFolderChosen?.Invoke (this, null);
            sync_button.DropDownItems.AddRange ([choose_folder, sync_now, stop_syncing]);

            tool_strip.Items.AddRange ([import, new ToolStripSeparator (), new ToolStripLabel ("Search"), search, sort_button, view_button, sync_button]);
            status_strip.Items.Add (status);
            status_strip.Items.Add (sync_status);

            list.Columns.Add ("Title", 320);
            list.Columns.Add ("Author", 200);
            list.Columns.Add ("Progress", 90, HorizontalAlignment.Right);
            list.Columns.Add ("Added", 120);
            list.DrawItem += DrawTile;
            list.ItemActivate += (_, _) => {
                if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is LibraryBook book)
                    OpenRequested?.Invoke (this, book);
            };

            var menu = new ContextMenuStrip ();
            var open = new ToolStripMenuItem ("Open");
            open.Click += (_, _) => {
                if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is LibraryBook book)
                    OpenRequested?.Invoke (this, book);
            };
            menu.Items.Add (open);
            list.ContextMenuStrip = menu;

            AllowDrop = true;
            DragEnter += (_, e) => {
                if (e.Data?.GetDataPresent (DataFormats.FileDrop) == true)
                    e.Effect = DragDropEffects.Copy;
            };
            DragDrop += (_, e) => {
                if (e.Data?.GetData (DataFormats.FileDrop) is string[] paths)
                    operations.Run (() => ImportFilesAsync (paths));
            };

            Controls.Add (list);
            Controls.Add (empty);
            Controls.Add (tool_strip);
            Controls.Add (status_strip);
            UpdateSortLabel ();
        }

        public ListView List => list;
        public IReadOnlyList<LibraryBook> Books => books;
        public string StatusText => status.Text ?? string.Empty;
        public string SyncStatusText => sync_status.Text ?? string.Empty;

        /// <summary>The books shown, in display order, after search and sort.</summary>
        public IReadOnlyList<LibraryBook> Shown => list.Items.Cast<ListViewItem> ().Select (i => (LibraryBook)i.Tag!).ToList ();

        public string SearchText {
            get => search.Text ?? string.Empty;
            set => search.Text = value;
        }

        public LibrarySort Sort {
            get => sort;
            set {
                sort = value;
                ShowBooks ();
            }
        }

        /// <summary>The theme's accent: selection and progress bars.</summary>
        public Color Accent {
            get;
            set {
                field = value;
                list.Invalidate ();
            }
        } = Colors.FromArgb (Core.Reading.ReaderTheme.Light.Accent);

        public event EventHandler<LibraryBook>? OpenRequested;

        /// <summary>The user picked a folder to sync with (null: stop syncing).</summary>
        public event EventHandler<string?>? SyncFolderChosen;

        public event EventHandler? SyncNowRequested;

        /// <summary>
        /// Shows the sync menu where a local sync folder is possible, and the sync state in the status bar.
        /// </summary>
        /// <param name="available">A sync folder can be chosen here (the desktop).</param>
        /// <param name="folder">The folder synced with, or null.</param>
        /// <param name="sync">The engine's latest status, or null when not syncing.</param>
        public void ShowSync (bool available, string? folder, SyncStatus? sync)
        {
            sync_button.Visible = available;
            sync_now.Enabled = stop_syncing.Enabled = folder is not null;
            sync_button.ToolTipText = folder is null ? "Keep this library in step with other devices through a shared folder" : "Syncing with " + folder;
            sync_status.Text = SyncText (folder, sync);
        }

        /// <summary>A few words on the sync state, for the status bar.</summary>
        public static string SyncText (string? folder, SyncStatus? sync)
        {
            if (folder is null || sync is null)
                return string.Empty;
            var waiting = sync.Pending > 0 ? $" ({sync.Pending} waiting)" : "";
            return sync.State switch {
                SyncState.Syncing => "Syncing…",
                SyncState.Offline => "Sync folder unavailable, retrying" + waiting,
                SyncState.NewerFormat => "Sync folder needs a newer Recital",
                _ when sync.LastSuccess is { } last => "Synced " + last.ToLocalTime ().ToString ("t", CultureInfo.CurrentCulture) + waiting,
                _ => "Not synced yet" + waiting,
            };
        }

        /// <summary>Re-reads the library from the sync home.</summary>
        public async Task RefreshAsync (CancellationToken ct = default)
        {
            books = await library.ListAsync (ct);
            foreach (var book in books)
                if (!covers.ContainsKey (book.Id))
                    covers[book.Id] = book.Meta.HasCover ? await LoadCoverAsync (book.Id, ct) : null;
            ShowBooks ();
        }

        /// <summary>Imports files by path, reporting each outcome in the status bar.</summary>
        public Task ImportFilesAsync (IEnumerable<string> paths, CancellationToken ct = default)
            => ImportFilesAsync (paths.Select (PickedFile.FromPath), ct);

        /// <summary>
        /// Imports picked files, reporting each outcome in the status bar. They are read as streams, so files
        /// the platform gives no path (Android content URIs, iOS and browser handles) import too.
        /// </summary>
        public async Task ImportFilesAsync (IEnumerable<PickedFile> files, CancellationToken ct = default)
        {
            var added = 0;
            var messages = new List<string> ();
            foreach (var file in files) {
                try {
                    await using var stream = await file.OpenReadAsync (ct);
                    var result = await library.ImportAsync (stream, file.Name, ct);
                    if (result.AlreadyInLibrary)
                        messages.Add ($"{result.Book.Title} is already in your library.");
                    else
                        added++;
                } catch (Exception ex) when (ex is BookImportException or IOException or UnauthorizedAccessException) {
                    messages.Add (ex is BookImportException ? ex.Message : $"Could not read {file.Name}: {ex.Message}");
                }
            }

            if (added > 0)
                messages.Insert (0, added == 1 ? "Added 1 book." : $"Added {added} books.");
            status.Text = string.Join (" ", messages);
            await RefreshAsync (ct);
        }

        private async Task ImportFromPickerAsync ()
        {
            using var dialog = new OpenFileDialog {
                Title = "Import books",
                Filter = "EPUB books (*.epub)|*.epub",
                Multiselect = true,
            };
            if (await dialog.ShowDialogAsync () != DialogResult.OK)
                return;
            await ImportFilesAsync (dialog.SelectedFiles);
        }

        /// <summary>Asks for a folder to sync with (the OS folder picker), then raises <see cref="SyncFolderChosen"/>.</summary>
        public async Task ChooseSyncFolderAsync ()
        {
            using var dialog = new FolderBrowserDialog {
                Description = "Choose a folder your other devices can see too: a Dropbox, Google Drive, OneDrive or iCloud Drive folder, or a network share. Recital keeps your library in a Recital folder inside it.",
                UseDescriptionForTitle = false,
                ShowNewFolderButton = true,
            };
            if (await dialog.ShowDialogAsync () != DialogResult.OK || string.IsNullOrEmpty (dialog.SelectedPath))
                return;
            SyncFolderChosen?.Invoke (this, dialog.SelectedPath);
        }

        private async Task<Bitmap?> LoadCoverAsync (string id, CancellationToken ct)
        {
            var bytes = await library.ReadCoverAsync (id, ct);
            if (bytes is null)
                return null;
            try {
                return new Bitmap (new MemoryStream (bytes));
            } catch (ArgumentException) {
                return null;
            }
        }

        private void ShowBooks ()
        {
            var shown = LibraryQuery.Apply (books, SearchText, sort);
            var selected = list.SelectedItems.Count == 1 ? (list.SelectedItems[0].Tag as LibraryBook)?.Id : null;

            list.BeginUpdate ();
            list.Items.Clear ();
            foreach (var book in shown) {
                var item = new ListViewItem (book.Title) { Tag = book };
                item.SubItems.Add (book.Authors);
                item.SubItems.Add (book.Progress is null ? "" : book.Percent.ToString ("P0", CultureInfo.CurrentCulture));
                item.SubItems.Add (book.Meta.AddedAt.ToLocalTime ().ToString ("d", CultureInfo.CurrentCulture));
                list.Items.Add (item);
                if (book.Id == selected)
                    item.Selected = true;
            }
            list.EndUpdate ();

            empty.Visible = books.Count == 0;
            list.Visible = books.Count > 0;
            if (books.Count > 0 && shown.Count == 0)
                status.Text = $"No books match \"{SearchText}\".";
            UpdateSortLabel ();
        }

        private void ToggleView ()
        {
            list.View = list.View == View.LargeIcon ? View.Details : View.LargeIcon;
            view_button.Text = list.View == View.LargeIcon ? "List" : "Covers";
        }

        private void UpdateSortLabel ()
        {
            sort_button.Text = "Sort: " + SortName (sort);
            foreach (ToolStripMenuItem item in sort_button.DropDownItems)
                item.Checked = (LibrarySort)item.Tag! == sort;
        }

        private static string SortName (LibrarySort sort) => sort switch {
            LibrarySort.Recent => "Recent",
            LibrarySort.Title => "Title",
            LibrarySort.Author => "Author",
            _ => "Progress",
        };

        /// <summary>
        /// A cover tile: the cover (or a lettered placeholder), the title under it, and a progress bar. Owner
        /// draw works in device pixels (plan 0.2), so every length is scaled.
        /// </summary>
        private void DrawTile (object? sender, DrawListViewItemEventArgs e)
        {
            if (list.View != View.LargeIcon || e.Item.Tag is not LibraryBook book) {
                e.DrawDefault = true;
                return;
            }

            var g = e.Graphics;
            var scale = LogicalToDeviceUnits (1000) / 1000f;
            int S (int logical) => (int)Math.Round (logical * scale);

            var fore = ForeColor;
            var back = list.BackColor;
            var bounds = e.Bounds;

            if (e.Item.Selected) {
                using var highlight = new SolidBrush (Colors.Mix (back, Accent, 0.25f));
                g.FillRectangle (highlight, bounds);
            }

            var cover = new Rectangle (bounds.X + (bounds.Width - S (CoverSize.Width)) / 2, bounds.Y + S (8), S (CoverSize.Width), S (CoverSize.Height));
            if (covers.GetValueOrDefault (book.Id) is { } image) {
                // Fit inside the cover box, keeping the aspect ratio, bottom-aligned like books on a shelf.
                var ratio = Math.Min (cover.Width / (float)image.Width, cover.Height / (float)image.Height);
                var size = new Size ((int)(image.Width * ratio), (int)(image.Height * ratio));
                g.DrawImage (image, new Rectangle (cover.X + (cover.Width - size.Width) / 2, cover.Bottom - size.Height, size.Width, size.Height));
            } else {
                using var fill = new SolidBrush (Colors.Mix (back, fore, 0.12f));
                using var ink = new SolidBrush (fore);
                using var font = new Font (Font.FontFamily, Font.Size * 1.1f * scale, FontStyle.Bold);
                using var centred = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.FillRectangle (fill, cover);
                g.DrawString (book.Title, font, ink, RectangleF.Inflate (cover, -S (10), -S (10)), centred);
            }

            var bar = new Rectangle (cover.X, cover.Bottom + S (5), cover.Width, Math.Max (1, S (3)));
            if (book.Progress is not null) {
                using var track = new SolidBrush (Colors.Mix (back, fore, 0.15f));
                using var done = new SolidBrush (Accent);
                g.FillRectangle (track, bar);
                g.FillRectangle (done, new Rectangle (bar.X, bar.Y, (int)(bar.Width * Math.Clamp (book.Percent, 0, 1)), bar.Height));
            }

            using (var text = new SolidBrush (fore))
            using (var font = new Font (Font.FontFamily, Font.Size * scale))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter }) {
                var titleBox = new RectangleF (bounds.X + S (4), bar.Bottom + S (4), bounds.Width - S (8), bounds.Bottom - bar.Bottom - S (6));
                g.DrawString (book.Title, font, text, titleBox, format);
            }
        }

        protected override void Dispose (bool disposing)
        {
            if (disposing) {
                foreach (var cover in covers.Values)
                    cover?.Dispose ();
                covers.Clear ();
            }
            base.Dispose (disposing);
        }
    }
}
