using AngleSharp.Dom;
using Xunit;

namespace Majorsilence.Recital.Epub.Tests
{
    /// <summary>
    /// Every book in tests/corpus parses, and CFIs round-trip on every paragraph of every book (plan Phase 2).
    /// Expected metadata comes from reading each OPF by hand, not from this parser.
    /// </summary>
    public class CorpusTests
    {
        public static TheoryData<string, string, string, string, int, EpubTocSource> Books => new () {
            { "se-austen-pride-and-prejudice.epub", "Pride and Prejudice", "Jane Austen", "en-GB", 65, EpubTocSource.Nav },
            { "se-shelley-frankenstein.epub", "Frankenstein", "Mary Shelley", "en-GB", 38, EpubTocSource.Nav },
            { "pg-11-alice-epub3.epub", "Alice's Adventures in Wonderland", "Lewis Carroll", "en", 15, EpubTocSource.Nav },
            { "pg-1342-pride-and-prejudice-epub2.epub", "Pride and Prejudice", "Jane Austen", "en", 17, EpubTocSource.Ncx },
            { "pg-84-frankenstein-epub3.epub", "Frankenstein; or, the modern prometheus", "Mary Wollstonecraft Shelley", "en", 32, EpubTocSource.Nav },
            { "pg-1661-sherlock-holmes-epub3.epub", "The Adventures of Sherlock Holmes", "Arthur Conan Doyle", "en", 15, EpubTocSource.Nav },
            { "pg-2701-moby-dick-epub3.epub", "Moby Dick; Or, The Whale", "Herman Melville", "en", 13, EpubTocSource.Nav },
        };

        public static TheoryData<string> Files => new (Books.Select (row => row.Data.Item1));

        [Theory]
        [MemberData (nameof (Books))]
        public async Task ParsesPackage (string file, string title, string author, string language, int spineCount, EpubTocSource tocSource)
        {
            await using var book = await Open (file);

            Assert.Equal (title, book.Metadata.Title);
            Assert.Equal (author, Assert.Single (book.Metadata.Authors).Name);
            Assert.Equal (language, book.Metadata.Language);
            Assert.False (string.IsNullOrEmpty (book.Metadata.UniqueIdentifier));
            Assert.Equal (spineCount, book.Spine.Count);
            Assert.Equal (tocSource, book.TableOfContentsSource);
            Assert.NotEmpty (book.TableOfContents);
            Assert.False (book.IsDrmProtected);
        }

        [Theory]
        [MemberData (nameof (Files))]
        public async Task FindsAnImageCover (string file)
        {
            await using var book = await Open (file);

            var cover = Assert.IsType<ManifestItem> (book.Cover);
            var bytes = await book.ReadResourceAsync (cover, TestContext.Current.CancellationToken);

            var jpeg = bytes is [0xFF, 0xD8, ..];
            var png = bytes is [0x89, 0x50, 0x4E, 0x47, ..];
            Assert.True (jpeg || png, $"{cover.Href} is neither JPEG nor PNG.");
        }

        [Theory]
        [MemberData (nameof (Files))]
        public async Task TableOfContentsTargetsResolve (string file)
        {
            await using var book = await Open (file);

            foreach (var entry in Flatten (book.TableOfContents).Where (e => e.Path is not null)) {
                var spineIndex = Assert.IsType<int> (entry.SpineIndex);

                if (entry.Fragment is { } fragment) {
                    var document = await book.LoadContentDocumentAsync (spineIndex, TestContext.Current.CancellationToken);
                    Assert.True (document.FindFragment (fragment) is not null, $"'{entry.Title}' → #{fragment} not found in {entry.Path}.");
                }
            }
        }

        [Theory]
        [MemberData (nameof (Files))]
        public async Task EveryContentDocumentIsWellFormedXml (string file)
        {
            await using var book = await Open (file);

            foreach (var item in book.Spine) {
                var document = await book.LoadContentDocumentAsync (item.Index, TestContext.Current.CancellationToken);
                Assert.True (document.ParsedAsXml, $"{item.Content.Path} fell back to the HTML parser.");
            }

            Assert.DoesNotContain (book.Warnings, w => w.Code == "xhtml-not-well-formed");
        }

