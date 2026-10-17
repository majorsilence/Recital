using System.Diagnostics;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>
    /// A synthesizer that takes <see cref="Latency"/> to make audio lasting <see cref="PerChar"/> per character
    /// (silence), with word timings unless told not to.
    /// </summary>
    internal sealed class FakeSynthesizer (TimeSpan latency, TimeSpan perChar, bool wordTimings = true) : ISpeechSynthesizer
    {
        public TimeSpan Latency { get; } = latency;
        public TimeSpan PerChar { get; } = perChar;
        public int Calls;

        public string Id => "fake";
        public string Name => "Fake";
        public TtsCapabilities Capabilities => new (false, wordTimings, 600);

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TtsVoice>> ([new ("v", "Voice", "en")]);

        public async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            Interlocked.Increment (ref Calls);
            await Task.Delay (Latency, ct);
            var format = PcmFormat.Speech;
            var perChar = PerChar / settings.ClampedRate;
            var pcm = new byte[format.Bytes (perChar * chunk.Text.Length)];
            var words = wordTimings ? chunk.WordStarts ().Select (i => new WordTiming (i, perChar * i)).ToList () : [];
            return new SynthesizedAudio (format, pcm, words);
        }

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;
    }

    /// <summary>
    /// An output that "plays" in real time without a sound card: it counts audio as played as the clock runs,
    /// up to what was written, and records every gap (the queue ran dry, then more audio came).
    /// </summary>
    internal sealed class RealtimeOutput : IAudioOutput
    {
        private readonly object gate = new ();
        private readonly Stopwatch clock = new ();
        private PcmFormat format = PcmFormat.Speech;
        private TimeSpan written;
        private TimeSpan played;
        private TimeSpan last;
        private TimeSpan? starved_since;
        private bool paused;

        public List<TimeSpan> Gaps { get; } = [];
        public TimeSpan MaxGap => Gaps.Count == 0 ? TimeSpan.Zero : Gaps.Max ();

        public Task StartAsync (PcmFormat pcmFormat, CancellationToken ct = default)
        {
            lock (gate) {
                format = pcmFormat;
                written = played = last = TimeSpan.Zero;
                starved_since = null;
                clock.Restart ();
            }
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            lock (gate) {
                Update ();
                if (starved_since is { } since && played > TimeSpan.Zero)
                    Gaps.Add (clock.Elapsed - since);
                starved_since = null;
                written += format.Duration (pcm.Length);
            }
            return ValueTask.CompletedTask;
        }

        public TimeSpan Played {
            get {
                lock (gate) {
                    Update ();
                    return played;
                }
            }
        }

        private void Update ()
        {
            var now = clock.Elapsed;
            if (!paused) {
                played += now - last;
                if (played >= written) {
                    if (starved_since is null && written > TimeSpan.Zero)
                        starved_since = now - (played - written);
                    played = written;
                }
            }
            last = now;
        }

        public void Pause ()
        {
            lock (gate) {
                Update ();
                paused = true;
            }
        }

        public void Unpause ()
        {
            lock (gate) {
                Update ();
                paused = false;
            }
        }

        public Task StopAsync ()
        {
            lock (gate) {
                written = played = TimeSpan.Zero;
                starved_since = null;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;
    }

    /// <summary>An engine that "speaks" by itself, a word every millisecond or so, like speech-dispatcher would.</summary>
    internal sealed class FakeDirectEngine : ITtsEngine
    {
        public List<SpeechChunk> Spoken { get; } = [];
        public List<TtsSettings> Opened { get; } = [];
        public TimeSpan PerWord { get; set; } = TimeSpan.FromMilliseconds (1);

        public string Id => "direct";
        public string Name => "Direct";
        public TtsCapabilities Capabilities => new (true, true, 1500);

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TtsVoice>> ([]);

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language)
        {
            lock (Opened)
                Opened.Add (settings);
            return new Channel (this);
        }

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;

        private sealed class Channel (FakeDirectEngine engine) : ISpeechChannel
        {
            private readonly Queue<SpeechChunk> queue = new ();
            private readonly CancellationTokenSource stop = new ();
            private Task? speaker;
            private volatile bool paused;

            public int Pending {
                get {
                    lock (queue)
                        return queue.Count;
                }
            }

            public bool IsPaused => paused;

            public event EventHandler<SpeechChunk>? ChunkStarted;
            public event EventHandler<SpeechProgress>? Progress;
            public event EventHandler<SpeechChunk>? ChunkFinished;
            public event EventHandler<Exception>? Failed { add { } remove { } }

            public void Enqueue (SpeechChunk chunk)
            {
                lock (queue) {
                    queue.Enqueue (chunk);
                    if (speaker is null || speaker.IsCompleted)
                        speaker = Task.Run (SpeakAsync);
                }
            }

            private async Task SpeakAsync ()
            {
                while (!stop.IsCancellationRequested) {
                    SpeechChunk chunk;
                    lock (queue) {
                        if (!queue.TryPeek (out chunk!))
                            return;
                    }
                    lock (engine.Spoken)
                        engine.Spoken.Add (chunk);
                    ChunkStarted?.Invoke (this, chunk);
                    foreach (var word in chunk.WordStarts ()) {
                        while (paused && !stop.IsCancellationRequested)
                            await Task.Delay (5);
                        if (stop.IsCancellationRequested)
                            return;
                        Progress?.Invoke (this, new SpeechProgress (chunk, word));
                        await Task.Delay (engine.PerWord);
                    }
                    lock (queue)
                        queue.Dequeue ();
                    ChunkFinished?.Invoke (this, chunk);
                }
            }

            public void Pause () => paused = true;
            public void Unpause () => paused = false;

            public async Task StopAsync ()
            {
                await stop.CancelAsync ();
                if (speaker is not null)
                    await speaker;
            }

            public async ValueTask DisposeAsync () => await StopAsync ();
        }
    }
}
