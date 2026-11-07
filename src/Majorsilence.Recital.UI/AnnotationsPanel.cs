using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.UI.Localization;

namespace Majorsilence.Recital.UI
{
    /// <summary>
    /// The reader's Notes panel: every highlight, note and bookmark in the book, grouped by chapter, with
    /// export to Markdown. Clicking one goes to it.
    /// </summary>
    public class AnnotationsPanel : UserControl
    {
        private readonly TreeView tree = new () { Dock = DockStyle.Fill, ShowLines = false, FullRowSelect = true, HideSelection = false };
        private readonly ToolStrip tool_strip = new () { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
        private readonly ToolStripButton export = new ();
        private readonly ToolStripButton copy = new ();
        private readonly Label empty = new () {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };
        private readonly ToolStripMenuItem edit_note = new ();
        private readonly ToolStripMenuItem delete_item = new ();

        public AnnotationsPanel ()
        {
            export.Click += (_, _) => ExportRequested?.Invoke (this, EventArgs.Empty);
            copy.Click += (_, _) => CopyRequested?.Invoke (this, EventArgs.Empty);
            tool_strip.Items.AddRange ([export, copy]);

            var menu = new ContextMenuStrip ();
            edit_note.Click += (_, _) => {
                if (tree.SelectedNode?.Tag is Annotation { Kind: AnnotationKind.Highlight } a)
                    EditNoteRequested?.Invoke (this, a);
            };
            delete_item.Click += (_, _) => {
                if (tree.SelectedNode?.Tag is Annotation a)
                    DeleteRequested?.Invoke (this, a);
            };
            menu.Items.AddRange ([edit_note, delete_item]);
            tree.ContextMenuStrip = menu;

            tree.NodeMouseClick += (_, e) => {
                if (e.Node?.Tag is Annotation a) {
                    tree.SelectedNode = e.Node;
                    if (e.Button == MouseButtons.Left)
                        OpenRequested?.Invoke (this, a);
                }
            };
            tree.KeyDown += (_, e) => {
                if (tree.SelectedNode?.Tag is not Annotation a)
                    return;
                if (e.KeyCode == Keys.Enter) {
                    e.Handled = true;
                    OpenRequested?.Invoke (this, a);
                } else if (e.KeyCode == Keys.Delete) {
                    e.Handled = true;
                    DeleteRequested?.Invoke (this, a);
                }
            };

            Controls.Add (tree);
            Controls.Add (empty);
            Controls.Add (tool_strip);
            ApplyLanguage ();
        }

        public TreeView Tree => tree;

        /// <summary>Re-reads this panel's own chrome text; <see cref="ReaderView.ApplyLanguage"/> calls it.</summary>
        public void ApplyLanguage ()
        {
            export.Text = Strings.Get ("Export");
            export.ToolTipText = Strings.Get ("ExportTooltip");
            copy.Text = Strings.Get ("CopyAll");
            copy.ToolTipText = Strings.Get ("CopyAllTooltip");
            edit_note.Text = Strings.Get ("EditNote");
            delete_item.Text = Strings.Get ("Delete");
            empty.Text = Strings.Get ("NoHighlightsYet");
        }

        public event EventHandler<Annotation>? OpenRequested;
        public event EventHandler<Annotation>? EditNoteRequested;
        public event EventHandler<Annotation>? DeleteRequested;
        public event EventHandler? ExportRequested;
        public event EventHandler? CopyRequested;

        /// <summary>Shows <paramref name="annotations"/> (in reading order) under their chapter headings.</summary>
        public void Show (IReadOnlyList<Annotation> annotations, Func<Annotation, string?> chapterOf)
        {
            tree.BeginUpdate ();
            tree.Nodes.Clear ();
            TreeNode? chapterNode = null;
            string? chapter = null;
            foreach (var annotation in annotations) {
                var title = chapterOf (annotation) ?? Strings.Get ("UntitledSection");
                if (chapterNode is null || title != chapter) {
                    chapterNode = new TreeNode (title) { ForeColor = tree.ForeColor };
                    tree.Nodes.Add (chapterNode);
                    chapter = title;
                }
                chapterNode.Nodes.Add (new TreeNode (Label (annotation)) {
                    Tag = annotation,
                    ForeColor = tree.ForeColor,
                    ToolTipText = (annotation.HasNote ? annotation.Note : annotation.Text) ?? "",
                });
            }
            tree.ExpandAll ();
            tree.EndUpdate ();
            empty.Visible = annotations.Count == 0;
            tree.Visible = annotations.Count > 0;
        }

        /// <summary>One line for the list: what it is and its words (a note after a dash).</summary>
        public static string Label (Annotation annotation)
        {
            var percent = Math.Round (annotation.Percent * 100).ToString (CultureInfo.CurrentCulture) + "%";
            var excerpt = Shorten (annotation.Text ?? "", 70);
            // Plain text, not an emoji: an emoji comes from whatever colour font the system has, so it looks
            // different (or missing) from one machine to the next.
            if (annotation.Kind == AnnotationKind.Bookmark)
                return string.Format (CultureInfo.CurrentCulture, Strings.Get ("BookmarkLabel"), percent, excerpt);
            var colour = annotation.Color is { } name && HighlightColors.All.Any (c => c.Name == name) ? Strings.Get ("Color" + Capitalize (name)) : Strings.Get ("Highlight");
            var label = $"{colour}: “{excerpt}”";
            return annotation.HasNote ? label + " — " + Shorten (annotation.Note!.ReplaceLineEndings (" "), 50) : label;
        }

        private static string Shorten (string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd () + "…";

        /// <summary>"yellow" to "Yellow": a <see cref="Core.Annotations.HighlightColors"/> name to the
        /// resource key for its translated display name ("ColorYellow").</summary>
        internal static string Capitalize (string name) => name.Length == 0 ? name : char.ToUpperInvariant (name[0]) + name[1..];
    }
}
