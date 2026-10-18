using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Recital.Tts;

namespace Majorsilence.Recital.UI
{
    public enum TtsCommand
    {
        PlayPause,
        Back,
        Forward,
        Stop,
        Close,
    }

    /// <summary>
    /// The reading-aloud controls: back a sentence, play or pause, forward a sentence, stop, speed, voice and
    /// the sleep timer.
    /// </summary>
    public class TtsBar : ToolStrip
    {
        public static readonly double[] Speeds = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5];

        private readonly ToolStripButton play = new ("Play");
        private readonly ToolStripDropDownButton speed = new ();
        private readonly ToolStripDropDownButton engine = new ("Engine");
        private readonly ToolStripDropDownButton voice = new ("Voice");
        private readonly ToolStripDropDownButton sleep = new ("Sleep");
        private readonly ToolStripLabel status = new ();
        private double rate = 1.0;

        public TtsBar ()
        {
            Dock = DockStyle.None;
            Visible = false;
            GripStyle = ToolStripGripStyle.Hidden;

            var back = new ToolStripButton ("‹ Back") { ToolTipText = "Back a sentence" };
            var forward = new ToolStripButton ("Next ›") { ToolTipText = "Forward a sentence" };
            var stop = new ToolStripButton ("Stop");
            var close = new ToolStripButton ("✕") { ToolTipText = "Stop and close", AccessibleName = "Close" };
            back.Click += (_, _) => Command?.Invoke (this, TtsCommand.Back);
            play.Click += (_, _) => Command?.Invoke (this, TtsCommand.PlayPause);
            forward.Click += (_, _) => Command?.Invoke (this, TtsCommand.Forward);
            stop.Click += (_, _) => Command?.Invoke (this, TtsCommand.Stop);
            close.Click += (_, _) => Command?.Invoke (this, TtsCommand.Close);

            foreach (var value in Speeds) {
                var item = new ToolStripMenuItem (SpeedText (value)) { Tag = value };
                item.Click += (_, _) => {
                    rate = value;
                    UpdateSpeed ();
                    SpeedChanged?.Invoke (this, value);
                };
                speed.DropDownItems.Add (item);
            }
            foreach (var (text, minutes) in new[] { ("Off", 0), ("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45), ("1 hour", 60), ("End of chapter", -1) }) {
                var item = new ToolStripMenuItem (text) { Tag = minutes };
                item.Click += (_, _) => SleepChosen?.Invoke (this, minutes);
                sleep.DropDownItems.Add (item);
            }
            UpdateSpeed ();
            Items.AddRange ([back, play, forward, stop, new ToolStripSeparator (), engine, voice, speed, sleep, new ToolStripSeparator (), status, close]);
            close.Alignment = ToolStripItemAlignment.Right;
        }

        public event EventHandler<TtsCommand>? Command;
        public event EventHandler<double>? SpeedChanged;

        /// <summary>An engine was chosen, by id.</summary>
        public event EventHandler<string>? EngineChosen;

        /// <summary>"Voice settings…": API keys and Piper.</summary>
        public event EventHandler? SettingsRequested;

        /// <summary>A voice was chosen (null: the engine's default).</summary>
        public event EventHandler<string?>? VoiceChosen;

        /// <summary>Minutes of the sleep timer: 0 off, -1 the end of the chapter.</summary>
        public event EventHandler<int>? SleepChosen;

        public string StatusText {
            get => status.Text ?? string.Empty;
            set => status.Text = value;
        }

        public double Rate {
            get => rate;
            set {
                rate = value;
                UpdateSpeed ();
            }
        }

        /// <summary>Shows play or pause, as the state is.</summary>
        public void ShowState (bool playing, bool paused)
            => play.Text = playing && !paused ? "Pause" : "Play";

        /// <summary>Lists the engines, ticking the one in use, and the way to voice settings.</summary>
        public void ShowEngines (IReadOnlyList<ITtsEngine> engines, string? selected)
        {
            engine.DropDownItems.Clear ();
            foreach (var e in engines) {
                var item = new ToolStripMenuItem (e.Name) { Checked = e.Id == selected, Tag = e.Id };
                item.Click += (_, _) => EngineChosen?.Invoke (this, e.Id);
                engine.DropDownItems.Add (item);
            }
            if (engines.Count > 0)
                engine.DropDownItems.Add (new ToolStripSeparator ());
            var settings = new ToolStripMenuItem ("Voice settings…");
            settings.Click += (_, _) => SettingsRequested?.Invoke (this, EventArgs.Empty);
            engine.DropDownItems.Add (settings);
            engine.Text = engines.FirstOrDefault (e => e.Id == selected)?.Name ?? "Engine";
        }

        /// <summary>Lists the engine's voices, ticking <paramref name="selected"/>.</summary>
        public void ShowVoices (IReadOnlyList<TtsVoice> voices, string? selected)
        {
            voice.DropDownItems.Clear ();
            var standard = new ToolStripMenuItem ("Default") { Checked = selected is null };
            standard.Click += (_, _) => VoiceChosen?.Invoke (this, null);
            voice.DropDownItems.Add (standard);
            foreach (var v in voices.OrderBy (v => v.Language, StringComparer.Ordinal).ThenBy (v => v.Name, StringComparer.CurrentCulture).Take (60)) {
                var item = new ToolStripMenuItem (v.Language is null ? v.Name : $"{v.Name} ({v.Language})") { Checked = v.Id == selected, Tag = v.Id };
                item.Click += (_, _) => VoiceChosen?.Invoke (this, v.Id);
                voice.DropDownItems.Add (item);
            }
            voice.Visible = voices.Count > 0;
        }

        /// <summary>Ticks the sleep choice in force.</summary>
        public void ShowSleep (int minutes)
        {
            foreach (ToolStripMenuItem item in sleep.DropDownItems)
                item.Checked = (int)item.Tag! == minutes;
            sleep.Text = minutes switch { 0 => "Sleep", -1 => "Sleep: chapter end", _ => $"Sleep: {minutes} min" };
        }

        /// <summary>Clicks a command's button (tests, keyboard shortcuts).</summary>
        public void Perform (TtsCommand command) => Command?.Invoke (this, command);

        private void UpdateSpeed ()
        {
            speed.Text = SpeedText (rate);
            foreach (ToolStripMenuItem item in speed.DropDownItems)
                item.Checked = Math.Abs ((double)item.Tag! - rate) < 0.001;
        }

        private static string SpeedText (double value) => value.ToString ("0.##", CultureInfo.CurrentCulture) + "×";
    }
}
