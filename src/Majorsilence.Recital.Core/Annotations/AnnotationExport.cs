using System.Globalization;
using System.Text;

namespace Majorsilence.Recital.Core.Annotations
{
    /// <summary>Highlights, notes and bookmarks as a Markdown document.</summary>
    public static class AnnotationExport
    {
        /// <param name="title">The book's title.</param>
        /// <param name="authors">The book's authors, comma-separated, or empty.</param>
        /// <param name="annotations">Live annotations (deleted ones are skipped anyway).</param>
        /// <param name="chapterOf">The chapter heading for a spine index, or null; groups the entries.</param>
        public static string ToMarkdown (string title, string authors, IEnumerable<Annotation> annotations, Func<Annotation, string?>? chapterOf = null)
        {
            var text = new StringBuilder ();
            text.Append ("# ").AppendLine (Escape (title));
            if (!string.IsNullOrWhiteSpace (authors))
                text.AppendLine ().Append ('*').Append (Escape (authors)).AppendLine ("*");

            var live = AnnotationMerge.Live (annotations);
            if (live.Count == 0) {
                text.AppendLine ().AppendLine ("No highlights, notes or bookmarks.");
                return text.ToString ();
            }

            string? currentChapter = null;
            var first = true;
            foreach (var annotation in live) {
                var chapter = chapterOf?.Invoke (annotation);
                if (first || chapter != currentChapter) {
                    text.AppendLine ().Append ("## ").AppendLine (Escape (chapter ?? "Untitled section"));
                    currentChapter = chapter;
                    first = false;
                }

                text.AppendLine ();
                var percent = Math.Round (annotation.Percent * 100).ToString (CultureInfo.InvariantCulture) + "%";
                if (annotation.Kind == AnnotationKind.Bookmark) {
                    text.Append ("- Bookmark (").Append (percent).Append (')');
                    if (!string.IsNullOrWhiteSpace (annotation.Text))
                        text.Append (": ").Append (Escape (Shorten (annotation.Text, 120)));
                    text.AppendLine ();
                    continue;
                }

                foreach (var line in (annotation.Text ?? "").Split ('\n'))
                    text.Append ("> ").AppendLine (Escape (line.Trim ()));
                text.AppendLine ().Append ('*').Append (HighlightColors.All.FirstOrDefault (c => c.Name == annotation.Color).Label ?? "Highlight")
                    .Append (", ").Append (percent).AppendLine ("*");
                if (annotation.HasNote) {
                    text.AppendLine ();
                    foreach (var line in annotation.Note!.Trim ().Split ('\n'))
                        text.AppendLine (line.TrimEnd ());
                }
            }
            return text.ToString ();
        }

        private static string Shorten (string text, int length) => text.Length <= length ? text : text[..(length - 1)].TrimEnd () + "…";

        // Characters that would otherwise start Markdown syntax mid-text.
        private static string Escape (string text)
        {
            var escaped = new StringBuilder (text.Length);
            foreach (var c in text) {
                if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#')
                    escaped.Append ('\\');
                escaped.Append (c);
            }
            return escaped.ToString ();
        }
    }
}
