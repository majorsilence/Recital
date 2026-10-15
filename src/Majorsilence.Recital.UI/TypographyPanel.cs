using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;

namespace Majorsilence.Recital.UI
{
    /// <summary>The reader's "Aa" panel: theme and typography. Raises <see cref="PreferencesChanged"/> on every edit.</summary>
    public class TypographyPanel : UserControl
    {
        private static readonly (string Label, string? Value)[] Families = [("Book's own", null), ("Serif", "serif"), ("Sans-serif", "sans-serif"), ("Monospace", "monospace")];
        private static readonly (string Label, float? Value)[] Spacings = [("Book's own", null), ("1.2", 1.2f), ("1.4", 1.4f), ("1.6", 1.6f), ("1.8", 1.8f), ("2.0", 2.0f)];
        private static readonly (string Label, bool? Value)[] Alignments = [("Book's own", null), ("Justified", true), ("Left", false)];

        private readonly ComboBox theme = NewCombo (ReaderTheme.All.Select (t => t.Name));
        private readonly NumericUpDown font_size = new () {
            Minimum = (decimal)ReaderPreferences.MinFontSize,
            Maximum = (decimal)ReaderPreferences.MaxFontSize,
            DecimalPlaces = 0,
            Increment = 1,
            Width = 120,
        };
        private readonly ComboBox family = NewCombo (Families.Select (f => f.Label));
        private readonly ComboBox spacing = NewCombo (Spacings.Select (s => s.Label));
        private readonly ComboBox alignment = NewCombo (Alignments.Select (a => a.Label));
        private readonly ComboBox margins = NewCombo (Enum.GetNames<MarginSize> ());
        private readonly CheckBox publisher_fonts = new () { Text = "Use the book's fonts", AutoSize = true };
        private ReaderPreferences preferences = new ();
        private bool updating;

        public TypographyPanel ()
        {
            AutoScroll = true;

            var flow = new FlowLayoutPanel {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding (12),
            };

            flow.Controls.Add (Caption ("Theme"));
            flow.Controls.Add (theme);
            flow.Controls.Add (Caption ("Text size"));
            flow.Controls.Add (font_size);
            flow.Controls.Add (Caption ("Font"));
            flow.Controls.Add (family);
            flow.Controls.Add (Caption ("Line spacing"));
            flow.Controls.Add (spacing);
            flow.Controls.Add (Caption ("Alignment"));
            flow.Controls.Add (alignment);
            flow.Controls.Add (Caption ("Margins"));
            flow.Controls.Add (margins);
            flow.Controls.Add (publisher_fonts);
            Controls.Add (flow);

            theme.SelectedIndexChanged += (_, _) => Edit (p => p with { Theme = ReaderTheme.All[theme.SelectedIndex].Kind });
            font_size.ValueChanged += (_, _) => Edit (p => p with { FontSize = (float)font_size.Value });
            family.SelectedIndexChanged += (_, _) => Edit (p => p with { FontFamily = Families[family.SelectedIndex].Value });
            spacing.SelectedIndexChanged += (_, _) => Edit (p => p with { LineHeight = Spacings[spacing.SelectedIndex].Value });
            alignment.SelectedIndexChanged += (_, _) => Edit (p => p with { Justify = Alignments[alignment.SelectedIndex].Value });
            margins.SelectedIndexChanged += (_, _) => Edit (p => p with { Margins = (MarginSize)margins.SelectedIndex });
            publisher_fonts.CheckedChanged += (_, _) => Edit (p => p with { PublisherFonts = publisher_fonts.Checked });
        }

        public event EventHandler<ReaderPreferences>? PreferencesChanged;

        public ReaderPreferences Preferences {
            get => preferences;
            set {
                preferences = value;
                updating = true;
                try {
                    theme.SelectedIndex = Index (ReaderTheme.All.Select (t => t.Kind), value.Theme);
                    font_size.Value = (decimal)Math.Clamp (value.FontSize, ReaderPreferences.MinFontSize, ReaderPreferences.MaxFontSize);
                    family.SelectedIndex = Index (Families.Select (f => f.Value), value.FontFamily);
                    spacing.SelectedIndex = Index (Spacings.Select (s => s.Value), value.LineHeight);
                    alignment.SelectedIndex = Index (Alignments.Select (a => a.Value), value.Justify);
                    margins.SelectedIndex = (int)value.Margins;
                    publisher_fonts.Checked = value.PublisherFonts;
                } finally {
                    updating = false;
                }
            }
        }

        private void Edit (Func<ReaderPreferences, ReaderPreferences> change)
        {
            if (updating)
                return;
            var next = change (preferences);
            if (next == preferences)
                return;
            preferences = next;
            PreferencesChanged?.Invoke (this, next);
        }

        private static int Index<T> (IEnumerable<T> values, T value) => Math.Max (0, values.ToList ().IndexOf (value));

        private static Label Caption (string text) => new () {
            Text = text,
            AutoSize = true,
            Margin = new Padding (0, 10, 0, 2),
        };

        private static ComboBox NewCombo (IEnumerable<string> items)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            foreach (var item in items)
                combo.Items.Add (item);
            return combo;
        }

    }
}
