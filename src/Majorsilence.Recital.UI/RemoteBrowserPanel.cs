using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Forms.Backends;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// Browses the sync home's storage (WebDAV, Dropbox, OneDrive) for EPUBs to import: folders to open, books
    /// to pick. Importing copies a book into the library, as importing from the file picker does.
    /// </summary>
    public class RemoteBrowserPanel : Panel
    {
        private readonly Label title = new () { Dock = DockStyle.Top, Height = 30, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding (8, 0, 8, 0) };
        private readonly ToolStrip bar = new () { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        private readonly ToolStripButton up = new ("↑ Up");
        private readonly ToolStripLabel location = new ();
        private readonly ListView list = new () { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
        private readonly Button import = new () { Text = "Import", AutoSize = true, Enabled = false };
        private IStorageProvider? provider;
        private string folder = "";

        public RemoteBrowserPanel ()
        {
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            up.Click += (_, _) => NavigateRequested?.Invoke (this, StoragePath.Parent (folder));
            bar.Items.AddRange ([up, new ToolStripSeparator (), location]);
            list.Columns.Add ("Name", 360);
            list.Columns.Add ("Size", 90, HorizontalAlignment.Right);
            list.ItemActivate += (_, _) => {
                if (list.SelectedItems.Count == 1 && list.SelectedItems[0].Tag is StorageEntry { IsFolder: true } entry)
                    NavigateRequested?.Invoke (this, entry.Path);
                else if (SelectedBooks.Count > 0)
                    ImportRequested?.Invoke (this, EventArgs.Empty);
            };
            list.SelectedIndexChanged += (_, _) => import.Enabled = SelectedBooks.Count > 0;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding (8) };
            var cancel = new Button { Text = "Close", AutoSize = true };
            import.Click += (_, _) => ImportRequested?.Invoke (this, EventArgs.Empty);
            cancel.Click += (_, _) => Visible = false;
            buttons.Controls.Add (cancel);
            buttons.Controls.Add (import);

            Controls.Add (list);
            Controls.Add (bar);
            Controls.Add (title);
            Controls.Add (buttons);
        }

        public ListView List => list;
        public string Folder => folder;

        /// <summary>The EPUB files selected.</summary>
        public IReadOnlyList<StorageEntry> SelectedBooks => list.SelectedItems.Cast<ListViewItem> ()
            .Select (i => i.Tag).OfType<StorageEntry> ().Where (e => !e.IsFolder).ToList ();

        public event EventHandler<string>? NavigateRequested;
        public event EventHandler? ImportRequested;

        /// <summary>Opens on <paramref name="storage"/>'s root, named <paramref name="name"/>.</summary>
        public void Open (IStorageProvider storage, string name)
        {
            provider = storage;
            title.Text = "Import from " + name;
            Visible = true;
            BringToFront ();
        }

        /// <summary>Lists a folder: subfolders first, then EPUB files.</summary>
        public async Task ShowFolderAsync (string path, CancellationToken ct = default)
        {
            if (provider is null)
                return;
            var entries = await provider.ListAsync (path, ct);
            folder = path;
            location.Text = path.Length == 0 ? "/" : "/" + path;
            up.Enabled = path.Length > 0;
            list.BeginUpdate ();
            list.Items.Clear ();
            foreach (var entry in entries.Where (e => e.IsFolder).OrderBy (e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                list.Items.Add (new ListViewItem ([entry.Name + "/", ""]) { Tag = entry });
            foreach (var entry in entries.Where (e => !e.IsFolder && e.Name.EndsWith (".epub", StringComparison.OrdinalIgnoreCase)).OrderBy (e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                list.Items.Add (new ListViewItem ([entry.Name, FileSize (entry.Size)]) { Tag = entry });
            list.EndUpdate ();
            import.Enabled = false;
        }

        /// <summary>The selected books as picked files the library can import.</summary>
        public IReadOnlyList<PickedFile> PickedBooks ()
        {
            var storage = provider ?? throw new InvalidOperationException ("Not open.");
            return SelectedBooks.Select (e => new PickedFile (e.Name, null, ct => storage.OpenReadAsync (e.Path, ct))).ToList ();
        }

        private static string FileSize (long bytes)
            => bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString ("0.0", CultureInfo.CurrentCulture) + " MB" : Math.Max (1, bytes / 1024).ToString (CultureInfo.CurrentCulture) + " KB";
    }
}
