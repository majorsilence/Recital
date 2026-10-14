using System.Text;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Layout.Boxes;
using SkiaSharp;
using Xunit;

namespace Majorsilence.Recital.Layout.Tests
{
    /// <summary>Lays out hand-written chapters and corpus books for tests.</summary>
    internal static class LayoutKit
    {
        public static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>A small page with round numbers: 300 x 200 content box, 18px base font.</summary>
        public static readonly LayoutSettings Small = new () {
            PageWidth = 340, PageHeight = 240,
            MarginLeft = 20, MarginRight = 20, MarginTop = 20, MarginBottom = 20,
            FontSize = 18,
        };

        /// <summary>One chapter made of <paramref name="body"/> and an inline stylesheet, laid out.</summary>
        public static async Task<ChapterLayout> Layout (string body, string css = "", LayoutSettings? settings = null, TestEpub? epub = null)
        {
            var (book, engine) = await Open (body, css, epub);
            await using (book)
            using (engine)
                return await engine.LayoutChapterAsync (0, settings ?? Small, Ct);
        }

        public static async Task<BlockBox> BoxTree (string body, string css = "", LayoutSettings? settings = null, TestEpub? epub = null)
        {
            var (book, engine) = await Open (body, css, epub);
            await using (book)
            using (engine)
                return await engine.GetBoxTreeAsync (0, settings ?? Small, Ct);
        }

        private static async Task<(EpubBook, LayoutEngine)> Open (string body, string css, TestEpub? epub)
        {
            var head = $"<title>t</title><style>{css}</style>";
            var book = await (epub ?? TestEpub.Epub3 ())
                .With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml (body, head))
                .OpenAsync ();
            return (book, new LayoutEngine (book));
        }

        public static async Task<EpubBook> OpenCorpus (string file)
            => await EpubBook.OpenAsync (Path.Combine (AppContext.BaseDirectory, "corpus", file), Ct);

        public static async Task<List<ChapterLayout>> LayoutBook (LayoutEngine engine, LayoutSettings settings, int? chapters = null)
        {
            var result = new List<ChapterLayout> ();
            var count = Math.Min (chapters ?? int.MaxValue, engine.Book.Spine.Count);
            for (var i = 0; i < count; i++)
                result.Add (await engine.LayoutChapterAsync (i, settings, Ct));
            return result;
        }

        /// <summary>A solid PNG of the given size, for image layout tests.</summary>
        public static byte[] Png (int width, int height)
        {
            using var surface = SKSurface.Create (new SKImageInfo (width, height));
            surface.Canvas.Clear (SKColors.SteelBlue);
            using var image = surface.Snapshot ();
            using var data = image.Encode (SKEncodedImageFormat.Png, 100);
            return data.ToArray ();
        }

        /// <summary>Every line, in page order, with the page it is on.</summary>
        public static IEnumerable<(LayoutPage Page, TextLine Line)> Lines (ChapterLayout chapter)
            => chapter.Pages.SelectMany (p => p.Lines.Select (l => (p, l)));

        public static string Words (int count, string word = "lorem")
            => string.Join (' ', Enumerable.Range (0, count).Select (i => i % 7 == 0 ? word + "ipsum" : word));
    }

    /// <summary>
    /// Golden files under Snapshots/. RECITAL_UPDATE_SNAPSHOTS=1 rewrites them in the source tree; otherwise
    /// a mismatch fails, writes &lt;name&gt;.actual.json beside the test assembly, and names the first
    /// differing line.
    /// </summary>
    internal static class Snapshot
    {
        public static void AssertMatches (string name, string actual)
        {
            actual = actual.Replace ("\r\n", "\n");
            var source = Path.Combine (SourceDirectory (), "Snapshots", name + ".json");

            if (Environment.GetEnvironmentVariable ("RECITAL_UPDATE_SNAPSHOTS") == "1") {
                Directory.CreateDirectory (Path.GetDirectoryName (source)!);
                File.WriteAllText (source, actual);
                return;
            }

            var expectedPath = Path.Combine (AppContext.BaseDirectory, "Snapshots", name + ".json");
            var actualPath = Path.Combine (AppContext.BaseDirectory, name + ".actual.json");

            if (!File.Exists (expectedPath)) {
                File.WriteAllText (actualPath, actual);
                Assert.Fail ($"No snapshot {name}.json. Review {actualPath} and commit it as {source}, or run with RECITAL_UPDATE_SNAPSHOTS=1.");
            }

            var expected = File.ReadAllText (expectedPath).Replace ("\r\n", "\n");
            if (expected == actual)
                return;

            File.WriteAllText (actualPath, actual);
            var e = expected.Split ('\n');
            var a = actual.Split ('\n');
            var line = 0;
            while (line < e.Length && line < a.Length && e[line] == a[line])
                line++;
            Assert.Fail ($"{name}.json differs at line {line + 1}:\n  expected: {(line < e.Length ? e[line] : "<end>")}\n  actual:   {(line < a.Length ? a[line] : "<end>")}\nActual: {actualPath}");
        }

        public static string Quote (string text) => System.Text.Json.JsonSerializer.Serialize (text);

        private static string SourceDirectory ([System.Runtime.CompilerServices.CallerFilePath] string path = "")
            => Path.GetDirectoryName (path)!;
    }
}
