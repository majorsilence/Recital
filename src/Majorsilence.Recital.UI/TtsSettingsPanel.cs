using Majorsilence.Forms;
using Majorsilence.Recital.UI.Localization;

namespace Majorsilence.Recital.UI
{
    /// <param name="Keys">Per cloud service: a new key, "" to remove the saved one, or null to leave it.</param>
    public sealed record TtsSettingsInput (IReadOnlyDictionary<string, string?> Keys, string? AzureRegion, string? PiperProgram, string? PiperVoices)
    {
        // Never shown in logs or messages.
        public override string ToString () => $"TtsSettingsInput {{ AzureRegion = {AzureRegion}, PiperProgram = {PiperProgram}, PiperVoices = {PiperVoices} }}";
    }

    /// <summary>
    /// Voice settings: API keys for the cloud voices (kept in the secure store) and where Piper is. It says,
    /// before any key is entered, that the text being read is sent to the service chosen (plan 3.2).
    /// </summary>
    public class TtsSettingsPanel : Panel
    {
        private readonly IReadOnlyList<(string Id, string Name)> services;
        private readonly Dictionary<string, TextBox> keys = [];
        private readonly Dictionary<string, Label> key_captions = [];
        private readonly TextBox region = new () { Width = 360 };
        private readonly TextBox piper = new () { Width = 360 };
        private readonly TextBox voices = new () { Width = 360 };
        private readonly FlowLayoutPanel recital = new () { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, Padding = new Padding (0, 0, 0, 8) };
        private readonly Button download = new () { AutoSize = true };
        private readonly Label download_status = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Visible = false };
        private readonly Label heading = new () { AutoSize = true };
        private readonly Label recital_description = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 4, 0, 4) };
        private readonly Label cloud_description = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 4, 0, 8) };
        private readonly Label region_caption = new () { AutoSize = true, Padding = new Padding (0, 6, 0, 0) };
        private readonly Label piper_caption = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 12, 0, 0) };
        private readonly Label voices_caption = new () { AutoSize = true, Padding = new Padding (0, 6, 0, 0) };
        private readonly Button save = new () { AutoSize = true };
        private readonly Button forget = new () { AutoSize = true };
        private readonly Button cancel = new () { AutoSize = true };

        public TtsSettingsPanel (IReadOnlyList<(string Id, string Name)> services)
        {
            this.services = services;
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding (16) };
            flow.Controls.Add (heading);
            // Recital's own voices, where the app does not carry them (phones and tablets): asked for, never assumed.
            recital.Controls.Add (recital_description);
            download.Click += (_, _) => DownloadRequested?.Invoke (this, EventArgs.Empty);
            recital.Controls.Add (download);
            recital.Controls.Add (download_status);
            flow.Controls.Add (recital);
            flow.Controls.Add (cloud_description);
            foreach (var (id, name) in services) {
                var caption = new Label { AutoSize = true, Padding = new Padding (0, 6, 0, 0) };
                key_captions[id] = caption;
                flow.Controls.Add (caption);
                var box = new TextBox { Width = 360, UseSystemPasswordChar = true };
                keys[id] = box;
                flow.Controls.Add (box);
                if (id == "azure") {
                    flow.Controls.Add (region_caption);
                    flow.Controls.Add (region);
                }
            }
            // Piper is a program Recital runs: a phone or tablet cannot run one, so it is not offered there.
            if (!OperatingSystem.IsAndroid () && !OperatingSystem.IsIOS ()) {
                flow.Controls.Add (piper_caption);
                flow.Controls.Add (piper);
                flow.Controls.Add (voices_caption);
                flow.Controls.Add (voices);
            }

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding (0, 12, 0, 0) };
            save.Click += (_, _) => Saved?.Invoke (this, Input (removeAll: false));
            forget.Click += (_, _) => Saved?.Invoke (this, Input (removeAll: true));
            cancel.Click += (_, _) => Visible = false;
            buttons.Controls.Add (save);
            buttons.Controls.Add (forget);
            buttons.Controls.Add (cancel);
            flow.Controls.Add (buttons);
            Controls.Add (flow);
            ApplyLanguage ();
        }

        /// <summary>Re-reads this panel's own chrome text; called wherever <c>MainForm.ApplyTheme</c> runs.</summary>
        public void ApplyLanguage ()
        {
            heading.Text = Strings.Get ("Voices");
            recital_description.Text = Strings.Get ("RecitalVoicesDescription");
            download.Text = Strings.Get ("DownloadRecitalVoicesButton");
            cloud_description.Text = Strings.Get ("CloudVoicesDescription");
            foreach (var (id, name) in services)
                key_captions[id].Text = string.Format (System.Globalization.CultureInfo.CurrentCulture, Strings.Get ("ServiceKey"), name);
            region_caption.Text = Strings.Get ("AzureRegion");
            region.PlaceholderText = "westeurope";
            piper_caption.Text = Strings.Get ("PiperCaption");
            voices_caption.Text = Strings.Get ("PiperVoicesCaption");
            save.Text = Strings.Get ("Save");
            forget.Text = Strings.Get ("RemoveAllKeys");
            cancel.Text = Strings.Get ("Cancel");
        }

        public event EventHandler<TtsSettingsInput>? Saved;

        /// <summary>The user asked to download Recital's voices (the host asks once more, saying the size).</summary>
        public event EventHandler? DownloadRequested;

        public Button DownloadButton => download;
        public string? DownloadStatus => download_status.Visible ? download_status.Text : null;

        /// <param name="offer">Offer the download (the voices are not on this device).</param>
        /// <param name="status">A line about the download in progress, done or failed.</param>
        public void ShowRecitalVoices (bool offer, string? status)
        {
            recital.Visible = offer || status is not null;
            download.Visible = offer;
            download_status.Text = status ?? "";
            download_status.Visible = status is not null;
        }

        public TextBox KeyBox (string service) => keys[service];
        public TextBox AzureRegion => region;
        public TextBox PiperProgram => piper;
        public TextBox PiperVoices => voices;

        /// <summary>Opens with the current settings; key boxes are empty, showing only which services have one saved.</summary>
        public void Open (IReadOnlySet<string> keysSaved, string? azureRegion, string? piperProgram, string? piperVoices)
        {
            foreach (var (id, box) in keys) {
                box.Text = "";
                box.PlaceholderText = keysSaved.Contains (id) ? Strings.Get ("SavedTypeToReplace") : "";
            }
            region.Text = azureRegion ?? "";
            piper.Text = piperProgram ?? "";
            voices.Text = piperVoices ?? "";
            Visible = true;
            BringToFront ();
        }

        private TtsSettingsInput Input (bool removeAll)
            => new (keys.ToDictionary (k => k.Key, k => removeAll ? "" : string.IsNullOrWhiteSpace (k.Value.Text) ? null : k.Value.Text.Trim ()),
                Blank (region.Text), Blank (piper.Text), Blank (voices.Text));

        private static string? Blank (string? text) => string.IsNullOrWhiteSpace (text) ? null : text.Trim ();
    }
}
