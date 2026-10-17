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
    }
}
