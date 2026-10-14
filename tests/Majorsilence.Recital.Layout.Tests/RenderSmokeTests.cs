using Majorsilence.Recital.Epub;
using SkiaSharp;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    public class RenderSmokeTests
    {
        [Theory]
        [InlineData ("se-austen-pride-and-prejudice.epub", 1)]
        [InlineData ("se-austen-pride-and-prejudice.epub", 12)]
        [InlineData ("pg-11-alice-epub3.epub", 2)]
        [InlineData ("pg-11-alice-epub3.epub", 0)]
        public async Task RendersAChapter (string file, int spineIndex)
        {
            var ct = TestContext.Current.CancellationToken;
            await using var book = await EpubBook.OpenAsync (Path.Combine (AppContext.BaseDirectory, "corpus", file), ct);
            using var engine = new LayoutEngine (book);
            var settings = new LayoutSettings { PageWidth = 420, PageHeight = 640, MarginLeft = 24, MarginRight = 24, MarginTop = 28, MarginBottom = 28 };

            var chapter = await engine.LayoutChapterAsync (spineIndex, settings, ct);

            Assert.NotEmpty (chapter.Pages);
            var directory = Path.Combine (AppContext.BaseDirectory, "renders");
            Directory.CreateDirectory (directory);

            foreach (var page in chapter.Pages.Take (2)) {
                var png = PageRenderer.RenderPng (page, settings, 2, path => Decode (book, path));
                File.WriteAllBytes (Path.Combine (directory, $"{Path.GetFileNameWithoutExtension (file)}.{spineIndex}.{page.Index}.png"), png);
            }
        }

        private static SKImage? Decode (EpubBook book, string path)
        {
            var bytes = book.ReadResourceAsync (path).GetAwaiter ().GetResult ();
            return SKImage.FromEncodedData (bytes);
        }
    }
}
