using Majorsilence.Forms;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.UI.Localization;

namespace Majorsilence.Recital.UI
{
    /// <summary>The reader's "Aa" panel: theme and typography. Raises <see cref="PreferencesChanged"/> on every edit.</summary>
    public class TypographyPanel : UserControl
    {
        private static readonly (string Key, string? Value)[] Families = [("BooksOwnFont", null), ("Serif", "serif"), ("SansSerif", "sans-serif"), ("Monospace", "monospace")];
        private static readonly (string Key, float? Value)[] Spacings = [("BooksOwnSpacing", null), ("1.2", 1.2f), ("1.4", 1.4f), ("1.6", 1.6f), ("1.8", 1.8f), ("2.0", 2.0f)];
        private static readonly (string Key, bool? Value)[] Alignments = [("BooksOwnAlignment", null), ("Justified", true), ("Left", false)];
        private static readonly string[] MarginKeys = ["MarginsNarrow", "MarginsNormal", "MarginsWide"];

        private readonly ComboBox theme = NewCombo ();
        private readonly NumericUpDown font_size = new () {
            Minimum = (decimal)ReaderPreferences.MinFontSize,
            Maximum = (decimal)ReaderPreferences.MaxFontSize,
            DecimalPlaces = 0,
            Increment = 1,
            Width = 120,
        };
        private readonly ComboBox family = NewCombo ();
        private readonly ComboBox spacing = NewCombo ();
        private readonly ComboBox alignment = NewCombo ();
        private readonly ComboBox margins = NewCombo ();
        private readonly CheckBox publisher_fonts = new () { AutoSize = true };
        private readonly Label theme_caption = Caption ();
        private readonly Label size_caption = Caption ();
        private readonly Label font_caption = Caption ();
        private readonly Label spacing_caption = Caption ();
        private readonly Label alignment_caption = Caption ();
        private readonly Label margins_caption = Caption ();
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

            flow.Controls.Add (theme_caption);
            flow.Controls.Add (theme);
            flow.Controls.Add (size_caption);
            flow.Controls.Add (font_size);
            flow.Controls.Add (font_caption);
            flow.Controls.Add (family);
            flow.Controls.Add (spacing_caption);
            flow.Controls.Add (spacing);
            flow.Controls.Add (alignment_caption);
            flow.Controls.Add (alignment);
            flow.Controls.Add (margins_caption);
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

            ApplyLanguage ();
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

        /// <summary>Re-reads every caption and combo item from <see cref="Strings"/>, keeping each combo's
        /// selection. <see cref="ReaderView.ApplyLanguage"/> calls it.</summary>
        public void ApplyLanguage ()
        {
            theme_caption.Text = Strings.Get ("Theme");
            size_caption.Text = Strings.Get ("TextSize");
            font_caption.Text = Strings.Get ("Font");
            spacing_caption.Text = Strings.Get ("LineSpacing");
            alignment_caption.Text = Strings.Get ("Alignment");
            margins_caption.Text = Strings.Get ("Margins");
            publisher_fonts.Text = Strings.Get ("UseBooksFonts");

            // Refilling a combo's Items can transiently change SelectedIndex (e.g. to -1 mid-Clear), which
            // would otherwise reach Edit() through SelectedIndexChanged — the same re-entrancy guard the
            // Preferences setter uses.
            updating = true;
            try {
                Refill (theme, ReaderTheme.All.Select (t => Strings.ThemeName (t.Kind)));
                Refill (family, Families.Select (f => Strings.Get (f.Key)));
                Refill (spacing, Spacings.Select (s => s.Value is null ? Strings.Get (s.Key) : s.Key));
                Refill (alignment, Alignments.Select (a => Strings.Get (a.Key)));
                Refill (margins, MarginKeys.Select (Strings.Get));
            } finally {
                updating = false;
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

        private static Label Caption () => new () {
            AutoSize = true,
            Margin = new Padding (0, 10, 0, 2),
        };

        private static ComboBox NewCombo () => new () { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };

        /// <summary>Replaces a combo's items with <paramref name="labels"/>, keeping whichever index was
        /// selected (each list is always the same length, just relabelled).</summary>
        private static void Refill (ComboBox combo, IEnumerable<string> labels)
        {
            var selected = combo.SelectedIndex;
            combo.Items.Clear ();
            foreach (var label in labels)
                combo.Items.Add (label);
            combo.SelectedIndex = Math.Max (0, Math.Min (selected, combo.Items.Count - 1));
        }
    }
}
