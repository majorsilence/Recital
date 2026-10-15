using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// Applies a reader theme to a tree of controls. Containers and labels take the theme ambiently from
    /// the form's BackColor/ForeColor (plan 4); the controls below do not, as in WinForms, where a toolbar
    /// paints its own colours and an input's background defaults to the window colour rather than its
    /// parent's. Those get theirs set here.
    /// </summary>
    internal static class ThemePainter
    {
        public static void Apply (Control root, ReaderTheme theme)
        {
            var chromeBack = Colors.FromArgb (theme.ChromeBackground);
            var chromeText = Colors.FromArgb (theme.ChromeText);
            // Inputs and lists sit on the page colour, which reads as "content" against the chrome.
            var inputBack = Colors.FromArgb (theme.PageBackground);
            var inputText = Colors.FromArgb (theme.PageText);

            // A hovered, open or checked toolbar item is drawn in this shared style, light grey by
            // default: unreadable under light text on a dark strip. The app shows one theme at a time.
            var hover = Colors.Mix (chromeBack, chromeText, 0.22f);
            ToolBar.DefaultItemHoverStyle.BackgroundColor = new SkiaSharp.SKColor (hover.R, hover.G, hover.B, hover.A);

            Walk (root);

            void Walk (Control control)
            {
                switch (control) {
                    case ToolStrip strip:
                        strip.BackColor = chromeBack;
                        strip.ForeColor = chromeText;
                        strip.Renderer ??= ItemTextColourRenderer.Instance;
                        foreach (var item in strip.Items.OfType<ToolStripItem> ()) {
                            item.ForeColor = chromeText;
                            if (item is ToolStripControlHost { Control: { } hosted }) {
                                hosted.BackColor = inputBack;
                                hosted.ForeColor = inputText;
                            }
                        }
                        break;
                    case SplitContainer split:
                        split.SplitterColor = Colors.Mix (chromeBack, chromeText, 0.15f);
                        break;
                    case TextBoxBase or ComboBox or NumericUpDown or ListView or TreeView or ListBox:
                        control.BackColor = inputBack;
                        control.ForeColor = inputText;
                        break;
                }

                foreach (Control child in control.Controls)
                    Walk (child);
            }
        }

        /// <summary>The toolbar renderer every themed strip uses, for two framework gaps (below).</summary>
        private sealed class ItemTextColourRenderer : ToolStripProfessionalRenderer
        {
            public static readonly ItemTextColourRenderer Instance = new ();

            protected override void OnRenderItemText (ToolStripItemTextRenderEventArgs e)
            {
                // FRAMEWORK-GAP: GAP-009. The strip renderer ignores ToolStripItem.ForeColor (and the
                // strip's) unless a ToolStripRenderer is installed, so toolbar text stayed near-black on a
                // dark strip. Pass the item's ForeColor through as the text colour.
                if (e.Item is { ForeColor.IsEmpty: false } item && item.Enabled)
                    e.TextColor = item.ForeColor;

                // FRAMEWORK-GAP: GAP-008. A drop-down item is measured with 6px less room for its arrow
                // than it is drawn with, so its text wrapped ("Sort:" over "Recent"). Give the 6px back.
                if (e.Item is ToolStripDropDownItem { HasDropDownItems: true } dropDown && dropDown.Owner is { } owner) {
                    var rect = e.TextRectangle;
                    e.TextRectangle = new System.Drawing.Rectangle (rect.X, rect.Y, rect.Width + owner.LogicalToDeviceUnits (6), rect.Height);
                }

                base.OnRenderItemText (e);
            }
        }
    }
}
