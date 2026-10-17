using Majorsilence.Forms;
using Majorsilence.Recital.Core.Annotations;

namespace Majorsilence.Recital.UI
{
    public enum HighlightAction
    {
        Color,
        Note,
        Copy,
        Delete,
        Dismiss,
    }

    public sealed class HighlightActionEventArgs (HighlightAction action, string? color = null) : EventArgs
    {
        public HighlightAction Action { get; } = action;

        /// <summary>The colour picked, for <see cref="HighlightAction.Color"/>.</summary>
        public string? Color { get; } = color;
    }

    /// <summary>
    /// The small toolbar shown beside a selection or a tapped highlight: a button per highlight colour,
    /// Note, Copy, Delete (for an existing highlight) and close.
    /// </summary>
    public class HighlightBar : ToolStrip
    {
        private readonly ToolStripButton delete = new ("Delete");

        public HighlightBar ()
        {
            Dock = DockStyle.None;
            Visible = false;
            GripStyle = ToolStripGripStyle.Hidden;

            foreach (var (name, label, argb) in HighlightColors.All) {
                var button = new ToolStripButton ("●") {
                    Name = "color-" + name,
                    ToolTipText = label,
                    AccessibleName = label + " highlight",
                    // Opaque, so the swatch reads on any theme.
                    ForeColor = Colors.FromArgb (argb | 0xFF000000),
                    Tag = name,
                };
                button.Click += (_, _) => Raise (HighlightAction.Color, name);
                Items.Add (button);
            }

            var note = new ToolStripButton ("Note…");
            note.Click += (_, _) => Raise (HighlightAction.Note);
            var copy = new ToolStripButton ("Copy");
            copy.Click += (_, _) => Raise (HighlightAction.Copy);
            delete.Click += (_, _) => Raise (HighlightAction.Delete);
            var close = new ToolStripButton ("✕") { ToolTipText = "Close", AccessibleName = "Close" };
            close.Click += (_, _) => Raise (HighlightAction.Dismiss);
            Items.AddRange ([new ToolStripSeparator (), note, copy, delete, close]);
        }

        /// <summary>The size of the visible buttons (a strip's own preferred width stretches to its parent).</summary>
        public System.Drawing.Size ContentSize {
            get {
                var width = Padding.Horizontal + 6;
                var height = 0;
                foreach (var item in Items.Cast<ToolStripItem> ()) {
                    if (item == delete && Target is null)
                        continue;
                    var size = item.GetPreferredSize (System.Drawing.Size.Empty);
                    width += size.Width + item.Margin.Horizontal;
                    height = Math.Max (height, size.Height + item.Margin.Vertical);
                }
                return new System.Drawing.Size (width, Math.Max (PreferredSize.Height, height + Padding.Vertical));
            }
        }

        /// <summary>The highlight being changed, or null for a new selection.</summary>
        public Annotation? Target { get; private set; }

        public event EventHandler<HighlightActionEventArgs>? ActionChosen;

        /// <summary>Shows the bar for a new selection (<paramref name="target"/> null) or an existing highlight.</summary>
        public void ShowFor (Annotation? target)
        {
            Target = target;
            delete.Visible = target is not null;
            foreach (var item in Items.OfType<ToolStripButton> ())
                if (item.Tag is string color)
                    item.Checked = target?.Color == color;
            Visible = true;
            BringToFront ();
        }

        public void HideBar ()
        {
            Target = null;
            Visible = false;
        }

        /// <summary>Clicks the button for <paramref name="action"/> (tests, keyboard shortcuts).</summary>
        public void Choose (HighlightAction action, string? color = null) => Raise (action, color);

        private void Raise (HighlightAction action, string? color = null)
            => ActionChosen?.Invoke (this, new HighlightActionEventArgs (action, color));
    }
}
