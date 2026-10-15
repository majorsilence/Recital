namespace Majorsilence.Recital.Storage
{
    /// <summary>
    /// Logical storage paths: '/'-separated, relative, with no empty, "." or ".." segments. Every provider
    /// validates paths with <see cref="Normalize"/>, so a path can never reach outside the provider's root.
    /// </summary>
    public static class StoragePath
    {
        /// <summary>The canonical form of a path; "" is the root.</summary>
        /// <exception cref="ArgumentException">The path has a "." or ".." segment, or a character no provider allows.</exception>
        public static string Normalize (string path)
        {
            ArgumentNullException.ThrowIfNull (path);

            var segments = path.Replace ('\\', '/').Split ('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments) {
                if (segment is "." or "..")
                    throw new ArgumentException ($"'{path}' is not a valid storage path: it has a '{segment}' segment.", nameof (path));
                if (segment.Any (c => c < ' ' || c is ':' or '*' or '?' or '"' or '<' or '>' or '|'))
                    throw new ArgumentException ($"'{path}' is not a valid storage path.", nameof (path));
            }

            return string.Join ('/', segments);
        }

        public static string Combine (params string[] parts) => Normalize (string.Join ('/', parts));

        /// <summary>The folder a path is in; "" for a top-level entry.</summary>
        public static string Parent (string path)
        {
            path = Normalize (path);
            var slash = path.LastIndexOf ('/');
            return slash < 0 ? string.Empty : path[..slash];
        }

        public static string Name (string path)
        {
            var slash = path.LastIndexOf ('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }

        /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> itself or inside it.</summary>
        public static bool IsWithin (string path, string folder)
            => folder.Length == 0 || path == folder || path.StartsWith (folder + "/", StringComparison.Ordinal);
    }
}
