using System.Drawing;
using Majorsilence.Forms;
using Majorsilence.Forms.Drawing;
using Majorsilence.Forms.Drawing.Drawing2D;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// Draws a hovered, pressed or checked toolbar/menu item as a rounded pill instead of the
    /// professional renderer's square fill — the one piece of Recital's chrome drawn by hand rather
    /// than left to the WinForms-compatible default. <see cref="ThemePainter"/> sets
    /// <see cref="HoverColor"/> and <see cref="PressedColor"/> from the active <c>ReaderTheme</c> and
    /// assigns one instance to every <see cref="ToolStrip"/> it walks.
    /// </summary>
    internal sealed class RecitalToolStripRenderer : ToolStripProfessionalRenderer
    {
        /// <summary>Background of a hovered item that is neither checked nor open.</summary>
        public Color HoverColor { get; set; }

        /// <summary>Background of a checked item, or one whose drop-down is open.</summary>
        public Color PressedColor { get; set; }

        protected override void OnRenderButtonBackground (ToolStripItemRenderEventArgs e) => FillPill (e);
        protected override void OnRenderDropDownButtonBackground (ToolStripItemRenderEventArgs e) => FillPill (e);
        protected override void OnRenderMenuItemBackground (ToolStripItemRenderEventArgs e) => FillPill (e, inset: 1);

        private void FillPill (ToolStripItemRenderEventArgs e, int inset = 2)
        {
            e.Handled = true;

            var item = e.Item;
            var color = item.Checked || item.IsDropDownOpened ? PressedColor : item.Hovered ? HoverColor : Color.Empty;
            if (color.IsEmpty || color.A == 0)
                return;

            var bounds = item.Bounds;
            bounds.Inflate (-inset, -inset);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRect (bounds, Math.Min (6f, bounds.Height / 2f));
            using var brush = new SolidBrush (color);
            e.Graphics.FillPath (brush, path);
        }

        private static GraphicsPath RoundedRect (Rectangle rect, float radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath ();
            path.AddArc (rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc (rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc (rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc (rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure ();
            return path;
        }
    }
}
