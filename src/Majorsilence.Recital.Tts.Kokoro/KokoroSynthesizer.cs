using System.Buffers.Binary;
using System.Text.Json;
using Majorsilence.Recital.Tts.Kokoro.Phonemes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Majorsilence.Recital.Tts.Kokoro
{
    /// <summary>
    /// Recital's own offline voices (D-031): Kokoro-82M run in-process with ONNX Runtime. Nothing leaves the
    /// device and nothing needs installing; the model and voices ship with the app (see <see cref="FindFolder"/>).
    /// </summary>
    /// <remarks>
    /// A chunk is spoken sentence by sentence, so each sentence's start is exact; words inside a sentence are
    /// placed by their share of its phonemes. The model reads at most 510 phonemes at once, so a longer sentence
    /// is split at its last pause (or space) before that. When little audio is waiting to play (the listener
    /// just pressed Listen), a sentence is split sooner, at a pause, so the first sound comes in well under a
    /// second; each part may then be longer, as the audio ahead grows. Chunks are made one at a time, in reading
    /// order, whatever order they are asked for in.
    /// </remarks>
    public sealed class KokoroSynthesizer : IStreamingSpeechSynthesizer, IPreparable
    {
        public const int SampleRate = 24000;
        public const string DefaultVoice = "af_heart";
        private const int MaxPhonemes = 510;
        private const int StyleWidth = 256;
        private const int MinPiece = 32;   // phonemes: about two and a half seconds of speech, a short clause
        private static readonly PcmFormat Format = new (SampleRate, 1, 16);

        private readonly string folder;
        private readonly OrderedGate gate = new ();
        // Phonemes made per second of work, measured: sizes a part to be ready before the audio ahead runs out.
        private double phonemes_per_second = 40;
        private readonly Dictionary<string, float[]> voices = new (StringComparer.Ordinal);
        private readonly Lazy<Task<(InferenceSession Session, EnglishPhonemizer Phonemizer, Dictionary<string, long> Vocab)>> loaded;

        /// <param name="folder">Holds <c>model.onnx</c> and <c>voices/*.bin</c>.</param>
        public KokoroSynthesizer (string folder)
        {
            this.folder = folder;
            loaded = new (() => Task.Run (Load));
        }

        public string Id => "kokoro";
        public string Name => "Recital voices (offline)";

        // Short chunks: the first is ready to play after about a quarter of its length on a desktop CPU.
        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: true, MaxChunkChars: 240);

        /// <summary>The bundled voices' folder beside the app (<c>kokoro/</c>), or null when this build has none.</summary>
        public static string? FindFolder (string? baseDirectory = null)
        {
            var candidate = Path.Combine (baseDirectory ?? AppContext.BaseDirectory, "kokoro");
            return IsVoiceFolder (candidate) ? candidate : null;
        }

        public static bool IsVoiceFolder (string folder)
            => File.Exists (Path.Combine (folder, "model.onnx")) && Directory.Exists (Path.Combine (folder, "voices"))
               && Directory.EnumerateFiles (Path.Combine (folder, "voices"), "*.bin").Any ();

        /// <summary>Loads the model and dictionaries now, in the background, so the first Listen does not wait for them.</summary>
        public Task PrepareAsync (CancellationToken ct = default) => loaded.Value.WaitAsync (ct);

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TtsVoice>> ([.. Directory.EnumerateFiles (Path.Combine (folder, "voices"), "*.bin")
                .Select (Path.GetFileNameWithoutExtension).OfType<string> ()
                .OrderBy (id => id == DefaultVoice ? 0 : 1).ThenBy (id => id, StringComparer.Ordinal)
                .Select (id => new TtsVoice (id, DisplayName (id), Language (id)))]);

        /// <summary>"af_heart" → "Heart (US English, female)". The first letter is the accent, the second the voice's gender.</summary>
        public static string DisplayName (string id)
        {
            if (id.Length < 4 || id[2] != '_')
                return id;
            var accent = id[0] switch { 'a' => "US English", 'b' => "UK English", _ => "" };
            var gender = id[1] switch { 'f' => "female", 'm' => "male", _ => "" };
            var name = char.ToUpperInvariant (id[3]) + id[4..];
            return $"{name} ({string.Join (", ", new[] { accent, gender }.Where (s => s.Length > 0))})";
        }

        private static string? Language (string id) => id.Length > 0 ? id[0] switch { 'a' => "en-US", 'b' => "en-GB", _ => null } : null;

        public async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var parts = new List<SynthesizedAudio> ();
            await foreach (var part in SynthesizePartsAsync (chunk, settings, language, () => TimeSpan.MaxValue, ct).ConfigureAwait (false))
                parts.Add (part);
            return SynthesizedAudio.Join (Format, parts);
        }

        public async IAsyncEnumerable<SynthesizedAudio> SynthesizePartsAsync (SpeechChunk chunk, TtsSettings settings, string? language, Func<TimeSpan> ahead,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var (session, phonemizer, vocab) = await loaded.Value.ConfigureAwait (false);
            var voice = await VoiceAsync (settings.VoiceId ?? DefaultVoice, ct).ConfigureAwait (false);
            var speed = (float) settings.ClampedRate;
            var starts = chunk.WordStarts ().ToArray ();
            var lastWord = -1;

            var sentences = chunk.Sentences.Count > 0 ? chunk.Sentences : [new SpeechSentence (0, chunk.Text.Length)];
            using var turn = await gate.EnterAsync ((chunk.SpineIndex, chunk.Index), ct).ConfigureAwait (false);
            foreach (var sentence in sentences) {
                var text = chunk.Text[sentence.Start..sentence.End];
                if (string.IsNullOrWhiteSpace (text))
                    continue;
                var phonemized = phonemizer.Phonemize (text);
                var phonemes = phonemized.Phonemes;
                var start = 0;
                while (start < phonemes.Length) {
                    ct.ThrowIfCancellationRequested ();
                    await turn.YieldAsync (ct).ConfigureAwait (false);
                    var end = PieceEnd (phonemes, start, Budget (ahead ()));
                    var ids = Tokens (phonemes[start..end], vocab);
                    if (ids.Count > 2) {
                        var watch = System.Diagnostics.Stopwatch.StartNew ();
                        var audio = await Task.Run (() => Run (session, ids, voice, speed), ct).ConfigureAwait (false);
                        Measure (ids.Count, watch.Elapsed);
                        var words = new List<WordTiming> ();
                        var length = Math.Max (1, end - start);
                        foreach (var (charIndex, phonemeIndex) in phonemized.Words)
                            if (phonemeIndex >= start && phonemeIndex < end
                                && AtWordStart (starts, sentence.Start + charIndex, ref lastWord) is { } word)
                                words.Add (new WordTiming (word, TimeSpan.FromSeconds (audio.Length * (double) (phonemeIndex - start) / length / SampleRate)));
                        yield return new SynthesizedAudio (Format, ToPcm (audio), words);
                    }
                    start = end;
                }
            }
        }

        // The most phonemes worth making before the audio ahead runs out, with room to spare.
        private int Budget (TimeSpan ahead)
        {
            if (ahead >= TimeSpan.FromMinutes (1))
                return MaxPhonemes;
            var budget = ahead.TotalSeconds * Volatile.Read (ref phonemes_per_second) / 2;
            return (int) Math.Clamp (budget, MinPiece, MaxPhonemes);
        }

        private void Measure (int phonemes, TimeSpan elapsed)
        {
            if (elapsed <= TimeSpan.Zero)
                return;
            var rate = phonemes / elapsed.TotalSeconds;
            var old = Volatile.Read (ref phonemes_per_second);
            Volatile.Write (ref phonemes_per_second, old * 0.7 + rate * 0.3);
        }

        // The chunk's words start after whitespace (an opening quote is part of its word); the phonemizer's start at
        // letters, and split hyphenated words. Each time goes to the word that holds it, the first time winning.
        private static int? AtWordStart (int[] starts, int charIndex, ref int last)
        {
            var i = Array.BinarySearch (starts, charIndex);
            if (i < 0)
                i = ~i - 1;
            if (i < 0 || i <= last)
                return null;
            last = i;
            return starts[i];
        }

        /// <summary>
        /// Where the piece from <paramref name="start"/> ends: all the rest if it fits the budget (or nearly), else
        /// after the last pause in the budget, else the last space; never more than the model takes.
        /// </summary>
        internal static int PieceEnd (string phonemes, int start, int budget)
        {
            var rest = phonemes.Length - start;
            // A short tail sounds clipped on its own: take it with this piece if the model allows.
            if (rest <= budget + MinPiece && rest <= MaxPhonemes)
                return phonemes.Length;
            var window = phonemes.AsSpan (start, Math.Min (budget, rest));
            var cut = window.LastIndexOfAny (",;:—…");
            if (cut < window.Length / 3) {
                // No pause early enough: a little past the budget is better than mid-phrase, if a pause is near.
                var further = phonemes.AsSpan (start, Math.Min (Math.Min (budget * 3 / 2, MaxPhonemes), rest));
                var next = further[window.Length..].IndexOfAny (",;:—…");
                cut = next >= 0 ? window.Length + next : window.LastIndexOf (' ');
            }
            if (cut <= 0)
                cut = window.Length - 1;
            return start + cut + 1;
        }

        // Pad, the phonemes the model knows, pad.
        private static List<long> Tokens (string phonemes, Dictionary<string, long> vocab)
        {
            var ids = new List<long> (phonemes.Length + 2) { 0 };
            foreach (var rune in phonemes.EnumerateRunes ())
                if (vocab.TryGetValue (rune.ToString (), out var id))
                    ids.Add (id);
            ids.Add (0);
            return ids;
        }

        private static float[] Run (InferenceSession session, List<long> ids, float[] voice, float speed)
        {
            // A voice holds one style vector per input length.
            var rows = voice.Length / StyleWidth;
            var row = Math.Clamp (ids.Count - 2, 0, rows - 1);
            var style = voice.AsSpan (row * StyleWidth, StyleWidth).ToArray ();
            var inputs = new List<NamedOnnxValue> {
                NamedOnnxValue.CreateFromTensor ("input_ids", new DenseTensor<long> (ids.ToArray (), [1, ids.Count])),
                NamedOnnxValue.CreateFromTensor ("style", new DenseTensor<float> (style, [1, StyleWidth])),
                NamedOnnxValue.CreateFromTensor ("speed", new DenseTensor<float> (new[] { speed }, [1])),
            };
            using var results = session.Run (inputs);
            return results[0].AsTensor<float> () is DenseTensor<float> dense ? dense.Buffer.ToArray () : [.. results[0].AsEnumerable<float> ()];
        }

        private static byte[] ToPcm (float[] samples)
        {
            var pcm = new byte[samples.Length * 2];
            for (var i = 0; i < samples.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian (pcm.AsSpan (i * 2), (short) Math.Clamp (samples[i] * 32767f, short.MinValue, short.MaxValue));
            return pcm;
        }

        private async Task<float[]> VoiceAsync (string id, CancellationToken ct)
        {
            lock (voices)
                if (voices.TryGetValue (id, out var cached))
                    return cached;
            var path = Path.Combine (folder, "voices", Path.GetFileName (id) + ".bin");
            if (!File.Exists (path))
                path = Path.Combine (folder, "voices", DefaultVoice + ".bin");
            var bytes = await File.ReadAllBytesAsync (path, ct).ConfigureAwait (false);
            var values = new float[bytes.Length / 4];
            for (var i = 0; i < values.Length; i++)
                values[i] = BinaryPrimitives.ReadSingleLittleEndian (bytes.AsSpan (i * 4));
            lock (voices)
                voices[id] = values;
            return values;
        }

        private (InferenceSession, EnglishPhonemizer, Dictionary<string, long>) Load ()
        {
            // A few threads, and no spinning: by default ONNX Runtime takes every core and busy-waits between steps,
            // burning about 4 s of CPU per second of speech. Three threads without spinning measured as fast or
            // faster on a 12-thread Ryzen (more so with other work running) for about 0.9 s of CPU.
            using var options = new SessionOptions {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Clamp (Environment.ProcessorCount / 4, 2, 4),
            };
            options.AddSessionConfigEntry ("session.intra_op.allow_spinning", "0");
            var session = new InferenceSession (Path.Combine (folder, "model.onnx"), options);
            using var vocabStream = typeof (KokoroSynthesizer).Assembly.GetManifestResourceStream ("Majorsilence.Recital.Tts.Kokoro.Data.kokoro_vocab.json")
                ?? throw new InvalidOperationException ("Missing resource kokoro_vocab.json.");
            var vocab = JsonSerializer.Deserialize<Dictionary<string, long>> (vocabStream) ?? [];
            return (session, EnglishPhonemizer.LoadAmerican (), vocab);
        }

        public async ValueTask DisposeAsync ()
        {
            // A run still going (a stopped channel does not wait for its chunks) must finish before its session goes.
            await gate.CloseAsync ().ConfigureAwait (false);
            if (loaded.IsValueCreated) {
                try {
                    var (session, _, _) = await loaded.Value.ConfigureAwait (false);
                    session.Dispose ();
                } catch (OnnxRuntimeException) {
                    // It never loaded; nothing to release.
                }
            }
        }

        /// <summary>
        /// One at a time, the lowest key first: when the model frees up, the earliest chunk waiting goes next, and a
        /// chunk between parts hands over to an earlier one that has come to wait, so a listener never waits for a
        /// later paragraph that happened to ask first.
        /// </summary>
        private sealed class OrderedGate
        {
            private readonly SortedDictionary<((int, int) Key, long Ticket), TaskCompletionSource> waiting = [];
            private TaskCompletionSource? idle;
            private long tickets;
            private bool busy;
            private bool closed;

            public async Task<Turn> EnterAsync ((int, int) key, CancellationToken ct)
            {
                await WaitAsync (key, ct).ConfigureAwait (false);
                return new Turn (this, key);
            }

            private Task WaitAsync ((int, int) key, CancellationToken ct)
            {
                TaskCompletionSource entry;
                ((int, int), long) id;
                lock (waiting) {
                    ObjectDisposedException.ThrowIf (closed, this);
                    if (!busy) {
                        busy = true;
                        return Task.CompletedTask;
                    }
                    id = (key, tickets++);
                    entry = new (TaskCreationOptions.RunContinuationsAsynchronously);
                    waiting.Add (id, entry);
                }
                return Wait ();

                async Task Wait ()
                {
                    using (ct.Register (() => {
                        lock (waiting)
                            if (!waiting.Remove (id))
                                return;
                        entry.TrySetCanceled (ct);
                    }))
                        await entry.Task.ConfigureAwait (false);
                }
            }

            // Hands the gate to the earliest waiter, if any; under the lock.
            private void Pass ()
            {
                if (waiting.Count == 0) {
                    busy = false;
                    idle?.TrySetResult ();
                    return;
                }
                var first = waiting.First ();
                waiting.Remove (first.Key);
                first.Value.SetResult ();
            }

            private bool EarlierWaiting ((int, int) key)
            {
                lock (waiting)
                    return waiting.Count > 0 && Comparer<(int, int)>.Default.Compare (waiting.First ().Key.Key, key) < 0;
            }

            /// <summary>Turns away everyone waiting, and completes once the one inside has left.</summary>
            public Task CloseAsync ()
            {
                lock (waiting) {
                    closed = true;
                    foreach (var entry in waiting.Values)
                        entry.TrySetCanceled ();
                    waiting.Clear ();
                    if (!busy)
                        return Task.CompletedTask;
                    idle ??= new (TaskCreationOptions.RunContinuationsAsynchronously);
                    return idle.Task;
                }
            }

            public sealed class Turn (OrderedGate gate, (int, int) key) : IDisposable
            {
                private bool inside = true;

                /// <summary>Between parts: if an earlier chunk is waiting, lets it go first, then carries on.</summary>
                public async Task YieldAsync (CancellationToken ct)
                {
                    if (!inside || !gate.EarlierWaiting (key))
                        return;
                    lock (gate.waiting) {
                        inside = false;
                        gate.Pass ();
                    }
                    await gate.WaitAsync (key, ct).ConfigureAwait (false);
                    inside = true;
                }

                public void Dispose ()
                {
                    if (!inside)
                        return;
                    inside = false;
                    lock (gate.waiting)
                        gate.Pass ();
                }
            }
        }
    }
}