        /// <summary>
        /// For every paragraph: the paragraph element itself, and the start, middle and end of its first
        /// text. Each position goes to a CFI, to a string, back to a CFI, and resolves to the same node and
        /// offset; and all of them, in document order, sort strictly ascending.
        /// </summary>
        [Theory]
        [MemberData (nameof (Files))]
        public async Task CfiRoundTripsOnEveryParagraph (string file)
        {
            await using var book = await Open (file);
            var inOrder = new List<EpubCfi> ();
            var paragraphs = 0;

            foreach (var item in book.Spine) {
                var document = await book.LoadContentDocumentAsync (item.Index, TestContext.Current.CancellationToken);

                foreach (var paragraph in document.Document.QuerySelectorAll ("p")) {
                    paragraphs++;

                    var positions = new List<DomPosition> { new (paragraph, 0) };
                    if (FirstText (paragraph) is { } text) {
                        positions.Add (new (text, 0));
                        if (text.Length > 1)
                            positions.Add (new (text, text.Length / 2));
                        if (text.Length > 0)
                            positions.Add (new (text, text.Length));
                    }

                    foreach (var position in positions) {
                        var cfi = document.GetCfi (position);
                        var parsed = EpubCfi.Parse (cfi.ToString ());

                        Assert.Equal (cfi, parsed);
                        Assert.Equal (item.Index, book.ResolveSpineIndex (parsed));

                        var resolved = document.Resolve (parsed);
                        Assert.True (resolved is not null, $"{cfi} did not resolve.");
                        Assert.Same (position.Node, resolved.Value.Node);
                        Assert.Equal (position.Offset, resolved.Value.Offset);

                        inOrder.Add (parsed);
                    }
                }
            }

            Assert.True (paragraphs > 50, $"{file}: only {paragraphs} paragraphs found.");
            TestContext.Current.TestOutputHelper?.WriteLine ($"{file}: {paragraphs} paragraphs, {inOrder.Count} CFIs round-tripped.");

            for (var i = 1; i < inOrder.Count; i++)
                Assert.True (inOrder[i - 1] < inOrder[i], $"{inOrder[i - 1]} should sort before {inOrder[i]}.");
        }

        [Theory]
        [MemberData (nameof (Files))]
        public async Task ResolveAsyncFindsTheSameText (string file)
        {
            await using var book = await Open (file);
            var middle = book.Spine.Count / 2;
            var document = await book.LoadContentDocumentAsync (middle, TestContext.Current.CancellationToken);
            var text = document.Document.QuerySelectorAll ("p").Select (FirstText).First (t => t is { Length: > 10 })!;

            var resolved = await book.ResolveAsync (document.GetCfi (text, 5), TestContext.Current.CancellationToken);

            var (other, position) = Assert.IsType<(ContentDocument, DomPosition)> (resolved);
            Assert.Equal (middle, other.SpineIndex);
            Assert.Equal (text.Data, ((IText)position.Node).Data);
            Assert.Equal (5, position.Offset);
        }

        internal static async Task<EpubBook> Open (string file)
            => await EpubBook.OpenAsync (Path.Combine (AppContext.BaseDirectory, "corpus", file), TestContext.Current.CancellationToken);

        private static IText? FirstText (IElement element)
        {
            foreach (var node in element.ChildNodes) {
                if (node is IText { Length: > 0 } text && !string.IsNullOrWhiteSpace (text.Data))
                    return text;
                if (node is IElement child && FirstText (child) is { } inner)
                    return inner;
            }
            return null;
        }

        private static IEnumerable<TocEntry> Flatten (IEnumerable<TocEntry> entries)
            => entries.SelectMany (e => Flatten (e.Children).Prepend (e));
    }
}
