using Majorsilence.Recital.Epub;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    public class PlayerTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static TtsPlayer Player (EpubBook book, ITtsEngine engine, Dictionary<int, ContentDocument> documents)
            => new (engine, async (spine, ct) => {
                lock (documents) {
                    if (documents.TryGetValue (spine, out var cached))
                        return cached;
                }
                var document = await book.LoadContentDocumentAsync (spine, ct);
                lock (documents)
                    documents[spine] = document;
                return document;
            }, spine => {
                for (var i = spine + 1; i < book.Spine.Count; i++)
                    if (book.Spine[i].Linear && book.Spine[i].Content.IsXhtml)
                        return i;
                return null;
            }, "en");

        private static async Task<ContentDocument> LoadAsync (EpubBook book, Dictionary<int, ContentDocument> documents, int spine)
        {
            var document = await book.LoadContentDocumentAsync (spine, Ct);
            documents[spine] = document;
            return document;
        }

        [Fact (Timeout = 60_000)]
        public async Task ReadsFromAPositionToTheEndOfTheBookAcrossChapters ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-11-alice-epub3.epub");
            var engine = new FakeDirectEngine { PerWord = TimeSpan.Zero };
            var documents = new Dictionary<int, ContentDocument> ();
            await using var player = Player (book, engine, documents);
            var lastSpine = book.Spine.Last (s => s.Linear && s.Content.IsXhtml).Index;
            var startSpine = book.Spine.Where (s => s.Linear && s.Content.IsXhtml).Reverse ().Skip (1).First ().Index;
            var document = await LoadAsync (book, documents, startSpine);

            var stopped = new TaskCompletionSource<TtsStoppedEventArgs> ();
            var spines = new HashSet<int> ();
            player.Speaking += (_, r) => { lock (spines) spines.Add (r.SpineIndex); };
            player.Stopped += (_, e) => stopped.TrySetResult (e);
            await player.StartAsync (startSpine, new DomPosition ((AngleSharp.Dom.INode?)document.Document.Body ?? document.Root, 0), new TtsSettings (), Ct);

            var result = await stopped.Task.WaitAsync (TimeSpan.FromSeconds (40), Ct);
            Assert.Equal (TtsStopReason.EndOfBook, result.Reason);
            Assert.Contains (startSpine, spines);
            Assert.Contains (lastSpine, spines);
            Assert.False (player.IsPlaying);
        }

        [Fact (Timeout = 60_000)]
        public async Task StartsAtTheSentenceHoldingThePositionAndSkips ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-2701-moby-dick-epub3.epub");
            var engine = new FakeDirectEngine { PerWord = TimeSpan.FromMilliseconds (20) };
            var documents = new Dictionary<int, ContentDocument> ();
            await using var player = Player (book, engine, documents);
            var entry = PlannerTests.Flatten (book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.");
            var document = await LoadAsync (book, documents, entry.SpineIndex!.Value);
            var chunks = UtterancePlanner.Plan (document, engine.Capabilities.MaxChunkChars);
            var paragraph = chunks.First (c => c.Text.StartsWith ("Call me Ishmael.", StringComparison.Ordinal));

            // Mid-way through the second sentence ("Some years ago..."): reading starts at that sentence.
            var second = paragraph.Sentences[1];
            var spoken = new List<SpokenRange> ();
            player.Speaking += (_, r) => { lock (spoken) spoken.Add (r); };
            await player.StartAsync (entry.SpineIndex.Value, paragraph.PositionAt (second.Start + 10), new TtsSettings (), Ct);
            await WaitFor (() => spoken.Count > 2);
            Assert.Equal (paragraph.PositionAt (second.Start), spoken[0].SentenceStart);
            Assert.Equal (paragraph.PositionAt (second.Start), spoken[0].WordStart);

            await player.SkipAsync (1, Ct);
            await WaitFor (() => spoken.Any (r => r.SentenceStart == paragraph.PositionAt (paragraph.Sentences[2].Start)));
            await player.SkipAsync (-2, Ct);
            await WaitFor (() => spoken.Count (r => r.SentenceStart == paragraph.PositionAt (second.Start)) > 1);

            // Skipping while paused stays paused, shows the next sentence, and Play starts there.
            player.Pause ();
            await Task.Delay (100, Ct);   // words already on their way
            var at = Array.FindIndex ([.. paragraph.Sentences], x => paragraph.PositionAt (x.Start) == spoken[^1].SentenceStart);
            Assert.InRange (at, 0, paragraph.Sentences.Count - 2);
            var nextSentence = paragraph.PositionAt (paragraph.Sentences[at + 1].Start);
            await player.SkipAsync (1, Ct);
            Assert.True (player.IsPaused);
            Assert.Equal (nextSentence, spoken[^1].SentenceStart);
            var heard = spoken.Count;
            await Task.Delay (200, Ct);
            Assert.Equal (heard, spoken.Count);
            player.Unpause ();
            await WaitFor (() => spoken.Count > heard + 1);
            Assert.Equal (nextSentence, spoken[heard].SentenceStart);

            // A new speed restarts the sentence with the new settings.
            await player.ChangeSettingsAsync (new TtsSettings (Rate: 1.5), Ct);
            Assert.Equal (1.5, engine.Opened[^1].Rate);
            await player.StopAsync ();
            Assert.False (player.IsPlaying);
        }

        [Fact (Timeout = 60_000)]
        public async Task TheSleepTimerAndChapterEndStopPlayback ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-2701-moby-dick-epub3.epub");
            var engine = new FakeDirectEngine { PerWord = TimeSpan.FromMilliseconds (5) };
            var documents = new Dictionary<int, ContentDocument> ();
            await using var player = Player (book, engine, documents);
            var spine = PlannerTests.Flatten (book.TableOfContents).First (e => e.Title == "CHAPTER 1. Loomings.").SpineIndex!.Value;
            var document = await LoadAsync (book, documents, spine);
            var body = new DomPosition ((AngleSharp.Dom.INode?)document.Document.Body ?? document.Root, 0);

            var stopped = new TaskCompletionSource<TtsStoppedEventArgs> ();
            player.Stopped += (_, e) => stopped.TrySetResult (e);
            await player.StartAsync (spine, body, new TtsSettings (), Ct);
            player.SleepAfter (TimeSpan.FromMilliseconds (200));
            Assert.NotNull (player.SleepAt);
            Assert.Equal (TtsStopReason.SleepTimer, (await stopped.Task.WaitAsync (TimeSpan.FromSeconds (10), Ct)).Reason);

            // At the end of this chapter, not the book.
            engine.PerWord = TimeSpan.Zero;
            stopped = new TaskCompletionSource<TtsStoppedEventArgs> ();
            var spines = new HashSet<int> ();
            player.Speaking += (_, r) => { lock (spines) spines.Add (r.SpineIndex); };
            player.SleepAtChapterEnd = true;
            await player.StartAsync (spine, body, new TtsSettings (), Ct);
            Assert.Equal (TtsStopReason.SleepTimer, (await stopped.Task.WaitAsync (TimeSpan.FromSeconds (30), Ct)).Reason);
            Assert.Equal ([spine], spines);
        }

        private static async Task WaitFor (Func<bool> condition)
        {
            for (var i = 0; i < 1000 && !condition (); i++)
                await Task.Delay (10, Ct);
            Assert.True (condition ());
        }
    }
}
