using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Recital.Core;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Platform;
using Majorsilence.Recital.Storage;

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
        private IReadOnlyList<Tts.ITtsEngine>? tts_engines;
        private bool custom_tts_engines;
        private readonly TtsSettingsPanel tts_settings = new (Tts.Platform.TtsEngines.CloudServices);
        private bool closing_reader;

        /// <summary>The app as the hosts run it: the library in local app data, secrets in the OS secure store.</summary>
        public MainForm ()
            : this (ct => AppServices.OpenAsync (AppServices.DefaultDataDirectory (), DefaultPlatform (), ct))
        {
        }

        /// <summary>
        /// This device's secure store and sign-in (the system browser on the desktop), and the cloud registrations
        /// this build carries (<c>-p:RecitalDropboxAppKey=...</c>, overridable by environment variables).
        /// </summary>
        public static PlatformServices DefaultPlatform ()
        {
            var desktop = OperatingSystem.IsWindows () || OperatingSystem.IsMacOS () || (OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid ());
            var metadata = typeof (MainForm).Assembly.GetCustomAttributes (typeof (System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute> ()
                .Where (a => !string.IsNullOrEmpty (a.Value))
                .ToDictionary (a => a.Key, a => a.Value);
            var clients = new CloudClients (
                metadata.GetValueOrDefault ("RecitalGoogleClientId"),
                metadata.GetValueOrDefault ("RecitalGoogleClientSecret"),
                metadata.GetValueOrDefault ("RecitalDropboxAppKey"),
                metadata.GetValueOrDefault ("RecitalOneDriveClientId")).WithEnvironment ();
            return new PlatformServices (
                Platform.SecretStores.ForCurrentPlatform (),
                desktop ? kind => {
                    var (host, port) = SyncHome.LoopbackRedirect (kind);
                    return new Platform.LoopbackAuthorizationBroker (host: host, port: port);
                } : null,
                clients);
        }

        public MainForm (Func<CancellationToken, Task<AppServices>> servicesFactory)
        {
            services_factory = servicesFactory;
            // The OS voices until InitializeAsync has found the rest (Piper, cloud keys).
            TtsEngines = () => tts_engines ??= [.. HostTtsEngines, .. Tts.Platform.TtsEngines.ForCurrentPlatform (
                Services?.DataDirectory is { } data ? Path.Combine (data, "tts-cache") : null, Services?.DataDirectory, HostAudioOutput)];
            tts_settings.Saved += (_, input) => Operations.Run (() => SaveVoiceSettingsAsync (input));
            tts_settings.DownloadRequested += (_, _) => Operations.Run (DownloadRecitalVoicesAsync);
            Text = "Recital";
            ClientSize = new Size (1000, 700);

            error_banner.Click += (_, _) => error_banner.Visible = false;
            Controls.Add (content);
            Controls.Add (error_banner);
            Controls.Add (tts_settings);

            Operations.Failed += (_, ex) => ShowError (ex);
            ApplyTheme ();
        }

        /// <summary>Asynchronous work started by the UI; tests wait on <see cref="UiOperations.WhenIdle"/>.</summary>
        public UiOperations Operations { get; } = new ();

        public AppServices? Services { get; private set; }

        /// <summary>
        /// Makes the reading-aloud engines (default: this OS's voices, caching audio in app data). Tests set
        /// their own.
        /// </summary>
        public Func<IReadOnlyList<Tts.ITtsEngine>> TtsEngines {
            get;
            set {
                field = value;
                custom_tts_engines = initialize is not null;
            }
        }

        /// <summary>Engines the host supplies itself, offered first (the browser's own voices in the browser head).</summary>
        public IReadOnlyList<Tts.ITtsEngine> HostTtsEngines { get; init; } = [];

        /// <summary>
        /// Reading aloud started or ended, with the book's title: Android keeps a media notification (a foreground
        /// service) up meanwhile, so it goes on with the screen off.
        /// </summary>
        public event EventHandler<ReadingAloudState>? ReadingAloudChanged;

        /// <summary>The host's audio output, where the OS has none Recital knows (phones and tablets), for engines that make audio.</summary>
        public Func<Tts.IAudioOutput>? HostAudioOutput { get; init; }

        public TtsSettingsPanel VoiceSettings => tts_settings;
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
            if (!custom_tts_engines)
                tts_engines = [.. HostTtsEngines, .. await Tts.Platform.TtsEngines.AvailableAsync (services.DataDirectory, services.Platform.Secrets, output: HostAudioOutput)];
            Preferences = await services.Library.LoadPreferencesAsync ();
            ApplyTheme ();

            var library = new LibraryView (services.Library, Operations) { Dock = DockStyle.Fill };
            library.OpenRequested += (_, book) => Operations.Run (() => OpenBookAsync (book));
            library.SyncFolderChosen += (_, folder) => Operations.Run (() => ChangeSyncFolderAsync (folder));
            library.CloudRequested += (_, kind) => Operations.Run (() => ConnectCloudAsync (kind));
            library.WebDavRequested += (_, credentials) => Operations.Run (() => ConnectWebDavAsync (credentials));
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
        public Task ChangeSyncFolderAsync (string? folder)
            => folder == Services?.SyncFolder && folder is not null
                ? Task.CompletedTask
                : ChangeSyncHomeAsync (folder is null ? null : new SyncHomeSettings (SyncPaths.FormatVersion, folder, SyncHomeKind.LocalFolder));

        /// <summary>Signs in to Google Drive, Dropbox or OneDrive in the browser, then syncs with it.</summary>
        public async Task ConnectCloudAsync (SyncHomeKind kind)
        {
            var services = Services ?? throw new InvalidOperationException ("The library is not open yet.");
            var settings = await SyncHome.ConnectCloudAsync (kind, services.Platform);
            await ChangeSyncHomeAsync (settings);
        }

        /// <summary>Checks the WebDAV folder and credentials, then syncs with it; a refusal is shown in the form.</summary>
        public async Task ConnectWebDavAsync (WebDavCredentials credentials)
        {
            var services = Services ?? throw new InvalidOperationException ("The library is not open yet.");
            Library?.WebDav.ShowBusy (true);
            SyncHomeSettings settings;
            try {
                settings = await SyncHome.ConnectWebDavAsync (credentials.Address, credentials.UserName, credentials.Password, services.Platform);
            } catch (Exception ex) when (ex is IOException or ArgumentException) {
                Library?.ShowWebDavError (ex is StorageAuthenticationException ? "The server refused the user name or password." : ex.Message);
                return;
            }
            await ChangeSyncHomeAsync (settings);
        }

        /// <summary>
        /// Makes <paramref name="settings"/> this device's sync home (null: stop syncing, forgetting its
        /// password or sign-in) and reopens the library on it. The local copy of the library is kept either way.
        /// </summary>
        public async Task ChangeSyncHomeAsync (SyncHomeSettings? settings)
        {
            if (Services?.DataDirectory is not { } dataDirectory)
                throw new InvalidOperationException ("This device has no local app data to keep a sync setting in.");

            var old = Services;
            await AppServices.SetSyncHomeAsync (dataDirectory, settings);
            if (old.SyncHome is { } previous && previous.RemoteKey != settings?.RemoteKey
                && (settings is null || previous.EffectiveKind != settings.EffectiveKind || previous.EffectiveKind == SyncHomeKind.WebDav))
                await SyncHome.ForgetSecretsAsync (previous, old.Platform);

            await CloseBookAsync ();
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
                // Highlights and notes made on another device show up in the open book.
                if (Reader is { } reader && e.Round.Pulled.Any (p => p.StartsWith (SyncPaths.StateFolder (reader.Book.Id) + "/", StringComparison.Ordinal)))
                    Operations.Run (reader.ReloadAnnotationsAsync);
            });
        }

        private void ShowSyncStatus ()
        {
            if (Services is { } services)
                Library?.ShowSync (new SyncMenu (services.CanChooseSyncHome, services.SyncHome, services.Sync?.Status, services.CanConnect, services.Remote));
        }

        /// <summary>Switches to the reader and opens <paramref name="book"/> at its saved position.</summary>
        public async Task OpenBookAsync (LibraryBook book)
        {
            if (Services is null)
                throw new InvalidOperationException ("The library is not open yet.");
            if (Reader is not null)
                await CloseBookAsync ();

            var reader = new ReaderView (Services.Library, book, Preferences, Operations) { Dock = DockStyle.Fill, TtsEngines = TtsEngines };
            reader.CloseRequested += (_, _) => Operations.Run (CloseBookAsync);
            reader.VoiceSettingsRequested += (_, _) => Operations.Run (ShowVoiceSettingsAsync);
            reader.ExternalLinkRequested += (_, uri) => Operations.Run (() => OpenExternalLinkAsync (uri));
            reader.ListeningChanged += (_, on) => ReadingAloudChanged?.Invoke (this, new ReadingAloudState (on, book.Title));
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

        /// <summary>
        /// Books handed to the app from outside ("Open with", the share sheet): imported, and one book opened at
        /// once, whether new or already in the library. A file that is not a book is reported, not thrown.
        /// </summary>
        public async Task OpenFilesAsync (IReadOnlyList<Majorsilence.Forms.Backends.PickedFile> files)
        {
            await InitializeAsync ();
            if (Services is not { } services || files.Count == 0)
                return;
            Core.Library.LibraryBook? opened = null;
            var problems = new List<string> ();
            foreach (var file in files) {
                try {
                    await using var stream = await file.OpenReadAsync ();
                    opened = (await services.Library.ImportAsync (stream, file.Name)).Book;
                } catch (Exception ex) when (ex is Core.Library.BookImportException or IOException or UnauthorizedAccessException) {
                    problems.Add (ex is Core.Library.BookImportException ? ex.Message : $"Could not read {file.Name}: {ex.Message}");
                }
            }
            if (problems.Count > 0)
                ShowError (new IOException (string.Join (" ", problems)));
            if (files.Count == 1 && opened is not null)
                await OpenBookAsync (opened);
            else if (Library is { } library && Reader is null)
                await library.RefreshAsync ();
        }

        /// <summary>
        /// Asks before opening a book's web link in the system browser: the reader never touches the network itself,
        /// and an address in a book is the book's content, so the reader sees it first.
        /// </summary>
        public async Task OpenExternalLinkAsync (Uri uri)
        {
            if (!SystemBrowser.CanOpen (uri)) {
                await MessageBox.ShowAsync ($"This link can't be opened from Recital:\n{uri}", "Link");
                return;
            }
            if (await MessageBox.ShowAsync ($"Open this link in your browser?\n\n{uri}", "Open link", MessageBoxButtons.OKCancel) != DialogResult.OK)
                return;
            if (!SystemBrowser.Open (uri))
                await MessageBox.ShowAsync ($"Recital couldn't open your browser. The address is:\n{uri}", "Open link");
        }

        /// <summary>Opens the voice settings over the window, showing which cloud keys are saved.</summary>
        public async Task ShowVoiceSettingsAsync ()
        {
            if (Services is not { } services)
                return;
            var saved = new HashSet<string> (StringComparer.Ordinal);
            foreach (var (id, _) in Tts.Platform.TtsEngines.CloudServices)
                if (await services.Platform.Secrets.GetAsync (Tts.Platform.TtsEngines.KeyFor (id)) is { Length: > 0 })
                    saved.Add (id);
            var settings = await Tts.Platform.TtsEngineSettings.LoadAsync (services.DataDirectory);
            tts_settings.SetBounds (Math.Max (0, (ClientSize.Width - 440) / 2), 40, Math.Min (ClientSize.Width, 440), Math.Max (240, ClientSize.Height - 80));
            tts_settings.Open (saved, settings.AzureRegion, settings.PiperProgram, settings.PiperVoices);
            tts_settings.ShowRecitalVoices (OfferRecitalVoices (), null);
        }

        // Offered where the app does not carry them and they are not yet downloaded, if there is a way to play them.
        private bool OfferRecitalVoices ()
            => Services?.DataDirectory is { } data && Tts.Platform.TtsEngines.FindRecitalVoices (data) is null
               && (HostAudioOutput ?? Tts.Platform.TtsEngines.AudioOutputFactory ()) is not null;

        /// <summary>
        /// Asks, saying the size and where from, then downloads Recital's voices into app data and offers them in the
        /// Engine menu (D-033).
        /// </summary>
        public async Task DownloadRecitalVoicesAsync ()
        {
            if (Services is not { DataDirectory: { } data } services)
                return;
            var megabytes = Tts.Platform.TtsEngines.RecitalVoicesSize / 1_000_000;
            if (await MessageBox.ShowAsync (
                    $"Download Recital's voices?\n\nAbout {megabytes} MB, once, from huggingface.co (the Kokoro voice model). Nothing about you or your books is sent, and afterwards they work offline.",
                    "Recital voices", MessageBoxButtons.OKCancel) != DialogResult.OK)
                return;
            var shown = -1L;
            var progress = new Progress<long> (bytes => {
                var percent = bytes * 100 / Math.Max (1, Tts.Platform.TtsEngines.RecitalVoicesSize);
                if (percent != shown)
                    tts_settings.ShowRecitalVoices (false, $"Downloading... {shown = percent}%");
            });
            tts_settings.ShowRecitalVoices (false, "Downloading...");
            try {
                await Tts.Platform.TtsEngines.DownloadRecitalVoicesAsync (data, progress);
            } catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException) {
                tts_settings.ShowRecitalVoices (true, $"The download did not finish: {ex.Message}");
                return;
            }
            tts_settings.ShowRecitalVoices (false, "Recital voices are ready: choose them in the Engine menu while listening.");
            if (!custom_tts_engines)
                tts_engines = [.. HostTtsEngines, .. await Tts.Platform.TtsEngines.AvailableAsync (data, services.Platform.Secrets, output: HostAudioOutput)];
            if (Reader is { } reader)
                await reader.SwitchEngineAsync ();
        }

        /// <summary>Keys into the secure store, the rest into local app data; then the engines are found again.</summary>
        public async Task SaveVoiceSettingsAsync (TtsSettingsInput input)
        {
            if (Services is not { } services)
                return;
            foreach (var (id, key) in input.Keys)
                if (key is not null)
                    await services.Platform.Secrets.SetAsync (Tts.Platform.TtsEngines.KeyFor (id), key.Length == 0 ? null : key);
            if (services.DataDirectory is { } data)
                await new Tts.Platform.TtsEngineSettings (input.PiperProgram, input.PiperVoices, input.AzureRegion).SaveAsync (data);
            tts_settings.Visible = false;
            if (!custom_tts_engines)
                tts_engines = [.. HostTtsEngines, .. await Tts.Platform.TtsEngines.AvailableAsync (services.DataDirectory, services.Platform.Secrets, output: HostAudioOutput)];
            if (Reader is { } reader)
                await reader.SwitchEngineAsync ();
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
                ThemePainter.Apply (Reader.Notes, theme);
            }
        }

        private void ShowError (Exception ex)
        {
            error_banner.Text = ex.Message;
            error_banner.Visible = true;
            error_banner.BringToFront ();
        }
    }

    /// <param name="Listening">True while reading aloud (paused or not).</param>
    public sealed record ReadingAloudState (bool Listening, string Title);
}
