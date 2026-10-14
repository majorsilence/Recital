namespace Majorsilence.Recital.Epub
{
    /// <summary>
    /// Archive paths: '/'-separated, relative to the ZIP root, no leading slash, percent-decoded.
    /// </summary>
    internal static class EpubPaths
    {
        /// <summary>The directory part of an archive path ("OEBPS/text/ch1.xhtml" → "OEBPS/text").</summary>
        public static string Directory (string path)
        {
            var slash = path.LastIndexOf ('/');
            return slash < 0 ? string.Empty : path[..slash];
        }

        /// <summary>
        /// Splits an href into its archive path (resolved against <paramref name="baseDirectory"/>) and
        /// fragment. The path is null for a remote URL; an href that is only "#fragment" resolves to
        /// <paramref name="selfPath"/>.
        /// </summary>
        public static (string? Path, string? Fragment) Resolve (string baseDirectory, string href, string? selfPath = null)
        {
            href = href.Trim ();

            string? fragment = null;
            var hash = href.IndexOf ('#');

            if (hash >= 0) {
                fragment = Unescape (href[(hash + 1)..]);
                href = href[..hash];
                if (fragment.Length == 0)
                    fragment = null;
            }

            // Drop any query; it has no meaning inside a ZIP.
            var query = href.IndexOf ('?');
            if (query >= 0)
                href = href[..query];

            if (href.Length == 0)
                return (selfPath, fragment);

            if (IsRemote (href))
                return (null, fragment);

            var combined = href.StartsWith ('/')
                ? href.TrimStart ('/')
                : baseDirectory.Length == 0 ? href : baseDirectory + "/" + href;

            return (Normalize (Unescape (combined)), fragment);
        }

        public static bool IsRemote (string href)
        {
            var colon = href.IndexOf (':');

            // A scheme is at least two characters, so "C:/x" style drive letters are not mistaken for one.
            if (colon < 2)
                return false;

            for (var i = 0; i < colon; i++) {
                var c = href[i];
                if (!(char.IsAsciiLetterOrDigit (c) || c is '+' or '-' or '.'))
                    return false;
            }

            return true;
        }

        /// <summary>Collapses "." and ".." segments and duplicate slashes.</summary>
        public static string Normalize (string path)
        {
            var parts = new List<string> ();

            foreach (var segment in path.Replace ('\\', '/').Split ('/')) {
                if (segment.Length == 0 || segment == ".")
                    continue;

                if (segment == "..") {
                    if (parts.Count > 0)
                        parts.RemoveAt (parts.Count - 1);
                    continue;
                }

                parts.Add (segment);
            }

            return string.Join ('/', parts);
        }

        private static string Unescape (string value)
        {
            try {
                return Uri.UnescapeDataString (value);
            } catch (UriFormatException) {
                return value;
            }
        }
    }
}
