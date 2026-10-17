using System.Xml.Linq;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Epub.Tests;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    public class PlannerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        internal static Task<EpubBook> OpenAsync (string file) => EpubBook.OpenAsync (Path.Combine (AppContext.BaseDirectory, "corpus", file), Ct);

        [Theory]
        [InlineData ("Mr. Darcy bowed. She smiled.", new[] { "Mr. Darcy bowed.", "She smiled." })]
        [InlineData ("“Where?” said he. “Here!” I cried.", new[] { "“Where?” said he.", "“Here!”", "I cried." })]
        [InlineData ("J. R. R. Tolkien wrote it... Then he slept.", new[] { "J. R. R. Tolkien wrote it...", "Then he slept." })]
        [InlineData ("It cost 3.50 dollars. Too much (it was.) Really?", new[] { "It cost 3.50 dollars.", "Too much (it was.)", "Really?" })]
        [InlineData ("No ending", new[] { "No ending" })]
        public void SplitsSentences (string text, string[] expected)
            => Assert.Equal (expected, SentenceSplitter.Split (text).Select (s => text[s.Start..s.End]));

        [Fact]
        public async Task PlansAChapterIntoHeadingAndParagraphChunks ()
        {
            await using var book = await OpenAsync ("pg-2701-moby-dick-epub3.epub");
            var entry = Flatten (book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.");
            var document = await book.LoadContentDocumentAsync (entry.SpineIndex!.Value, Ct);
            var chunks = UtterancePlanner.Plan (document);

            var start = UtterancePlanner.Locate (chunks, new DomPosition (document.FindFragment (entry.Fragment!)!, 0));
            var heading = chunks[start.Chunk];
            Assert.Equal (ChunkKind.Heading, heading.Kind);
            Assert.Equal ("CHAPTER 1. Loomings.", heading.Text);
            Assert.True (heading.PauseBefore >= UtterancePlanner.HeadingPause);

            var first = chunks[start.Chunk + 1];
            Assert.Equal (ChunkKind.Paragraph, first.Kind);
            Assert.StartsWith ("Call me Ishmael. Some years ago—never mind how long precisely—", first.Text, StringComparison.Ordinal);
            Assert.Equal ("Call me Ishmael.", first.Text[first.Sentences[0].Start..first.Sentences[0].End]);
            Assert.True (first.Sentences.Count > 5);

            // Every character maps back to the same character in the document.
            foreach (var chunk in chunks.Take (40))
                for (var i = 0; i < chunk.Text.Length; i++)
                    if (chunk.Text[i] != ' ' && chunk.PositionAt (i) is { Node: AngleSharp.Dom.IText text } p)
                        Assert.Equal (chunk.Text[i], text.Data[p.Offset]);
        }

        [Fact]
        public async Task MarksEmphasisAndDialogue ()
        {
            await using var book = await OpenAsync ("pg-1661-sherlock-holmes-epub3.epub");
            var chunks = await AllChunksAsync (book);
            var dialogue = chunks.First (c => c.Text.StartsWith ("“Wedlock suits you,”", StringComparison.Ordinal));
            var span = dialogue.Spans.First (s => s.Kind == ProsodyKind.Dialogue);
            Assert.Equal ("“Wedlock suits you,”", dialogue.Text[span.Start..span.End]);

            var emphasis = chunks.SelectMany (c => c.Spans.Where (s => s.Kind is ProsodyKind.Emphasis).Select (s => c.Text[s.Start..s.End])).ToList ();
            Assert.NotEmpty (emphasis);
            Assert.All (emphasis, e => Assert.Equal (e.Trim (), e));
        }

        [Fact]
        public async Task LongParagraphsSplitAtSentenceEnds ()
        {
            await using var book = await OpenAsync ("pg-2701-moby-dick-epub3.epub");
            var chunks = await AllChunksAsync (book, maxChunkChars: 300);
            Assert.All (chunks, c => Assert.True (c.Text.Length <= 300 || c.Sentences.Count == 1, $"{c.Text.Length}: {c}"));
            Assert.All (chunks, c => Assert.Equal (c.Text.Length, c.Sentences[^1].End));
        }

        /// <summary>Every chunk of three books makes well-formed SSML, with marks and a dialogue voice too.</summary>
        [Theory]
        [InlineData ("pg-11-alice-epub3.epub")]
        [InlineData ("pg-1661-sherlock-holmes-epub3.epub")]
        [InlineData ("se-austen-pride-and-prejudice.epub")]
        public async Task EveryChunkIsWellFormedSsml (string file)
        {
            await using var book = await OpenAsync (file);
            foreach (var chunk in await AllChunksAsync (book)) {
                var plain = XDocument.Parse (Ssml.Build (chunk, "en-GB"), LoadOptions.PreserveWhitespace);
                Assert.Equal (chunk.Text, plain.Root!.Value);
                var marked = XDocument.Parse (Ssml.Build (chunk, "en-GB", dialogueVoice: "Other", marks: true), LoadOptions.PreserveWhitespace);
                XNamespace ns = "http://www.w3.org/2001/10/synthesis";
                Assert.Equal (chunk.WordStarts ().Count (), marked.Descendants (ns + "mark").Count ());
                Assert.Equal (chunk.Text, marked.Root!.Value);
            }
        }

        internal static async Task<List<SpeechChunk>> AllChunksAsync (EpubBook book, int maxChunkChars = 2000)
        {
            var chunks = new List<SpeechChunk> ();
            foreach (var item in book.Spine.Where (s => s.Linear && s.Content.IsXhtml))
                chunks.AddRange (UtterancePlanner.Plan (await book.LoadContentDocumentAsync (item.Index, Ct), maxChunkChars));
            return chunks;
        }

        internal static IEnumerable<TocEntry> Flatten (IEnumerable<TocEntry> entries)
            => entries.SelectMany (e => Flatten (e.Children).Prepend (e));
    }
}
