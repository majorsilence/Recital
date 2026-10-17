using System.Globalization;
using Majorsilence.Forms;
using Majorsilence.Recital.Core.Annotations;

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
        private readonly ToolStripButton export = new ("Export…") { ToolTipText = "Save as a Markdown file" };
        private readonly Label empty = new () {
            Dock = DockStyle.Fill,
            Text = "No highlights or bookmarks yet.\nPress and hold on the text, then drag, to highlight it.",
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false,
        };

        public AnnotationsPanel ()
        {
            var copy = new ToolStripButton ("Copy all") { ToolTipText = "Copy everything as Markdown" };
            export.Click += (_, _) => ExportRequested?.Invoke (this, EventArgs.Empty);
            copy.Click += (_, _) => CopyRequested?.Invoke (this, EventArgs.Empty);
            tool_strip.Items.AddRange ([export, copy]);

            var menu = new ContextMenuStrip ();
            var editNote = new ToolStripMenuItem ("Edit note…");
            editNote.Click += (_, _) => {
                if (tree.SelectedNode?.Tag is Annotation { Kind: AnnotationKind.Highlight } a)
                    EditNoteRequested?.Invoke (this, a);
            };
            var delete = new ToolStripMenuItem ("Delete");
            delete.Click += (_, _) => {
                if (tree.SelectedNode?.Tag is Annotation a)
                    DeleteRequested?.Invoke (this, a);
            };
            menu.Items.AddRange ([editNote, delete]);
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
        }

        public TreeView Tree => tree;

        /// <summary>Whether saving to a file is offered (the desktop); Copy all works everywhere.</summary>
        public bool CanExportToFile {
            get => export.Visible;
            set => export.Visible = value;
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
                var title = chapterOf (annotation) ?? "Untitled section";
                if (chapterNode is null || title != chapter) {
                    chapterNode = new TreeNode (title);
                    tree.Nodes.Add (chapterNode);
                    chapter = title;
                }
                chapterNode.Nodes.Add (new TreeNode (Label (annotation)) {
                    Tag = annotation,
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
            if (annotation.Kind == AnnotationKind.Bookmark)
                return $"🔖 {percent} {excerpt}";
            var colour = HighlightColors.All.FirstOrDefault (c => c.Name == annotation.Color).Label ?? "Highlight";
            var label = $"{colour}: “{excerpt}”";
            return annotation.HasNote ? label + " — " + Shorten (annotation.Note!.ReplaceLineEndings (" "), 50) : label;
        }

        private static string Shorten (string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd () + "…";
    }
}
