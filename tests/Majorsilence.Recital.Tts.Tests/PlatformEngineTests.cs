using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Tts.Platform;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>
    /// The OS voices, where this machine has them. They make sound, so they run only when asked:
    /// RECITAL_TEST_SPEECH=1 (Linux speech-dispatcher at its lowest volume; Windows SAPI and macOS say
    /// only synthesize, without playing).
    /// </summary>
    public class PlatformEngineTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static bool Enabled => Environment.GetEnvironmentVariable ("RECITAL_TEST_SPEECH") == "1";

        private static async Task<IReadOnlyList<SpeechChunk>> TwoParagraphsAsync ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-11-alice-epub3.epub");
            var chunks = await PlannerTests.AllChunksAsync (book);
            return chunks.Where (c => c.Kind == ChunkKind.Paragraph && c.Text.Length is > 60 and < 260).Take (2).ToList ();
        }

        [Fact (Timeout = 120_000)]
        public async Task SpeechDispatcherSpeaksWithMarks ()
        {
            Assert.SkipUnless (Enabled && SpeechDispatcherEngine.IsAvailable (), "Set RECITAL_TEST_SPEECH=1 on a Linux desktop with speech-dispatcher.");
            Environment.SetEnvironmentVariable ("RECITAL_SPEECHD_VOLUME", "-100");
            await using var engine = new SpeechDispatcherEngine ();
            var voices = await engine.GetVoicesAsync (Ct);
            Assert.NotEmpty (voices);

            var chunks = await TwoParagraphsAsync ();
            await using var channel = engine.OpenChannel (new TtsSettings (Rate: 2), "en");
            var progress = new List<SpeechProgress> ();
            var finished = new List<SpeechChunk> ();
            var done = new TaskCompletionSource ();
            channel.Progress += (_, p) => { lock (progress) progress.Add (p); };
            channel.ChunkFinished += (_, c) => { lock (finished) { finished.Add (c); if (finished.Count == chunks.Count) done.TrySetResult (); } };
            channel.Failed += (_, ex) => done.TrySetException (ex);
            foreach (var chunk in chunks)
                channel.Enqueue (chunk);
            await done.Task.WaitAsync (TimeSpan.FromSeconds (90), Ct);

            Assert.Equal (chunks, finished);
            // Marks came back in order, for words of both paragraphs (an output module without index marks reports none).
            if (progress.Count > 0) {
                Assert.Contains (progress, p => p.Chunk == chunks[1]);
                Assert.Equal (progress.Where (p => p.Chunk == chunks[0]).Select (p => p.CharIndex).Order (), progress.Where (p => p.Chunk == chunks[0]).Select (p => p.CharIndex));
            }
        }

        [Fact (Timeout = 60_000)]
        public async Task ThePipeOutputPlaysInRealTimeAndPauses ()
        {
            Assert.SkipUnless (Enabled && OperatingSystem.IsLinux () && PipeAudioOutput.IsAvailable (), "Set RECITAL_TEST_SPEECH=1 on a Linux desktop with pw-cat, pacat or aplay.");
            if (!OperatingSystem.IsLinux ())
                return;
            await using var output = new PipeAudioOutput ();
            await output.StartAsync (PcmFormat.Speech, Ct);
            // Writing waits while the player's pipe is full (the player reads as it plays), so it runs in the background.
            var clock = System.Diagnostics.Stopwatch.StartNew ();
            var writing = output.WriteAsync (new byte[PcmFormat.Speech.Bytes (TimeSpan.FromSeconds (4))], Ct).AsTask ();
            await Task.Delay (1000, Ct);
            var expected = clock.Elapsed - TimeSpan.FromMilliseconds (150);
            Assert.InRange (output.Played.TotalMilliseconds, expected.TotalMilliseconds - 150, expected.TotalMilliseconds + 150);

            output.Pause ();
            var paused = output.Played;
            await Task.Delay (400, Ct);
            Assert.Equal (paused, output.Played);
            output.Unpause ();
            await writing;
            // The pipe holds the last second or so: wait for it to play out.
            for (var i = 0; i < 40 && output.Played < TimeSpan.FromSeconds (3.9); i++)
                await Task.Delay (100, Ct);
            Assert.InRange (output.Played.TotalSeconds, 3.9, 4.0);
            await output.StopAsync ();
            Assert.Equal (TimeSpan.Zero, output.Played);
        }

        [Fact]
        public void SpeedMapsOntoSpeechDispatchersScale ()
        {
            Assert.Equal (0, SpeechDispatcherEngine.Rate (1));
            Assert.Equal (100, SpeechDispatcherEngine.Rate (2));
            Assert.Equal (100, SpeechDispatcherEngine.Rate (3));
            Assert.Equal (-100, SpeechDispatcherEngine.Rate (0.5));
            Assert.Equal (50, SpeechDispatcherEngine.Rate (1.5));
        }
    }
}
