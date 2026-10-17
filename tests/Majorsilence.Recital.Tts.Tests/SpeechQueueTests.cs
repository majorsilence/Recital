using Majorsilence.Recital.Epub;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    public class SpeechQueueTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static async Task<(EpubBook Book, ContentDocument Document, IReadOnlyList<SpeechChunk> Chunks, int Start)> MobyDickChapter1Async ()
        {
            var book = await PlannerTests.OpenAsync ("pg-2701-moby-dick-epub3.epub");
            var entry = PlannerTests.Flatten (book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.");
            var document = await book.LoadContentDocumentAsync (entry.SpineIndex!.Value, Ct);
            var chunks = UtterancePlanner.Plan (document, 600);
            var start = UtterancePlanner.Locate (chunks, new DomPosition (document.FindFragment (entry.Fragment!)!, 0)).Chunk;
            return (book, document, chunks, start);
        }

        /// <summary>
        /// The plan's acceptance, at the channel: chunks play back to back (no gap over 300 ms once started,
        /// though each takes 150 ms to synthesize), and the word reported is never early and never a sentence late.
        /// </summary>
        [Theory (Timeout = 60_000)]
        [InlineData (true)]
        [InlineData (false)]
        public async Task ChunksPlayWithoutGapsAndProgressKeepsUp (bool wordTimings)
        {
            var (book, _, chunks, start) = await MobyDickChapter1Async ();
            await using var _book = book;
            var perChar = TimeSpan.FromMilliseconds (1.5);
            var synthesizer = new FakeSynthesizer (TimeSpan.FromMilliseconds (150), perChar, wordTimings);
            var output = new RealtimeOutput ();
            await using var channel = new AudioSpeechChannel (synthesizer, output, new TtsSettings (), "en");

            var played = chunks.Skip (start).Take (6).ToList ();
            var events = new List<(SpeechChunk Chunk, int Char, TimeSpan At)> ();
            var finished = new TaskCompletionSource ();
            channel.Progress += (_, p) => { lock (events) events.Add ((p.Chunk, p.CharIndex, output.Played)); };
            channel.ChunkFinished += (_, c) => { if (c == played[^1]) finished.TrySetResult (); };
            channel.Failed += (_, ex) => finished.TrySetException (ex);
            foreach (var chunk in played)
                channel.Enqueue (chunk);
            await finished.Task.WaitAsync (TimeSpan.FromSeconds (40), Ct);

            Assert.True (output.MaxGap < TimeSpan.FromMilliseconds (300), $"Longest gap {output.MaxGap.TotalMilliseconds} ms");

            // Where each word really is on the output's timeline.
            var chunkStart = new Dictionary<SpeechChunk, TimeSpan> ();
            var t = TimeSpan.Zero;
            foreach (var chunk in played) {
                t += chunk.PauseBefore;
                chunkStart[chunk] = t;
                t += perChar * chunk.Text.Length + chunk.PauseAfter;
            }
            Assert.NotEmpty (events);
            foreach (var (chunk, ch, at) in events) {
                var expected = chunkStart[chunk] + perChar * ch;
                var sentence = chunk.Sentences[chunk.SentenceAt (ch)];
                var sentenceLength = perChar * (sentence.End - sentence.Start);
                Assert.True (at >= expected - TimeSpan.FromMilliseconds (1), $"Reported '{chunk.Text[ch..Math.Min (chunk.Text.Length, ch + 10)]}' {(expected - at).TotalMilliseconds} ms early");
                Assert.True (at - expected < sentenceLength + TimeSpan.FromMilliseconds (120), $"Reported {(at - expected).TotalMilliseconds} ms late, a sentence lasts {sentenceLength.TotalMilliseconds}");
            }
            // Every sentence of every chunk was reported.
            foreach (var chunk in played)
                for (var s = 0; s < chunk.Sentences.Count; s++)
                    Assert.Contains (events, e => e.Chunk == chunk && chunk.SentenceAt (e.Char) == s);
        }

        [Fact]
        public async Task TheCacheMeansAChunkIsSynthesizedOnce ()
        {
            var (book, _, chunks, start) = await MobyDickChapter1Async ();
            await using var _book = book;
            var directory = Path.Combine (Path.GetTempPath (), "recital-tts-cache-" + Guid.NewGuid ().ToString ("N"));
            try {
                var synthesizer = new FakeSynthesizer (TimeSpan.Zero, TimeSpan.FromMilliseconds (0.2));
                var cache = new AudioCache (directory);
                for (var round = 0; round < 2; round++) {
                    await using var channel = new AudioSpeechChannel (synthesizer, new RealtimeOutput (), new TtsSettings (), "en", cache);
                    var done = new TaskCompletionSource ();
                    channel.ChunkFinished += (_, c) => done.TrySetResult ();
                    channel.Enqueue (chunks[start + 1]);
                    await done.Task.WaitAsync (TimeSpan.FromSeconds (10), Ct);
                }
                Assert.Equal (1, synthesizer.Calls);

                // A different speed is different audio.
                var key1 = AudioCache.Key ("fake", new TtsSettings (Rate: 1), "en", chunks[start + 1]);
                var key2 = AudioCache.Key ("fake", new TtsSettings (Rate: 1.5), "en", chunks[start + 1]);
                Assert.NotEqual (key1, key2);
            } finally {
                if (Directory.Exists (directory))
                    Directory.Delete (directory, recursive: true);
            }
        }

        [Fact]
        public void WaveFilesRoundTrip ()
        {
            var pcm = Enumerable.Range (0, 400).Select (i => (byte)i).ToArray ();
            var (format, read) = Wave.Read (Wave.Write (PcmFormat.Speech, pcm));
            Assert.Equal (PcmFormat.Speech, format);
            Assert.Equal (pcm, read);
            Assert.Throws<InvalidDataException> (() => Wave.Read ("RIFF0000WAVEnope"u8));
        }
    }
}
