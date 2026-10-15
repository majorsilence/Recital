using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;

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
            Services = await services_factory (CancellationToken.None);
            Preferences = await Services.Library.LoadPreferencesAsync ();
            ApplyTheme ();

            Library = new LibraryView (Services.Library, Operations) { Dock = DockStyle.Fill };
            Library.OpenRequested += (_, book) => Operations.Run (() => OpenBookAsync (book));
            ApplyTheme ();
            Show (Library);
            await Library.RefreshAsync ();
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
