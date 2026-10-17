using System.Drawing;
using Majorsilence.Forms;

namespace Majorsilence.Recital.UI
{
    /// <summary>An in-window box for writing a highlight's note (an in-window panel rather than a dialog, so it works the same on phones).</summary>
    public class NoteEditor : Panel
    {
        private readonly Label quote = new () { Dock = DockStyle.Top, Height = 48, AutoSize = false, AutoEllipsis = true, Padding = new Padding (0, 0, 0, 6) };
        private readonly TextBox text = new () {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = true,
            PlaceholderText = "Write a note…",
        };

        public NoteEditor ()
        {
            Visible = false;
            Padding = new Padding (12);
            BorderStyle = BorderStyle.FixedSingle;

            var buttons = new FlowLayoutPanel {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 40,
                WrapContents = false,
                Padding = new Padding (0, 6, 0, 0),
            };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += (_, _) => Saved?.Invoke (this, NoteText);
            var cancel = new Button { Text = "Cancel", AutoSize = true };
            cancel.Click += (_, _) => Cancelled?.Invoke (this, EventArgs.Empty);
            buttons.Controls.Add (save);
            buttons.Controls.Add (cancel);

            Controls.Add (text);
            Controls.Add (quote);
            Controls.Add (buttons);
        }

        public string NoteText {
            get => text.Text ?? string.Empty;
            set => text.Text = value;
        }

        public TextBox TextBox => text;

        public event EventHandler<string>? Saved;
        public event EventHandler? Cancelled;

        /// <summary>Opens the editor for the passage <paramref name="excerpt"/> with its current note.</summary>
        public void Edit (string excerpt, string? note)
        {
            quote.Text = "“" + excerpt + "”";
            NoteText = note ?? string.Empty;
            Visible = true;
            BringToFront ();
            text.Focus ();
        }

        /// <summary>Centres the editor over <paramref name="area"/>, at most 480 by 300.</summary>
        public void PlaceIn (Rectangle area)
        {
            var width = Math.Min (480, area.Width - 24);
            var height = Math.Min (300, area.Height - 24);
            SetBounds (area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 3, Math.Max (100, width), Math.Max (120, height));
        }
    }
}
