using System.Diagnostics;
using Majorsilence.Recital.Tts.Kokoro;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>
    /// The bundled voices, end to end on the real model (fetched by build/Kokoro.targets). Run alone: the model
    /// takes the CPU, which would make the timing tests beside it late.
    /// </summary>
    [CollectionDefinition (nameof (KokoroEngineTests), DisableParallelization = true)]
    [Collection (nameof (KokoroEngineTests))]
    public class KokoroEngineTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static string Folder => KokoroSynthesizer.FindFolder ()
            ?? throw new InvalidOperationException ("The Kokoro files are missing from the test output (build/Kokoro.targets).");

        [Fact]
        public async Task ListsTheBundledVoicesWithTheDefaultFirst ()
        {
            await using var kokoro = new KokoroSynthesizer (Folder);
            var voices = await kokoro.GetVoicesAsync (Ct);

            Assert.Equal (KokoroSynthesizer.DefaultVoice, voices[0].Id);
            Assert.Equal ("Heart (US English, female)", voices[0].Name);
            Assert.Contains (voices, v => v.Id == "bm_george" && v.Language == "en-GB");
        }

        // Speaks the opening of Pride and Prejudice: audio of a believable length, with a time for every word, in
        // order, inside the audio. How fast depends on the machine (a desktop makes it 2-4x faster than it plays; a
        // shared 3-core CI runner may not), so only a gross slowdown fails.
        [Fact]
        public async Task SpeaksAChunkWithATimeForEachWord ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-1342-pride-and-prejudice-epub2.epub");
            await using var kokoro = new KokoroSynthesizer (Folder);
            var chunks = (await PlannerTests.AllChunksAsync (book, kokoro.Capabilities.MaxChunkChars))
                .SkipWhile (c => !c.Text.Contains ("truth universally acknowledged", StringComparison.Ordinal)).Take (3).ToList ();
            Assert.Equal (3, chunks.Count);
            await kokoro.SynthesizeAsync (chunks[0], new TtsSettings (), "en", Ct);   // loads the model

            foreach (var chunk in chunks) {
                var watch = Stopwatch.StartNew ();
                var audio = await kokoro.SynthesizeAsync (chunk, new TtsSettings (), "en", Ct);
                watch.Stop ();

                var seconds = audio.Duration.TotalSeconds;
                Assert.Equal (KokoroSynthesizer.SampleRate, audio.Format.SampleRate);
                Assert.InRange (seconds, chunk.Text.Length / 30.0, chunk.Text.Length / 8.0);
                Assert.True (watch.Elapsed < audio.Duration * 4, $"{watch.Elapsed} to make {audio.Duration}");

                var starts = chunk.WordStarts ().ToList ();
                Assert.InRange (audio.Words.Count, starts.Count * 9 / 10, starts.Count);
                Assert.All (audio.Words, w => Assert.Contains (w.CharIndex, starts));
                Assert.Equal (audio.Words.OrderBy (w => w.Offset).Select (w => w.CharIndex), audio.Words.Select (w => w.CharIndex));
                Assert.True (audio.Words[^1].Offset < audio.Duration);

                if (Environment.GetEnvironmentVariable ("RECITAL_TTS_SAMPLES") is { Length: > 0 } samples) {
                    Directory.CreateDirectory (samples);
                    await File.WriteAllBytesAsync (Path.Combine (samples, $"kokoro-pride-{chunks.IndexOf (chunk) + 1}.wav"), Wave.Write (audio.Format, audio.Pcm), Ct);
                }
            }
        }

        // Listen on a loaded model: the first words are heard before the first chunk (a long sentence) is all made,
        // chunks are made in reading order, and the rest follows. Real times go to the samples folder, not into
        // asserts: they depend on the machine.
        [Fact]
        public async Task StartsSpeakingBeforeTheFirstChunkIsMade ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-1342-pride-and-prejudice-epub2.epub");
            await using var kokoro = new KokoroSynthesizer (Folder);
            var chunks = (await PlannerTests.AllChunksAsync (book, kokoro.Capabilities.MaxChunkChars))
                .SkipWhile (c => !c.Text.Contains ("truth universally acknowledged", StringComparison.Ordinal)).Take (3).ToList ();
            await kokoro.PrepareAsync (Ct);

            var watch = Stopwatch.StartNew ();
            var recording = new RecordingSynthesizer (kokoro, watch);
            var output = new RealtimeOutput ();
            await using var channel = new AudioSpeechChannel (recording, output, new TtsSettings (), "en");
            var started = new TaskCompletionSource<TimeSpan> ();
            var finished = new TaskCompletionSource ();
            channel.ChunkStarted += (_, _) => started.TrySetResult (watch.Elapsed);
            channel.ChunkFinished += (_, c) => { if (c == chunks[^1]) finished.TrySetResult (); };
            channel.Failed += (_, ex) => finished.TrySetException (ex);
            foreach (var chunk in chunks)
                channel.Enqueue (chunk);
            var firstSound = await started.Task.WaitAsync (TimeSpan.FromSeconds (60), Ct);
            await finished.Task.WaitAsync (TimeSpan.FromMinutes (3), Ct);

            if (Environment.GetEnvironmentVariable ("RECITAL_TTS_SAMPLES") is { Length: > 0 } samples)
                await File.AppendAllTextAsync (Path.Combine (samples, "kokoro-latency.txt"),
                    $"first sound {firstSound.TotalMilliseconds:0} ms, gaps [{string.Join (", ", output.Gaps.Select (g => $"{g.TotalMilliseconds:0}"))}] ms\n", Ct);
            var parts = recording.Parts;
            var firstChunkMade = parts.Last (p => p.Chunk == chunks[0].Index).At;
            Assert.True (parts.Count (p => p.Chunk == chunks[0].Index) > 1, "the first chunk came in one part");
            Assert.True (firstSound < firstChunkMade, $"first sound at {firstSound}, the first chunk made at {firstChunkMade}");
            Assert.True (parts[0].Audio < TimeSpan.FromSeconds (4), $"a first part of {parts[0].Audio}");
            Assert.Equal (parts.Select (p => p.Chunk).Order (), parts.Select (p => p.Chunk));
        }

        /// <summary>Passes parts through, noting when each was made, for which chunk, and how long it plays.</summary>
        private sealed class RecordingSynthesizer (KokoroSynthesizer inner, Stopwatch watch) : IStreamingSpeechSynthesizer
        {
            public List<(int Chunk, TimeSpan At, TimeSpan Audio)> Parts { get; } = [];

            public string Id => inner.Id;
            public string Name => inner.Name;
            public TtsCapabilities Capabilities => inner.Capabilities;
            public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default) => inner.GetVoicesAsync (ct);
            public Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
                => inner.SynthesizeAsync (chunk, settings, language, ct);

            public async IAsyncEnumerable<SynthesizedAudio> SynthesizePartsAsync (SpeechChunk chunk, TtsSettings settings, string? language, Func<TimeSpan> ahead,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var part in inner.SynthesizePartsAsync (chunk, settings, language, ahead, ct)) {
                    lock (Parts)
                        Parts.Add ((chunk.Index, watch.Elapsed, part.Duration));
                    yield return part;
                }
            }

            public ValueTask DisposeAsync () => ValueTask.CompletedTask;
        }

        [Theory]
        [InlineData (32)]
        [InlineData (60)]
        [InlineData (200)]
        public void SplitsALongSentenceAtAPauseNearTheBudget (int budget)
        {
            const string phonemes = "ɪt ɪz ə tɹˈuθ jˌunəvˈɜɹsᵊli ɐkhnˈɑləʤd, ðæt ə sˈɪŋɡəl mˈæn ɪn pəzˈɛʃən ʌv ə ɡˈʊd fˈɔɹʧən, mˈʌst bi ɪn wˈɑnt ʌv ə wˈIf.";
            var pieces = new List<string> ();
            for (var start = 0; start < phonemes.Length;) {
                var end = KokoroSynthesizer.PieceEnd (phonemes, start, budget);
                pieces.Add (phonemes[start..end]);
                start = end;
            }

            Assert.Equal (phonemes, string.Concat (pieces));
            if (budget >= phonemes.Length) {
                Assert.Single (pieces);
            } else {
                Assert.EndsWith ("ɐkhnˈɑləʤd,", pieces[0]);   // a little past a tight budget, to the first pause
                if (budget >= 60)
                    Assert.All (pieces[..^1], p => Assert.EndsWith (",", p));
            }
        }

        [Fact]
        public async Task ChangesSpeedAndVoice ()
        {
            await using var book = await PlannerTests.OpenAsync ("pg-1342-pride-and-prejudice-epub2.epub");
            await using var kokoro = new KokoroSynthesizer (Folder);
            var chunk = (await PlannerTests.AllChunksAsync (book, kokoro.Capabilities.MaxChunkChars))
                .First (c => c.Text.Contains ("truth universally acknowledged", StringComparison.Ordinal));

            var normal = await kokoro.SynthesizeAsync (chunk, new TtsSettings (), "en", Ct);
            var fast = await kokoro.SynthesizeAsync (chunk, new TtsSettings (Rate: 1.5), "en", Ct);
            var george = await kokoro.SynthesizeAsync (chunk, new TtsSettings ("bm_george"), "en", Ct);

            Assert.InRange (fast.Duration / normal.Duration, 0.55, 0.8);
            Assert.NotEqual (normal.Pcm, george.Pcm);
        }
    }
}
