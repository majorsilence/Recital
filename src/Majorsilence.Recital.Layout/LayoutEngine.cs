using System.Collections.Concurrent;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout.Boxes;
using Majorsilence.Recital.Layout.Css;
using Majorsilence.Recital.Layout.Fonts;
using SkiaSharp;

namespace Majorsilence.Recital.Layout
{
    /// <summary>
    /// Lays out a book's chapters. Styled box trees are cached per (chapter, typography, page size), so
    /// re-paginating at another size reuses the parsing and shaping work; CPU work runs off the caller's thread.
    /// </summary>
    public sealed class LayoutEngine : IDisposable
    {
        private readonly EpubBook book;
        private readonly FontLibrary bundled;
        private readonly FontLibrary book_fonts;
        private readonly SemaphoreSlim book_fonts_gate = new (1, 1);
        private readonly HashSet<string> loaded_font_paths = new (StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, Task<ContentDocument>> documents = new ();
        private readonly ConcurrentDictionary<StyleKey, Task<BlockBox>> trees = new ();
        private readonly ConcurrentDictionary<string, (float Width, float Height)> image_sizes = new (StringComparer.Ordinal);
        private readonly StyleSheetCache sheet_cache = new ();
        private readonly ConcurrentDictionary<string, Task<byte[]?>> css_files = new (StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Sheets, StyleKey Settings), StyleResolver> resolvers = new ();

        public LayoutEngine (EpubBook book, FontLibrary? fonts = null)
        {
            this.book = book;
            bundled = fonts ?? FontLibrary.Bundled;
            book_fonts = bundled.CreateChild ();
        }

        public EpubBook Book => book;

        public Task<ContentDocument> GetDocumentAsync (int spineIndex, CancellationToken ct = default)
            => documents.GetOrAdd (spineIndex, index => book.LoadContentDocumentAsync (index, ct));

        public async Task<ChapterLayout> LayoutChapterAsync (int spineIndex, LayoutSettings settings, CancellationToken ct = default)
        {
            var document = await GetDocumentAsync (spineIndex, ct).ConfigureAwait (false);
            var tree = await GetBoxTreeAsync (spineIndex, settings, ct).ConfigureAwait (false);
            var fonts = settings.PublisherFonts ? book_fonts : bundled;

            return await Task.Run (() => {
                // A tree's shaped paragraphs are filled in lazily; one pagination at a time per tree.
                lock (tree) {
                    var flow = new FlowBuilder (fonts, settings).Build (tree);
                    var pages = Paginator.Paginate (flow, settings.ContentHeight);
                    return new ChapterLayout (document, settings, pages);
                }
            }, ct).ConfigureAwait (false);
        }

        /// <summary>The styled box tree of a chapter, for inspection and snapshot tests.</summary>
        public Task<BlockBox> GetBoxTreeAsync (int spineIndex, LayoutSettings settings, CancellationToken ct = default)
            => trees.GetOrAdd (new StyleKey (spineIndex, settings.TypographyKey, settings.PageWidth, settings.PageHeight),
                _ => BuildTreeAsync (spineIndex, settings, ct));

        private async Task<BlockBox> BuildTreeAsync (int spineIndex, LayoutSettings settings, CancellationToken ct)
        {
            var document = await GetDocumentAsync (spineIndex, ct).ConfigureAwait (false);
            var path = document.SpineItem.Content.Path ?? string.Empty;

            var sheets = await StyleResolver.CollectAsync (document, ReadCssAsync, sheet_cache).ConfigureAwait (false);

            // Chapters usually link the same sheets; they then share one resolver (it holds no per-chapter state).
            var sheetsKey = string.Join ('\u0001', sheets.Select (sheet => sheet.Path + '\u0002' + sheet.Text.Length + '\u0002' + sheet.Text.GetHashCode (StringComparison.Ordinal)));
            var styleKey = new StyleKey (-1, settings.TypographyKey, settings.PageWidth, settings.PageHeight);
            var resolver = resolvers.GetOrAdd ((sheetsKey, styleKey), _ => new StyleResolver (sheets, settings, sheet_cache));

            if (settings.PublisherFonts)
                await LoadBookFontsAsync (resolver.FontFaces, ct).ConfigureAwait (false);

            foreach (var imagePath in BoxTreeBuilder.ImagePaths (document.Document, path).Distinct ())
                if (!image_sizes.ContainsKey (imagePath))
                    image_sizes[imagePath] = await ReadImageSizeAsync (imagePath, ct).ConfigureAwait (false);

            var sizes = new Dictionary<string, (float, float)> (image_sizes);
            return await Task.Run (() => new BoxTreeBuilder (resolver, sizes, path).Build (document.Document), ct).ConfigureAwait (false);
        }

        private Task<byte[]?> ReadCssAsync (string path) => css_files.GetOrAdd (path, ReadOrNullAsync);

        private async Task<byte[]?> ReadOrNullAsync (string path)
        {
            try {
                return await book.ReadResourceAsync (path).ConfigureAwait (false);
            } catch (Exception ex) when (ex is FileNotFoundException or EpubException) {
                return null;
            }
        }

        private async Task LoadBookFontsAsync (IReadOnlyList<FontFaceSource> faces, CancellationToken ct)
        {
            await book_fonts_gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                foreach (var face in faces) {
                    if (!loaded_font_paths.Add (face.Path + "|" + face.Family + "|" + face.Weight + "|" + face.Italic))
                        continue;
                    if (await ReadOrNullAsync (face.Path).ConfigureAwait (false) is { } bytes
                        && FontFace.Load (face.Family, face.Weight, face.Italic, bytes) is { } loaded)
                        book_fonts.Add (loaded);
                }
            } finally {
                book_fonts_gate.Release ();
            }
        }

        private async Task<(float, float)> ReadImageSizeAsync (string path, CancellationToken ct)
        {
            if (await ReadOrNullAsync (path).ConfigureAwait (false) is not { } bytes)
                return (0, 0);

            ct.ThrowIfCancellationRequested ();
            // Only the header is decoded.
            using var codec = SKCodec.Create (new SKMemoryStream (bytes));
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }

        /// <summary>Releases the engine's own resources; the book and its fonts stay open.</summary>
        public void Dispose () => book_fonts_gate.Dispose ();

        private readonly record struct StyleKey (int SpineIndex, (float, float?, string?, bool?, bool, bool) Typography, float PageWidth, float PageHeight);
    }
}
