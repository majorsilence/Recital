using System.Drawing;
using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Forms.Backends;
using Majorsilence.Forms.Drawing;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;

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
        private readonly Dictionary<SyncHomeKind, ToolStripMenuItem> home_items = [];
        private readonly ToolStripButton import_remote = new ("Import from…") { Visible = false };
        private readonly WebDavPanel webdav = new ();
        private readonly RemoteBrowserPanel browser = new ();
        private IStorageProvider? remote;
        private string remote_name = "";
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

            foreach (var (kind, text) in new[] {
                (SyncHomeKind.LocalFolder, "Local folder…"), (SyncHomeKind.WebDav, "WebDAV…"),
                (SyncHomeKind.GoogleDrive, "Google Drive"), (SyncHomeKind.Dropbox, "Dropbox"), (SyncHomeKind.OneDrive, "OneDrive") }) {
                var item = new ToolStripMenuItem (text) { Tag = kind };
                item.Click += (_, _) => ChooseHome (kind);
                home_items[kind] = item;
                sync_button.DropDownItems.Add (item);
            }
            sync_now.Click += (_, _) => SyncNowRequested?.Invoke (this, EventArgs.Empty);
            stop_syncing.Click += (_, _) => SyncFolderChosen?.Invoke (this, null);
            sync_button.DropDownItems.AddRange ([new ToolStripSeparator (), sync_now, stop_syncing]);
            import_remote.Click += (_, _) => operations.Run (OpenRemoteBrowserAsync);

            webdav.ConnectRequested += (_, credentials) => WebDavRequested?.Invoke (this, credentials);
            webdav.Cancelled += (_, _) => webdav.Visible = false;
            browser.NavigateRequested += (_, path) => operations.Run (() => browser.ShowFolderAsync (path));
            browser.ImportRequested += (_, _) => operations.Run (() => ImportFilesAsync (browser.PickedBooks ()));

            tool_strip.Items.AddRange ([import, import_remote, new ToolStripSeparator (), new ToolStripLabel ("Search"), search, sort_button, view_button, sync_button]);
            status_strip.Items.Add (status);
            status_strip.Items.Add (sync_status);

            list.Columns.Add ("Title", 320);
            list.Columns.Add ("Author", 200);
            list.Columns.Add ("Progress", 90, HorizontalAlignment.Right);
            list.Columns.Add ("Added", 120);
            list.DrawItem += DrawTile;
            // On a touch screen a tap opens the book; a desktop keeps double-click (a click selects).
            if (OperatingSystem.IsAndroid () || OperatingSystem.IsIOS ())
                list.Activation = ItemActivation.OneClick;
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

            Controls.Add (webdav);
            Controls.Add (browser);
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

        /// <summary>The user chose Google Drive, Dropbox or OneDrive: sign in.</summary>
        public event EventHandler<SyncHomeKind>? CloudRequested;

        /// <summary>The user filled in the WebDAV form.</summary>
        public event EventHandler<WebDavCredentials>? WebDavRequested;

        public event EventHandler? SyncNowRequested;

        public WebDavPanel WebDav => webdav;
        public RemoteBrowserPanel RemoteBrowser => browser;

        /// <summary>
        /// Shows the sync menu (with the sync homes this build and device can use), the sync state in the status
        /// bar, and "Import from …" when the sync home can be browsed.
        /// </summary>
        public void ShowSync (SyncMenu menu)
        {
            sync_button.Visible = menu.Available;
            foreach (var (kind, item) in home_items) {
                var possible = menu.CanConnect (kind);
                item.Visible = kind != SyncHomeKind.LocalFolder || possible;
                item.Enabled = possible;
                item.ToolTipText = possible ? "" : "Not set up in this build of Recital";
                item.Checked = menu.Home?.EffectiveKind == kind;
            }
            var syncing = menu.Home is not null;
            sync_now.Enabled = stop_syncing.Enabled = syncing;
            sync_button.ToolTipText = syncing ? "Syncing with " + menu.Home!.DisplayName : "Keep this library in step with your other devices";
            sync_status.Text = SyncText (menu.Home?.DisplayName, menu.Status);

            remote = menu.Remote is { } r && r.Capabilities.HasFlag (StorageCapabilities.Browse) && menu.Home?.EffectiveKind != SyncHomeKind.LocalFolder ? r : null;
            remote_name = menu.Home is { } home ? SyncHome.Label (home.EffectiveKind) : "";
            import_remote.Visible = remote is not null;
            import_remote.Text = "Import from " + remote_name + "…";
        }

        /// <summary>Opens the WebDAV form over the library (Sync > WebDAV…).</summary>
        public void ShowWebDavForm ()
        {
            webdav.SetBounds (Math.Max (0, (Width - 420) / 2), 40, Math.Min (Width, 420), Math.Max (200, Math.Min (Height - 50, 440)));
            webdav.Open ();
        }

        /// <summary>The WebDAV form, after a failed attempt: says why (keeping what was typed).</summary>
        public void ShowWebDavError (string message)
        {
            webdav.Visible = true;
            webdav.BringToFront ();
            webdav.ShowMessage (message);
        }

        /// <summary>A few words on the sync state, for the status bar.</summary>
        public static string SyncText (string? home, SyncStatus? sync)
        {
            if (home is null || sync is null)
                return string.Empty;
            var waiting = sync.Pending > 0 ? $" ({sync.Pending} waiting)" : "";
            return sync.State switch {
                SyncState.Syncing => "Syncing…",
                SyncState.Offline => "Can't reach " + home + ", retrying" + waiting,
                SyncState.SignInNeeded => "Sign in again to sync (Sync menu)" + waiting,
                SyncState.NewerFormat => "The sync folder needs a newer Recital",
                _ when sync.LastSuccess is { } last => "Synced " + last.ToLocalTime ().ToString ("t", CultureInfo.CurrentCulture) + waiting,
                _ => "Not synced yet" + waiting,
            };
        }

        private void ChooseHome (SyncHomeKind kind)
        {
            switch (kind) {
            case SyncHomeKind.LocalFolder:
                operations.Run (ChooseSyncFolderAsync);
                break;
            case SyncHomeKind.WebDav:
                ShowWebDavForm ();
                break;
            default:
                CloudRequested?.Invoke (this, kind);
                break;
            }
        }

        /// <summary>Opens the browser on the sync home's storage, at its root.</summary>
        public async Task OpenRemoteBrowserAsync ()
        {
            if (remote is null)
                return;
            browser.SetBounds (Math.Max (0, (Width - 560) / 2), 30, Math.Min (Width, 560), Math.Max (200, Height - 60));
            browser.Open (remote, remote_name);
            await browser.ShowFolderAsync ("");
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

    /// <summary>What the library's Sync menu shows.</summary>
    /// <param name="Available">The menu shows at all (there is local app data to keep the choice in).</param>
    /// <param name="Home">The current sync home, or null.</param>
    /// <param name="Status">The engine's latest status, or null.</param>
    /// <param name="CanConnect">Whether each kind of sync home can be chosen here.</param>
    /// <param name="Remote">The sync home's storage, offered for importing when it can be browsed.</param>
    public sealed record SyncMenu (bool Available, SyncHomeSettings? Home, SyncStatus? Status, Func<SyncHomeKind, bool> CanConnect, IStorageProvider? Remote)
    {
        public static SyncMenu Hidden { get; } = new (false, null, null, _ => false, null);
    }
}
