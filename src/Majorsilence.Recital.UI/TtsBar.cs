using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Recital.Tts;
using Majorsilence.Recital.UI.Localization;

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
        private static readonly (string Key, int Minutes)[] SleepChoices = [
            ("SleepOff", 0), ("Sleep15Minutes", 15), ("Sleep30Minutes", 30),
            ("Sleep45Minutes", 45), ("Sleep1Hour", 60), ("SleepEndOfChapter", -1),
        ];

        private readonly ToolStripButton back = new () { Tag = ToolbarIcons.Kind.SkipBack };
        private readonly ToolStripButton play = new PlayPauseButton ();
        private readonly ToolStripButton forward = new () { Tag = ToolbarIcons.Kind.SkipForward };
        private readonly ToolStripButton stop = new () { Tag = ToolbarIcons.Kind.Stop };
        private readonly ToolStripButton close = new ("✕") { Tag = ToolbarIcons.Kind.Close };
        private readonly ToolStripDropDownButton speed = new ();
        private readonly ToolStripDropDownButton engine = new ();
        private readonly ToolStripDropDownButton voice = new ();
        private readonly ToolStripDropDownButton sleep = new ();
        private readonly ToolStripLabel status = new ();
        private double rate = 1.0;
        private bool playing, paused;
        private IReadOnlyList<ITtsEngine> last_engines = [];
        private string? last_selected_engine;
        private IReadOnlyList<TtsVoice> last_voices = [];
        private string? last_selected_voice;
        private int last_sleep_minutes;

        public TtsBar ()
        {
            Dock = DockStyle.None;
            Visible = false;
            GripStyle = ToolStripGripStyle.Hidden;
            // The bar sits at the bottom of the reader: its menus open upwards, or on a phone they would open
            // off the screen.
            DefaultDropDownDirection = ToolStripDropDownDirection.AboveRight;

            play.Tag = ToolbarIcons.Kind.Play;
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
            foreach (var (_, minutes) in SleepChoices) {
                var item = new ToolStripMenuItem { Tag = minutes };
                item.Click += (_, _) => SleepChosen?.Invoke (this, minutes);
                sleep.DropDownItems.Add (item);
            }
            UpdateSpeed ();
            Items.AddRange ([back, play, forward, stop, new ToolStripSeparator (), engine, voice, speed, sleep, new ToolStripSeparator (), status, close]);
            close.Alignment = ToolStripItemAlignment.Right;
            ApplyLanguage ();
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
        {
            this.playing = playing;
            this.paused = paused;
            var kind = playing && !paused ? ToolbarIcons.Kind.Pause : ToolbarIcons.Kind.Play;
            play.Text = Strings.Get (kind == ToolbarIcons.Kind.Pause ? "Pause" : "Play");
            play.Tag = kind;
            // ThemePainter redraws every tagged icon in the chrome colour whenever the theme changes;
            // this keeps the icon in step with the state in between, from whichever colour it last drew.
            play.Image = ToolbarIcons.Draw (kind, play.ForeColor.IsEmpty ? System.Drawing.Color.Black : play.ForeColor);
        }

        /// <summary>Lists the engines, ticking the one in use, and the way to voice settings.</summary>
        public void ShowEngines (IReadOnlyList<ITtsEngine> engines, string? selected)
        {
            last_engines = engines;
            last_selected_engine = selected;
            engine.DropDownItems.Clear ();
            foreach (var e in engines) {
                var item = new ToolStripMenuItem (e.Name) { Checked = e.Id == selected, Tag = e.Id };
                item.Click += (_, _) => EngineChosen?.Invoke (this, e.Id);
                engine.DropDownItems.Add (item);
            }
            if (engines.Count > 0)
                engine.DropDownItems.Add (new ToolStripSeparator ());
            var settings = new ToolStripMenuItem (Strings.Get ("VoiceSettingsEllipsis"));
            settings.Click += (_, _) => SettingsRequested?.Invoke (this, EventArgs.Empty);
            engine.DropDownItems.Add (settings);
            engine.Text = engines.FirstOrDefault (e => e.Id == selected)?.Name ?? Strings.Get ("Engine");
        }

        /// <summary>Lists the engine's voices, ticking <paramref name="selected"/>.</summary>
        public void ShowVoices (IReadOnlyList<TtsVoice> voices, string? selected)
        {
            last_voices = voices;
            last_selected_voice = selected;
            voice.DropDownItems.Clear ();
            var standard = new ToolStripMenuItem (Strings.Get ("Default")) { Checked = selected is null };
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
            last_sleep_minutes = minutes;
            foreach (ToolStripMenuItem item in sleep.DropDownItems)
                item.Checked = (int)item.Tag! == minutes;
            sleep.Text = minutes switch {
                0 => Strings.Get ("Sleep"),
                -1 => Strings.Get ("SleepEndOfChapterStatus"),
                _ => string.Format (CultureInfo.CurrentCulture, Strings.Get ("SleepMinutesStatus"), minutes),
            };
        }

        /// <summary>Clicks a command's button (tests, keyboard shortcuts).</summary>
        public void Perform (TtsCommand command) => Command?.Invoke (this, command);

        /// <summary>Re-reads this bar's own chrome text; <see cref="ReaderView.ApplyLanguage"/> calls it.</summary>
        public void ApplyLanguage ()
        {
            back.Text = Strings.Get ("Back");
            back.ToolTipText = Strings.Get ("BackASentence");
            forward.Text = Strings.Get ("Next");
            forward.ToolTipText = Strings.Get ("ForwardASentence");
            stop.Text = Strings.Get ("Stop");
            close.ToolTipText = Strings.Get ("StopAndClose");
            close.AccessibleName = Strings.Get ("Close");
            voice.Text = Strings.Get ("Voice");
            ShowState (playing, paused);
            UpdateSpeed ();
            foreach (ToolStripMenuItem item in sleep.DropDownItems)
                item.Text = Strings.Get (SleepChoices.First (c => c.Minutes == (int)item.Tag!).Key);
            // The engine/voice drop-downs are rebuilt from the data they last showed, so their own items
            // (engine and voice names) are kept and only the surrounding chrome text changes.
            ShowEngines (last_engines, last_selected_engine);
            ShowVoices (last_voices, last_selected_voice);
            ShowSleep (last_sleep_minutes);
        }

        private void UpdateSpeed ()
        {
            speed.Text = SpeedText (rate);
            foreach (ToolStripMenuItem item in speed.DropDownItems)
                item.Checked = Math.Abs ((double)item.Tag! - rate) < 0.001;
        }

        private static string SpeedText (double value) => value.ToString ("0.##", CultureInfo.CurrentCulture) + "×";

        /// <summary>As wide as "Pause" (translated) whichever it says, so the buttons after it never move
        /// under a finger.</summary>
        private sealed class PlayPauseButton () : ToolStripButton ("Play")
        {
            public override System.Drawing.Size GetPreferredSize (System.Drawing.Size proposedSize)
            {
                var size = base.GetPreferredSize (proposedSize);
                if (Owner is not { } strip)
                    return size;
                var extra = TextRenderer.MeasureText (Strings.Get ("Pause"), strip).Width - TextRenderer.MeasureText (Text, strip).Width;
                return extra > 0 ? size with { Width = size.Width + extra } : size;
            }
        }
    }
}
