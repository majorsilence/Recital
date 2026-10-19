using Majorsilence.Recital.Epub;

namespace Majorsilence.Recital.Tts
{
    /// <param name="SpineIndex">The chapter.</param>
    /// <param name="SentenceStart">Where the sentence being spoken starts.</param>
    /// <param name="SentenceEnd">And ends.</param>
    /// <param name="WordStart">Where the word being spoken starts.</param>
    /// <param name="WordEnd">And ends.</param>
    public sealed record SpokenRange (int SpineIndex, DomPosition SentenceStart, DomPosition SentenceEnd, DomPosition WordStart, DomPosition WordEnd);

    public enum TtsStopReason
    {
        /// <summary>Asked to stop.</summary>
        Stopped,
        EndOfBook,
        SleepTimer,
        /// <summary>The engine failed; the message says how.</summary>
        Error,
    }

    public sealed class TtsStoppedEventArgs (TtsStopReason reason, string? message = null) : EventArgs
    {
        public TtsStopReason Reason { get; } = reason;
        public string? Message { get; } = message;
    }

    /// <summary>
    /// Reads a book aloud (plan 3.1): plans each chapter into chunks, keeps the engine a few chunks ahead so
    /// there are no gaps, follows on into the next chapter, and reports the sentence and word being spoken so
    /// the reader can highlight them and turn pages. Skipping, changing speed or voice, and the sleep timer
    /// are here too. Events come on a worker thread.
    /// </summary>
    public sealed class TtsPlayer : IAsyncDisposable
    {
        /// <summary>Chunks kept queued on the engine beyond the one playing.</summary>
        public const int Lookahead = 3;

        private readonly ITtsEngine engine;
        private readonly Func<int, CancellationToken, Task<ContentDocument>> load_chapter;
        private readonly Func<int, int?> next_chapter;
        private readonly string? language;
        private readonly TimeProvider time;
        private readonly SemaphoreSlim gate = new (1, 1);
        private readonly object state_gate = new ();
        private readonly Dictionary<int, IReadOnlyList<SpeechChunk>> plans = [];
        private ISpeechChannel? channel;
        private (int Spine, int Chunk) next;
        private bool exhausted;
        private SpeechChunk? speaking_chunk;
        private int speaking_char;
        // Where the channel was started: the current sentence until the first word is heard.
        private (int Spine, int Chunk, int Sentence)? started_at;
        private CancellationTokenSource? sleep;
        private int generation;

        /// <param name="loadChapter">The chapter's document: the same instance the reader lays out, so positions compare.</param>
        /// <param name="nextChapter">The next readable spine index after one, or null at the end.</param>
        public TtsPlayer (ITtsEngine engine, Func<int, CancellationToken, Task<ContentDocument>> loadChapter, Func<int, int?> nextChapter, string? language = null, TimeProvider? time = null)
        {
            this.engine = engine;
            load_chapter = loadChapter;
            next_chapter = nextChapter;
            this.language = language;
            this.time = time ?? TimeProvider.System;
        }

        public TtsSettings Settings { get; private set; } = new ();
        public bool IsPlaying => channel is not null;
        public bool IsPaused => channel?.IsPaused == true;

        /// <summary>When the sleep timer stops playback, or null.</summary>
        public DateTimeOffset? SleepAt { get; private set; }

        /// <summary>Stop at the end of the chapter being read.</summary>
        public bool SleepAtChapterEnd { get; set; }

        public event EventHandler<SpokenRange>? Speaking;
        public event EventHandler<TtsStoppedEventArgs>? Stopped;

        /// <summary>Starts reading at <paramref name="from"/> in chapter <paramref name="spine"/> (from the sentence holding it).</summary>
        public async Task StartAsync (int spine, DomPosition from, TtsSettings settings, CancellationToken ct = default)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                Settings = settings;
                await StopCoreAsync ().ConfigureAwait (false);
                var plan = await PlanAsync (spine, ct).ConfigureAwait (false);
                var (chunk, sentence) = UtterancePlanner.Locate (plan, from);
                await BeginAsync (spine, chunk, sentence, ct).ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
        }

        public void Pause () => channel?.Pause ();

        public void Unpause () => channel?.Unpause ();

        public async Task StopAsync ()
        {
            await gate.WaitAsync ().ConfigureAwait (false);
            try {
                var wasPlaying = channel is not null;
                await StopCoreAsync ().ConfigureAwait (false);
                if (wasPlaying)
                    Stopped?.Invoke (this, new TtsStoppedEventArgs (TtsStopReason.Stopped));
            } finally {
                gate.Release ();
            }
        }

