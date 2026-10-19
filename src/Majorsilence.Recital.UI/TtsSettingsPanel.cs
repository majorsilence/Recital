using Majorsilence.Forms;

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
        private readonly Dictionary<string, TextBox> keys = [];
        private readonly TextBox region = new () { Width = 360, PlaceholderText = "westeurope" };
        private readonly TextBox piper = new () { Width = 360 };
        private readonly TextBox voices = new () { Width = 360 };
        private readonly FlowLayoutPanel recital = new () { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false, Padding = new Padding (0, 0, 0, 8) };
        private readonly Button download = new () { Text = "Download Recital voices", AutoSize = true };
        private readonly Label download_status = new () { AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Visible = false };

        public TtsSettingsPanel (IReadOnlyList<(string Id, string Name)> services)
        {
            Visible = false;
            BorderStyle = BorderStyle.FixedSingle;
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding (16) };
            flow.Controls.Add (new Label { Text = "Voices", AutoSize = true, Font = new Majorsilence.Forms.Drawing.Font (Font.FontFamily, Font.Size * 1.2f) });
            // Recital's own voices, where the app does not carry them (phones and tablets): asked for, never assumed.
            recital.Controls.Add (new Label {
                Text = "Recital voices: natural voices that run on this device, offline. A one-time download; nothing about you or your books is sent.",
                AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 4, 0, 4),
            });
            download.Click += (_, _) => DownloadRequested?.Invoke (this, EventArgs.Empty);
            recital.Controls.Add (download);
            recital.Controls.Add (download_status);
            flow.Controls.Add (recital);
            flow.Controls.Add (new Label {
                Text = "Cloud voices sound most natural, with your own key from the service. While you listen with one, the text being read is sent to that service; nothing is sent otherwise. Keys stay in this device's secure store.",
                AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 4, 0, 8),
            });
            foreach (var (id, name) in services) {
                flow.Controls.Add (new Label { Text = name + " key", AutoSize = true, Padding = new Padding (0, 6, 0, 0) });
                var box = new TextBox { Width = 360, UseSystemPasswordChar = true };
                keys[id] = box;
                flow.Controls.Add (box);
                if (id == "azure") {
                    flow.Controls.Add (new Label { Text = "Azure region", AutoSize = true, Padding = new Padding (0, 6, 0, 0) });
                    flow.Controls.Add (region);
                }
            }
            flow.Controls.Add (new Label { Text = "Piper (offline voices): the piper program, if not on the PATH", AutoSize = true, MaximumSize = new System.Drawing.Size (380, 0), Padding = new Padding (0, 12, 0, 0) });
            flow.Controls.Add (piper);
            flow.Controls.Add (new Label { Text = "Folder of Piper voices (.onnx with .onnx.json)", AutoSize = true, Padding = new Padding (0, 6, 0, 0) });
            flow.Controls.Add (voices);

            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding (0, 12, 0, 0) };
            var save = new Button { Text = "Save", AutoSize = true };
            var forget = new Button { Text = "Remove all keys", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            save.Click += (_, _) => Saved?.Invoke (this, Input (removeAll: false));
            forget.Click += (_, _) => Saved?.Invoke (this, Input (removeAll: true));
            cancel.Click += (_, _) => Visible = false;
            buttons.Controls.Add (save);
            buttons.Controls.Add (forget);
            buttons.Controls.Add (cancel);
            flow.Controls.Add (buttons);
            Controls.Add (flow);
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
                box.PlaceholderText = keysSaved.Contains (id) ? "(saved; type to replace)" : "";
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
