using System.IO.Compression;

namespace Majorsilence.Recital.Epub
{
    /// <summary>
    /// Read access to the book's ZIP. Entry lookup is exact first, then case-insensitive (with a warning),
    /// because books made on case-insensitive file systems often get the case of an href wrong.
    /// </summary>
    internal sealed class EpubArchive : IAsyncDisposable
    {
        private readonly ZipArchive zip;
        private readonly Dictionary<string, ZipArchiveEntry> exact = new (StringComparer.Ordinal);
        private readonly Dictionary<string, ZipArchiveEntry> folded = new (StringComparer.OrdinalIgnoreCase);

        // ZipArchive is not safe for concurrent reads.
        private readonly SemaphoreSlim gate = new (1, 1);

        private EpubArchive (ZipArchive zip)
        {
            this.zip = zip;

            foreach (var entry in zip.Entries) {
                // Directory entries have an empty Name.
                if (entry.Name.Length == 0)
                    continue;

                var path = EpubPaths.Normalize (entry.FullName);
                exact.TryAdd (path, entry);
                folded.TryAdd (path, entry);
            }
        }

        public IEnumerable<string> Paths => exact.Keys;

        public static async Task<EpubArchive> OpenAsync (Stream stream, bool leaveOpen, CancellationToken ct)
        {
            // Reading a ZIP needs to seek to the central directory at the end.
            if (!stream.CanSeek) {
                var buffer = new MemoryStream ();
                await stream.CopyToAsync (buffer, ct).ConfigureAwait (false);
                if (!leaveOpen)
                    await stream.DisposeAsync ().ConfigureAwait (false);
                buffer.Position = 0;
                stream = buffer;
                leaveOpen = false;
            }

            try {
                var zip = await ZipArchive.CreateAsync (stream, ZipArchiveMode.Read, leaveOpen, entryNameEncoding: null, ct).ConfigureAwait (false);
                return new EpubArchive (zip);
            } catch (InvalidDataException ex) {
                throw new EpubException ("Not an EPUB: the file is not a ZIP archive.", ex);
            }
        }

        /// <summary>The entry's actual path, or null when there is none even ignoring case.</summary>
        public string? Find (string path, List<EpubWarning>? warnings = null)
        {
            if (exact.ContainsKey (path))
                return path;

            if (folded.TryGetValue (path, out var entry)) {
                var actual = EpubPaths.Normalize (entry.FullName);
                warnings?.Add (new EpubWarning ("path-case", $"'{path}' only matches '{actual}' ignoring case."));
                return actual;
            }

            return null;
        }

        public bool Exists (string path) => Find (path) is not null;

        public async Task<byte[]> ReadAsync (string path, CancellationToken ct)
        {
            var actual = Find (path) ?? throw new FileNotFoundException ($"The book has no file '{path}'.", path);
            var entry = exact[actual];

            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                await using var source = await entry.OpenAsync (ct).ConfigureAwait (false);
                var buffer = new MemoryStream (entry.Length is > 0 and < int.MaxValue ? (int)entry.Length : 0);
                await source.CopyToAsync (buffer, ct).ConfigureAwait (false);
                return buffer.ToArray ();
            } catch (InvalidDataException ex) {
                throw new EpubException ($"'{path}' is corrupt in the archive.", ex);
            } finally {
                gate.Release ();
            }
        }

        public async ValueTask DisposeAsync ()
        {
            await zip.DisposeAsync ().ConfigureAwait (false);
            gate.Dispose ();
        }
    }
}
