using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// The app's top-level window. Every host (desktop, Android, iOS, browser) and the Headless UI
    /// tests show this same form; only the backend underneath differs. It shows the library or one open
    /// book, and owns the theme: chrome colours are set on the form and reach every control ambiently.
    /// </summary>
    public class MainForm : Form
    {
        private readonly Func<CancellationToken, Task<AppServices>> services_factory;
        private readonly Label error_banner = new () {
            Dock = DockStyle.Top,
            Visible = false,
            AutoSize = false,
            Height = 32,
            Padding = new Padding (12, 0, 12, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb (0xB0, 0x2A, 0x2A),
            ForeColor = Color.White,
        };
        private readonly Panel content = new () { Dock = DockStyle.Fill };
        private Task? initialize;
        private bool closing_reader;

        /// <summary>The app as the hosts run it: the library in local app data.</summary>
        public MainForm ()
            : this (ct => AppServices.OpenAsync (AppServices.DefaultDataDirectory (), ct))
        {
        }

        public MainForm (Func<CancellationToken, Task<AppServices>> servicesFactory)
        {
            services_factory = servicesFactory;
            Text = "Recital";
            ClientSize = new Size (1000, 700);

            error_banner.Click += (_, _) => error_banner.Visible = false;
            Controls.Add (content);
            Controls.Add (error_banner);

            Operations.Failed += (_, ex) => ShowError (ex);
            ApplyTheme ();
        }

        /// <summary>Asynchronous work started by the UI; tests wait on <see cref="UiOperations.WhenIdle"/>.</summary>
        public UiOperations Operations { get; } = new ();

        public AppServices? Services { get; private set; }
        public LibraryView? Library { get; private set; }
        public ReaderView? Reader { get; private set; }
        public ReaderPreferences Preferences { get; private set; } = new ();
        public string? ErrorText => error_banner.Visible ? error_banner.Text : null;

        protected override void OnLoad (EventArgs e)
        {
            base.OnLoad (e);
            Operations.Run (InitializeAsync);
        }

        /// <summary>Opens the library and shows it. Safe to call more than once.</summary>
        public Task InitializeAsync () => initialize ??= InitializeCoreAsync ();

        private async Task InitializeCoreAsync ()
        {
            var services = await services_factory (CancellationToken.None);
            Services = services;
            Preferences = await services.Library.LoadPreferencesAsync ();
            ApplyTheme ();

            var library = new LibraryView (services.Library, Operations) { Dock = DockStyle.Fill };
            library.OpenRequested += (_, book) => Operations.Run (() => OpenBookAsync (book));
            library.SyncFolderChosen += (_, folder) => Operations.Run (() => ChangeSyncFolderAsync (folder));
            library.SyncNowRequested += (_, _) => Operations.Run (SyncNowAsync);
            var previous = Library;
            Library = library;
            if (services.Sync is { } sync)
                sync.RoundCompleted += OnSyncRoundCompleted;
            ShowSyncStatus ();
            ApplyTheme ();
            Show (library);
            previous?.Dispose ();
            await library.RefreshAsync ();
        }

        /// <summary>
        /// Makes <paramref name="folder"/> this device's sync home (null: stop syncing) and reopens the library
        /// on it. Only where the services have local app data to record the choice in.
        /// </summary>
        public async Task ChangeSyncFolderAsync (string? folder)
        {
            if (Services?.DataDirectory is not { } dataDirectory)
                throw new InvalidOperationException ("This device has no local app data to keep a sync folder setting in.");
            if (folder == Services.SyncFolder)
                return;

            await AppServices.SetSyncFolderAsync (dataDirectory, folder);
            await CloseBookAsync ();
            var old = Services;
            if (old.Sync is { } sync)
                sync.RoundCompleted -= OnSyncRoundCompleted;
            Services = null;
            await old.DisposeAsync ();

            initialize = null;
            await InitializeAsync ();
            if (Services?.Sync is { } started)
                await started.SyncOnceAsync ();
        }

        /// <summary>Runs a sync round now; the library refreshes when it brings changes.</summary>
        public async Task SyncNowAsync ()
        {
            if (Services?.Sync is { } sync)
                await sync.SyncOnceAsync ();
        }

        // Raised on a worker thread by the sync loop.
        private void OnSyncRoundCompleted (object? sender, SyncRoundEventArgs e)
        {
            if (IsDisposed)
                return;
            BeginInvoke (() => {
                if (IsDisposed || !ReferenceEquals (sender, Services?.Sync))
                    return;
                ShowSyncStatus ();
                // Another device's changes: show them in the library. An open book keeps its place; the next
                // time it is opened, it opens at the newest position from any device.
                if (e.Round.Pulled.Count > 0 && Reader is null && Library is { } library)
                    Operations.Run (() => library.RefreshAsync ());
            });
        }

        private void ShowSyncStatus ()
        {
            if (Services is { } services)
                Library?.ShowSync (services.CanUseSyncFolder, services.SyncFolder, services.Sync?.Status);
        }

        /// <summary>Switches to the reader and opens <paramref name="book"/> at its saved position.</summary>
        public async Task OpenBookAsync (LibraryBook book)
        {
            if (Services is null)
                throw new InvalidOperationException ("The library is not open yet.");
            if (Reader is not null)
                await CloseBookAsync ();

            var reader = new ReaderView (Services.Library, book, Preferences, Operations) { Dock = DockStyle.Fill };
            reader.CloseRequested += (_, _) => Operations.Run (CloseBookAsync);
            reader.PreferencesChanged += (_, preferences) => {
                Preferences = preferences;
                ApplyTheme ();
                Operations.Run (() => Services.Library.SavePreferencesAsync (preferences));
            };
            Reader = reader;
            Text = $"{book.Title} - Recital";
            ApplyTheme ();
            Show (reader);
            await reader.OpenAsync ();
        }

        /// <summary>Saves the position, closes the book and goes back to the library.</summary>
        public async Task CloseBookAsync ()
        {
            if (Reader is not { } reader)
                return;
            Reader = null;
            await reader.CloseAsync ();
            Text = "Recital";
            if (Library is not null) {
                Show (Library);
                await Library.RefreshAsync ();
            }
            reader.Dispose ();
        }

        protected override void OnFormClosing (FormClosingEventArgs e)
        {
            base.OnFormClosing (e);
            // Saving the position is asynchronous: hold the close until it is written, then close again.
            if (!e.Cancel && Reader is not null && !closing_reader) {
                e.Cancel = true;
                closing_reader = true;
                Operations.Run (async () => {
                    try {
                        await CloseBookAsync ();
                    } finally {
                        Close ();
                    }
                });
            }
        }

        protected override void OnFormClosed (FormClosedEventArgs e)
        {
            base.OnFormClosed (e);
            // Stops the sync loop; a round in flight finishes on its own (each write is atomic).
            Services?.Dispose ();
        }

        private void Show (Control view)
        {
            content.SuspendLayout ();
            content.Controls.Clear ();
            content.Controls.Add (view);
            content.ResumeLayout ();
            error_banner.Visible = false;
            if (view is ReaderView reader)
                reader.PageView.Focus ();
        }

        private void ApplyTheme ()
        {
            var theme = ReaderTheme.For (Preferences.Theme);
            BackColor = Colors.FromArgb (theme.ChromeBackground);
            ForeColor = Colors.FromArgb (theme.ChromeText);
            if (Library is not null)
                Library.Accent = Colors.FromArgb (theme.Accent);

            // A form is a window here, not a Control, so the walk starts at its children.
            foreach (Control child in Controls)
                ThemePainter.Apply (child, theme);
            // The library and the reader's side panels are not always in the form's tree.
            if (Library is not null)
                ThemePainter.Apply (Library, theme);
            if (Reader is not null) {
                ThemePainter.Apply (Reader, theme);
                ThemePainter.Apply (Reader.Contents, theme);
                ThemePainter.Apply (Reader.Typography, theme);
            }
        }

        private void ShowError (Exception ex)
        {
            error_banner.Text = ex.Message;
            error_banner.Visible = true;
            error_banner.BringToFront ();
        }
    }
}