        /// <summary>Jumps <paramref name="sentences"/> sentences forward (or back, negative) from the one being spoken.</summary>
        public async Task SkipAsync (int sentences, CancellationToken ct = default)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                if (CurrentSentence () is not var (spine, chunk, sentence))
                    return;
                var plan = await PlanAsync (spine, ct).ConfigureAwait (false);
                // Walk sentences across chunk (but not chapter) boundaries.
                var steps = sentences;
                while (steps > 0) {
                    if (sentence + 1 < plan[chunk].Sentences.Count)
                        sentence++;
                    else if (chunk + 1 < plan.Count)
                        (chunk, sentence) = (chunk + 1, 0);
                    steps--;
                }
                while (steps < 0) {
                    if (sentence > 0)
                        sentence--;
                    else if (chunk > 0)
                        (chunk, sentence) = (chunk - 1, plan[chunk - 1].Sentences.Count - 1);
                    steps++;
                }
                // Skipping while paused stays paused, and shows the sentence Play will start from.
                var paused = IsPaused;
                await StopCoreAsync ().ConfigureAwait (false);
                await BeginAsync (spine, chunk, sentence, ct, paused).ConfigureAwait (false);
                if (paused) {
                    if (chunk < plan.Count && plan[chunk].Sentences.Count > 0)
                        OnProgress (generation, plan[chunk], plan[chunk].Sentences[Math.Clamp (sentence, 0, plan[chunk].Sentences.Count - 1)].Start);
                }
            } finally {
                gate.Release ();
            }
        }

        /// <summary>A new voice or speed, from the sentence being spoken.</summary>
        public async Task ChangeSettingsAsync (TtsSettings settings, CancellationToken ct = default)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                Settings = settings;
                if (CurrentSentence () is not var (spine, chunk, sentence))
                    return;
                var paused = IsPaused;
                await StopCoreAsync ().ConfigureAwait (false);
                await BeginAsync (spine, chunk, sentence, ct, paused).ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
        }

        /// <summary>Stops playback after <paramref name="duration"/> (null cancels the timer).</summary>
        public void SleepAfter (TimeSpan? duration)
        {
            sleep?.Cancel ();
            sleep = null;
            SleepAt = null;
            if (duration is not { } wait)
                return;
            var source = new CancellationTokenSource ();
            sleep = source;
            SleepAt = time.GetUtcNow () + wait;
            _ = Task.Run (async () => {
                try {
                    await Task.Delay (wait, time, source.Token).ConfigureAwait (false);
                } catch (OperationCanceledException) {
                    return;
                }
                SleepAt = null;
                await StopWithAsync (TtsStopReason.SleepTimer).ConfigureAwait (false);
            });
        }

        public async ValueTask DisposeAsync ()
        {
            sleep?.Cancel ();
            await gate.WaitAsync ().ConfigureAwait (false);
            try {
                await StopCoreAsync ().ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
            gate.Dispose ();
        }

        private async Task<IReadOnlyList<SpeechChunk>> PlanAsync (int spine, CancellationToken ct)
        {
            lock (state_gate) {
                if (plans.TryGetValue (spine, out var cached))
                    return cached;
            }
            var document = await load_chapter (spine, ct).ConfigureAwait (false);
            var plan = UtterancePlanner.Plan (document, engine.Capabilities.MaxChunkChars);
            lock (state_gate) {
                // Only nearby chapters stay planned.
                foreach (var key in plans.Keys.Where (k => Math.Abs (k - spine) > 2).ToList ())
                    plans.Remove (key);
                plans[spine] = plan;
            }
            return plan;
        }

        /// <summary>Opens a channel and starts at a sentence; callers hold <see cref="gate"/> and have stopped any channel.</summary>
        /// <param name="paused">Opens the channel paused, before anything is queued, so not a word is spoken.</param>
        private async Task BeginAsync (int spine, int chunk, int sentence, CancellationToken ct, bool paused = false)
        {
            var plan = await PlanAsync (spine, ct).ConfigureAwait (false);
            var current = engine.OpenChannel (Settings, language);
            if (paused)
                current.Pause ();
            var mine = Interlocked.Increment (ref generation);
            current.ChunkStarted += (_, c) => OnProgress (mine, c, 0);
            current.Progress += (_, p) => OnProgress (mine, p.Chunk, p.CharIndex);
            current.ChunkFinished += (_, c) => _ = OnChunkFinishedAsync (mine, c);
            current.Failed += (_, ex) => _ = StopWithAsync (TtsStopReason.Error, ex.Message, mine);
            lock (state_gate) {
                channel = current;
                exhausted = false;
                next = (spine, chunk);
                speaking_chunk = null;
                started_at = (spine, chunk, sentence);
            }
            if (chunk >= plan.Count) {
                // Past the chapter's last text: start at the next one.
                await FillAsync (mine, ct, firstSlice: null).ConfigureAwait (false);
                return;
            }
            await FillAsync (mine, ct, firstSlice: plan[chunk].Sentences[Math.Clamp (sentence, 0, plan[chunk].Sentences.Count - 1)].Start).ConfigureAwait (false);
        }

        /// <summary>Tops the channel up to <see cref="Lookahead"/> chunks, crossing into following chapters.</summary>
        private async Task FillAsync (int mine, CancellationToken ct, int? firstSlice = null)
        {
            while (true) {
                ISpeechChannel? current;
                (int Spine, int Chunk) at;
                lock (state_gate) {
                    current = channel;
                    at = next;
                    if (current is null || mine != generation || exhausted || current.Pending >= Lookahead)
                        return;
                }
                var plan = await PlanAsync (at.Spine, ct).ConfigureAwait (false);
                if (at.Chunk >= plan.Count) {
                    if (SleepAtChapterEnd || next_chapter (at.Spine) is not { } following) {
                        lock (state_gate)
                            exhausted = true;
                        return;
                    }
                    lock (state_gate)
                        next = (following, 0);
                    continue;
                }
                var chunk = plan[at.Chunk];
                if (firstSlice is { } slice) {
                    chunk = chunk.Slice (slice);
                    firstSlice = null;
                }
                lock (state_gate) {
                    if (mine != generation)
                        return;
                    next = (at.Spine, at.Chunk + 1);
                }
                current.Enqueue (chunk);
            }
        }

        private void OnProgress (int mine, SpeechChunk chunk, int charIndex)
        {
            if (mine != generation)
                return;
            lock (state_gate) {
                speaking_chunk = chunk;
                speaking_char = charIndex;
            }
            var sentence = chunk.Sentences[chunk.SentenceAt (charIndex)];
            var (wordStart, wordEnd) = chunk.WordAt (charIndex);
            if (wordStart >= chunk.Text.Length)
                return;
            Speaking?.Invoke (this, new SpokenRange (chunk.SpineIndex,
                chunk.PositionAt (sentence.Start), chunk.PositionAfter (sentence.End),
                chunk.PositionAt (wordStart), chunk.PositionAfter (wordEnd)));
        }

        private async Task OnChunkFinishedAsync (int mine, SpeechChunk chunk)
        {
            if (mine != generation)
                return;
            await FillAsync (mine, CancellationToken.None).ConfigureAwait (false);
            bool done;
            lock (state_gate)
                done = mine == generation && exhausted && channel?.Pending == 0;
            if (done)
                await StopWithAsync (SleepAtChapterEnd ? TtsStopReason.SleepTimer : TtsStopReason.EndOfBook, null, mine).ConfigureAwait (false);
        }

        private async Task StopWithAsync (TtsStopReason reason, string? message = null, int? only = null)
        {
            await gate.WaitAsync ().ConfigureAwait (false);
            try {
                if (only is { } g && g != generation || channel is null)
                    return;
                await StopCoreAsync ().ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
            Stopped?.Invoke (this, new TtsStoppedEventArgs (reason, message));
        }

        private async Task StopCoreAsync ()
        {
            ISpeechChannel? old;
            lock (state_gate) {
                old = channel;
                channel = null;
                Interlocked.Increment (ref generation);
            }
            if (old is not null)
                await old.DisposeAsync ().ConfigureAwait (false);
        }

        /// <summary>The sentence being spoken: chapter, planned chunk index, sentence index in it.</summary>
        private (int Spine, int Chunk, int Sentence)? CurrentSentence ()
        {
            lock (state_gate) {
                if (speaking_chunk is not { } chunk)
                    return channel is null ? null : started_at;
                // A sliced chunk counts characters from its slice; the plan's chunk from its start.
                var planned = plans.TryGetValue (chunk.SpineIndex, out var plan) ? plan[chunk.Index] : chunk;
                var sentence = planned.SentenceAt (speaking_char + chunk.SliceStart);
                return (chunk.SpineIndex, chunk.Index, sentence);
            }
        }
    }
}
