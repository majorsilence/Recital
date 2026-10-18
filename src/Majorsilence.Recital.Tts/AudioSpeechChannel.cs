namespace Majorsilence.Recital.Tts
{
    /// <summary>
    /// An <see cref="ITtsEngine"/> made from a synthesizer (chunk to audio) and an audio output, for every
    /// engine that produces audio rather than speaking itself.
    /// </summary>
    public sealed class AudioTtsEngine (ISpeechSynthesizer synthesizer, Func<IAudioOutput> output, AudioCache? cache = null, TimeProvider? time = null) : ITtsEngine
    {
        public string Id => synthesizer.Id;
        public string Name => synthesizer.Name;
        public TtsCapabilities Capabilities => synthesizer.Capabilities;

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default) => synthesizer.GetVoicesAsync (ct);

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language)
            => new AudioSpeechChannel (synthesizer, output (), settings, language, cache, time);

        public ValueTask DisposeAsync () => synthesizer.DisposeAsync ();
    }

    /// <summary>
    /// Speaks chunks through a synthesizer and an <see cref="IAudioOutput"/>. Each enqueued chunk is
    /// synthesized at once (or found in the cache), in order, and its audio queued on the output behind the
    /// one before, so as long as the player enqueues ahead, playback has no gaps (plan Phase 8: under 300 ms).
    /// Progress comes from the output's play position: exact where the engine gave word timings, otherwise
    /// interpolated through the chunk by character count (plan 3.3).
    /// </summary>
    public sealed class AudioSpeechChannel : ISpeechChannel
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds (40);

        // Outputs report their position coarsely, and summed durations round differently from the total:
        // a chunk counts as heard this close to its end.
        private static readonly TimeSpan EndTolerance = TimeSpan.FromMilliseconds (15);

        private readonly ISpeechSynthesizer synthesizer;
        private readonly IAudioOutput output;
        private readonly TtsSettings settings;
        private readonly string? language;
        private readonly AudioCache? cache;
        private readonly TimeProvider time;
        private readonly object gate = new ();
        private readonly List<Entry> entries = [];
        private CancellationTokenSource stop = new ();
        private Task? feeder;
        private Task? monitor;
        private PcmFormat? format;
        private long written;
        private bool started;
        private bool paused;
        private bool disposed;
        // Set and cleared under gate, so a chunk enqueued as a loop finds the queue empty always gets a loop.
        private bool feeding;
        private bool monitoring;

        private sealed class Entry (SpeechChunk chunk)
        {
            public SpeechChunk Chunk { get; } = chunk;
            public Task<SynthesizedAudio>? Audio;
            public TimeSpan? Start;
            public TimeSpan? End;
            public bool Started;
            public int LastWord = -1;
        }

        public AudioSpeechChannel (ISpeechSynthesizer synthesizer, IAudioOutput output, TtsSettings settings, string? language, AudioCache? cache = null, TimeProvider? time = null)
        {
            this.synthesizer = synthesizer;
            this.output = output;
            this.settings = settings;
            this.language = language;
            this.cache = cache;
            this.time = time ?? TimeProvider.System;
        }

        public int Pending {
            get {
                lock (gate)
                    return entries.Count;
            }
        }

        public bool IsPaused => paused;

        public event EventHandler<SpeechChunk>? ChunkStarted;
        public event EventHandler<SpeechProgress>? Progress;
        public event EventHandler<SpeechChunk>? ChunkFinished;
        public event EventHandler<Exception>? Failed;

        public void Enqueue (SpeechChunk chunk)
        {
            lock (gate) {
                ObjectDisposedException.ThrowIf (disposed, this);
                var entry = new Entry (chunk);
                entries.Add (entry);
                // Start synthesizing now: by the time it is needed, it is ready.
                var token = stop.Token;
                entry.Audio = Task.Run (() => SynthesizeAsync (chunk, token), token);
                if (!feeding) {
                    feeding = true;
                    var feedToken = stop.Token;
                    feeder = Task.Run (() => FeedAsync (feedToken));
                }
                if (!monitoring) {
                    monitoring = true;
                    var monitorToken = stop.Token;
                    monitor = Task.Run (() => MonitorAsync (monitorToken));
                }
            }
        }

        public void Pause ()
        {
            paused = true;
            output.Pause ();
        }

        public void Unpause ()
        {
            paused = false;
            output.Unpause ();
        }

        public async Task StopAsync ()
        {
            CancellationTokenSource old;
            lock (gate) {
                old = stop;
                stop = new CancellationTokenSource ();
                entries.Clear ();
            }
            await old.CancelAsync ().ConfigureAwait (false);
            foreach (var task in new[] { feeder, monitor })
                if (task is not null)
                    try {
                        await task.ConfigureAwait (false);
                    } catch (OperationCanceledException) {
                    }
            old.Dispose ();
            await output.StopAsync ().ConfigureAwait (false);
            lock (gate) {
                started = false;
                written = 0;
                feeder = monitor = null;
                feeding = monitoring = false;
            }
            paused = false;
        }

        public async ValueTask DisposeAsync ()
        {
            await StopAsync ().ConfigureAwait (false);
            lock (gate)
                disposed = true;
            await output.DisposeAsync ().ConfigureAwait (false);
        }

        private async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, CancellationToken ct)
        {
            var key = cache is null ? null : AudioCache.Key (synthesizer.Id, settings, language, chunk);
            if (key is not null && await cache!.TryGetAsync (key, ct).ConfigureAwait (false) is { } cached)
                return cached;
            var audio = await synthesizer.SynthesizeAsync (chunk, settings, language, ct).ConfigureAwait (false);
            if (key is not null)
                await cache!.PutAsync (key, audio, ct).ConfigureAwait (false);
            return audio;
        }

        /// <summary>Writes each chunk's audio to the output, in order, as soon as it is ready.</summary>
        private async Task FeedAsync (CancellationToken ct)
        {
            try {
                while (!ct.IsCancellationRequested) {
                    Entry? entry;
                    lock (gate) {
                        entry = entries.FirstOrDefault (e => e.Start is null);
                        if (entry is null) {
                            feeding = false;
                            return;
                        }
                    }
                    var audio = await entry.Audio!.WaitAsync (ct).ConfigureAwait (false);
                    if (!started) {
                        format = audio.Format;
                        await output.StartAsync (audio.Format, ct).ConfigureAwait (false);
                        if (paused)
                            output.Pause ();
                        started = true;
                    } else if (audio.Format != format) {
                        throw new InvalidOperationException ($"The engine changed audio format mid-stream ({format} to {audio.Format}).");
                    }
                    await WriteSilenceAsync (entry.Chunk.PauseBefore, ct).ConfigureAwait (false);
                    lock (gate)
                        entry.Start = format!.Duration (written);
                    await output.WriteAsync (audio.Pcm, ct).ConfigureAwait (false);
                    written += audio.Pcm.Length;
                    lock (gate) {
                        entry.End = format.Duration (written);
                        Words[entry] = audio.Words.Count > 0 ? audio.Words : Interpolate (entry.Chunk, audio.Duration);
                    }
                    await WriteSilenceAsync (entry.Chunk.PauseAfter, ct).ConfigureAwait (false);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            } catch (Exception ex) {
                Failed?.Invoke (this, ex);
            } finally {
                lock (gate)
                    if (ct.IsCancellationRequested)
                        feeding = false;
            }
        }

        private readonly Dictionary<Entry, IReadOnlyList<WordTiming>> Words = [];

        private async Task WriteSilenceAsync (TimeSpan duration, CancellationToken ct)
        {
            if (duration <= TimeSpan.Zero || format is null)
                return;
            var silence = new byte[format.Bytes (duration)];
            await output.WriteAsync (silence, ct).ConfigureAwait (false);
            written += silence.Length;
        }

        /// <summary>Word start times spread through the audio by character count, for engines that give none.</summary>
        private static List<WordTiming> Interpolate (SpeechChunk chunk, TimeSpan duration)
        {
            var length = Math.Max (1, chunk.Text.Length);
            return chunk.WordStarts ().Select (i => new WordTiming (i, duration * (i / (double)length))).ToList ();
        }

        /// <summary>Watches the play position and reports which chunk and word is being heard.</summary>
        private async Task MonitorAsync (CancellationToken ct)
        {
            try {
                while (!ct.IsCancellationRequested) {
                    await Task.Delay (Tick, time, ct).ConfigureAwait (false);
                    var played = output.Played;
                    List<(Entry Entry, bool Start, List<int> Words, bool Finish)> events = [];
                    lock (gate) {
                        foreach (var entry in entries) {
                            if (entry.Start is not { } start || played < start)
                                break;
                            var finish = entry.End is { } end && played >= end - EndTolerance;
                            var into = played - start;
                            var words = Words.GetValueOrDefault (entry) ?? [];
                            var word = -1;
                            for (var w = 0; w < words.Count && words[w].Offset <= into; w++)
                                word = w;
                            // Every word passed since the last tick, so a short sentence is never skipped over.
                            // A finished chunk reports the rest of its words too: the end tolerance can come before the last one.
                            if (finish)
                                word = words.Count - 1;
                            var report = new List<int> ();
                            for (var w = entry.LastWord + 1; w <= word; w++)
                                report.Add (words[w].CharIndex);
                            events.Add ((entry, !entry.Started, report, finish));
                            entry.Started = true;
                            if (word > entry.LastWord)
                                entry.LastWord = word;
                        }
                        foreach (var e in events.Where (e => e.Finish)) {
                            entries.Remove (e.Entry);
                            Words.Remove (e.Entry);
                        }
                    }
                    foreach (var (entry, start, crossed, finish) in events) {
                        if (start)
                            ChunkStarted?.Invoke (this, entry.Chunk);
                        foreach (var word in crossed)
                            Progress?.Invoke (this, new SpeechProgress (entry.Chunk, word));
                        if (finish)
                            ChunkFinished?.Invoke (this, entry.Chunk);
                    }
                    lock (gate) {
                        if (entries.Count == 0) {
                            monitoring = false;
                            return;
                        }
                    }
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            } catch (Exception ex) {
                Failed?.Invoke (this, ex);
            } finally {
                lock (gate)
                    if (ct.IsCancellationRequested)
                        monitoring = false;
            }
        }
    }
}
