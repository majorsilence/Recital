using Android.Content;
using Android.OS;
using Android.Speech.Tts;
using Majorsilence.Recital.Tts;

using AndroidVoice = Android.Speech.Tts.Voice;
using JavaLocale = Java.Util.Locale;

namespace Majorsilence.Recital.Android
{
    /// <summary>
    /// The device's own voices (Android TextToSpeech): built for the phone, so they start at once and cost next to
    /// no battery. Only voices installed on the device are offered: a voice that needs the network would send the
    /// book's text to a server the user never chose.
    /// </summary>
    /// <remarks>
    /// Android speaks queued utterances back to back and reports each word as it starts (API 26+). It cannot pause,
    /// so pausing stops, and resuming speaks the chunk again from the last word heard.
    /// </remarks>
    public sealed class AndroidSpeechEngine : ITtsEngine
    {
        private readonly Context context;
        private readonly Lazy<Task<TextToSpeech>> speech;
        private readonly Listener listener = new ();
        private int next_channel;

        public AndroidSpeechEngine (Context context)
        {
            this.context = context;
            speech = new (StartAsync);
        }

        public string Id => "android";
        public string Name => "Device voices";

        // Neural voices plan intonation across what they are given, and Android takes up to 4000 characters.
        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: OperatingSystem.IsAndroidVersionAtLeast (26), MaxChunkChars: 1000);

        private Task<TextToSpeech> StartAsync ()
        {
            var ready = new TaskCompletionSource<TextToSpeech> (TaskCreationOptions.RunContinuationsAsynchronously);
            TextToSpeech? tts = null;
            tts = new TextToSpeech (context, new InitListener (status => {
                if (status == OperationResult.Success && tts is not null) {
                    tts.SetOnUtteranceProgressListener (listener);
                    ready.TrySetResult (tts);
                } else {
                    ready.TrySetException (new InvalidOperationException ("This device's text-to-speech service did not start."));
                }
            }));
            return ready.Task;
        }

        public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            var tts = await speech.Value.WaitAsync (ct).ConfigureAwait (false);
            return [.. LocalVoices (tts)
                .OrderBy (v => v.Locale?.ToLanguageTag (), StringComparer.Ordinal).ThenByDescending (v => v.Quality)
                .Select (v => new TtsVoice (v.Name!, DisplayName (v), v.Locale?.ToLanguageTag ()))];
        }

        private static IEnumerable<AndroidVoice> LocalVoices (TextToSpeech tts)
            => (tts.Voices ?? []).Where (v => v.Name is { Length: > 0 } && !v.IsNetworkConnectionRequired
                && !(v.Features?.Contains (TextToSpeech.Engine.KeyFeatureNotInstalled) ?? false));

        // "en-us-x-sfg-local" says little: show the language, and the voice's own short code to tell them apart.
        private static string DisplayName (AndroidVoice voice)
        {
            var name = voice.Name ?? "";
            var language = voice.Locale?.DisplayName ?? name;
            var parts = name.Split ('-');
            var code = parts.Length >= 4 && parts[2] == "x" ? parts[3] : null;
            return code is null ? language : $"{language} ({code})";
        }

        /// <summary>The voice to use: the one asked for if installed, else the best installed one for the language.</summary>
        private static AndroidVoice? Choose (TextToSpeech tts, string? voiceId, string? language)
        {
            var voices = LocalVoices (tts).ToList ();
            if (voiceId is not null && voices.FirstOrDefault (v => v.Name == voiceId) is { } chosen)
                return chosen;
            var wanted = language is { Length: > 0 } ? JavaLocale.ForLanguageTag (language) : JavaLocale.Default;
            return voices.Where (v => v.Locale?.Language == wanted?.Language)
                .OrderByDescending (v => v.Locale?.Country == wanted?.Country).ThenByDescending (v => v.Quality).ThenBy (v => v.Latency)
                .FirstOrDefault ();
        }

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language)
            => new Channel (this, Interlocked.Increment (ref next_channel), settings, language);

        public async ValueTask DisposeAsync ()
        {
            if (speech.IsValueCreated) {
                try {
                    var tts = await speech.Value.ConfigureAwait (false);
                    tts.Stop ();
                    tts.Shutdown ();
                } catch (InvalidOperationException) {
                    // It never started.
                }
            }
        }

        private sealed class InitListener (Action<OperationResult> done) : Java.Lang.Object, TextToSpeech.IOnInitListener
        {
            public void OnInit (OperationResult status) => done (status);
        }

        /// <summary>Routes the service's callbacks (on a binder thread) to the channel named in the utterance id.</summary>
        private sealed class Listener : UtteranceProgressListener
        {
            private readonly Dictionary<int, Channel> channels = [];

            public void Add (Channel channel)
            {
                lock (channels)
                    channels[channel.Id] = channel;
            }

            public void Remove (Channel channel)
            {
                lock (channels)
                    channels.Remove (channel.Id);
            }

            private void Route (string? utteranceId, Action<Channel, Utterance> action)
            {
                if (Utterance.Parse (utteranceId) is not { } utterance)
                    return;
                Channel? channel;
                lock (channels)
                    channels.TryGetValue (utterance.Channel, out channel);
                if (channel is not null)
                    action (channel, utterance);
            }

            public override void OnStart (string? utteranceId) => Route (utteranceId, (c, u) => c.OnStart (u));
            public override void OnDone (string? utteranceId) => Route (utteranceId, (c, u) => c.OnDone (u));
            public override void OnRangeStart (string? utteranceId, int start, int end, int frame) => Route (utteranceId, (c, u) => c.OnWord (u, start));
            public override void OnStop (string? utteranceId, bool interrupted) { }

            [Obsolete ("Android's own abstract member; the overload with an error code is also handled.")]
            public override void OnError (string? utteranceId) => Route (utteranceId, (c, u) => c.OnError (u, 0));

            public override void OnError (string? utteranceId, [global::Android.Runtime.GeneratedEnum] TextToSpeechError errorCode)
                => Route (utteranceId, (c, u) => c.OnError (u, (int) errorCode));
        }

        /// <summary>"channel:generation:token:offset": which chunk, from which character (after a pause).</summary>
        private readonly record struct Utterance (int Channel, int Generation, int Token, int Offset)
        {
            public override string ToString () => $"{Channel}:{Generation}:{Token}:{Offset}";

            public static Utterance? Parse (string? id)
            {
                var parts = id?.Split (':');
                return parts is { Length: 4 } && int.TryParse (parts[0], out var channel) && int.TryParse (parts[1], out var generation)
                    && int.TryParse (parts[2], out var token) && int.TryParse (parts[3], out var offset)
                    ? new Utterance (channel, generation, token, offset)
                    : null;
            }
        }

        private sealed class Channel (AndroidSpeechEngine engine, int id, TtsSettings settings, string? language) : ISpeechChannel
        {
            private sealed class Entry (int token, SpeechChunk chunk)
            {
                public int Token { get; } = token;
                public SpeechChunk Chunk { get; } = chunk;
                public bool Started;
                public int ResumeAt;   // the last word heard, to speak again from after a pause
            }

            private readonly object gate = new ();
            private readonly List<Entry> queued = [];
            private int next_token;
            private int generation;
            private bool stopped;
            private bool paused;
            private bool registered;

            public int Id => id;

            public int Pending {
                get {
                    lock (gate)
                        return queued.Count;
                }
            }

            public bool IsPaused => paused;

            public event EventHandler<SpeechChunk>? ChunkStarted;
            public event EventHandler<SpeechProgress>? Progress;
            public event EventHandler<SpeechChunk>? ChunkFinished;
            public event EventHandler<Exception>? Failed;

            public void Enqueue (SpeechChunk chunk)
            {
                Entry entry;
                lock (gate) {
                    if (stopped)
                        return;
                    entry = new Entry (++next_token, chunk);
                    queued.Add (entry);
                    if (paused)
                        return;   // spoken on Unpause
                }
                _ = SpeakAsync ([entry]);
            }

            private async Task SpeakAsync (IReadOnlyList<Entry> entries)
            {
                try {
                    var tts = await engine.speech.Value.ConfigureAwait (false);
                    if (!registered) {
                        engine.listener.Add (this);
                        registered = true;
                    }
                    lock (gate) {
                        if (stopped || paused)
                            return;
                        if (Choose (tts, settings.VoiceId, language) is { } voice)
                            tts.SetVoice (voice);
                        tts.SetSpeechRate ((float) settings.ClampedRate);
                        foreach (var entry in entries) {
                            var offset = Math.Clamp (entry.ResumeAt, 0, entry.Chunk.Text.Length);
                            var utterance = new Utterance (id, generation, entry.Token, offset);
                            var result = tts.Speak (entry.Chunk.Text[offset..], QueueMode.Add, Bundle.Empty, utterance.ToString ());
                            if (result != OperationResult.Success)
                                throw new InvalidOperationException ("This device's text-to-speech service would not take the text.");
                        }
                    }
                } catch (Exception ex) when (ex is InvalidOperationException or Java.Lang.Exception) {
                    Fail (ex);
                }
            }

            // A callback for an utterance flushed by a pause or a stop belongs to an old generation: ignored.
            private Entry? Current (Utterance utterance)
            {
                lock (gate)
                    return stopped || utterance.Generation != generation ? null : queued.FirstOrDefault (e => e.Token == utterance.Token);
            }

            internal void OnStart (Utterance utterance)
            {
                if (Current (utterance) is not { } entry)
                    return;
                var first = !entry.Started;
                entry.Started = true;
                if (first)
                    ChunkStarted?.Invoke (this, entry.Chunk);
            }

            internal void OnWord (Utterance utterance, int start)
            {
                if (Current (utterance) is not { } entry)
                    return;
                var index = utterance.Offset + start;
                if (index < 0 || index >= entry.Chunk.Text.Length)
                    return;
                entry.ResumeAt = index;
                Progress?.Invoke (this, new SpeechProgress (entry.Chunk, index));
            }

            internal void OnDone (Utterance utterance)
            {
                if (Current (utterance) is not { } entry)
                    return;
                lock (gate)
                    queued.Remove (entry);
                ChunkFinished?.Invoke (this, entry.Chunk);
            }

            internal void OnError (Utterance utterance, int code)
            {
                if (Current (utterance) is null)
                    return;
                Fail (new InvalidOperationException ($"This device's text-to-speech service failed (error {code})."));
            }

            private void Fail (Exception ex)
            {
                lock (gate) {
                    if (stopped)
                        return;
                    stopped = true;
                    queued.Clear ();
                }
                engine.listener.Remove (this);
                Failed?.Invoke (this, ex);
            }

            public void Pause ()
            {
                lock (gate) {
                    if (paused || stopped)
                        return;
                    paused = true;
                    generation++;
                }
                if (engine.speech.IsValueCreated && engine.speech.Value.IsCompletedSuccessfully)
                    engine.speech.Value.Result.Stop ();
            }

            public void Unpause ()
            {
                List<Entry> again;
                lock (gate) {
                    if (!paused || stopped)
                        return;
                    paused = false;
                    again = [.. queued];
                }
                _ = SpeakAsync (again);
            }

            public Task StopAsync ()
            {
                lock (gate) {
                    if (stopped)
                        return Task.CompletedTask;
                    stopped = true;
                    generation++;
                    queued.Clear ();
                }
                engine.listener.Remove (this);
                if (engine.speech.IsValueCreated && engine.speech.Value.IsCompletedSuccessfully)
                    engine.speech.Value.Result.Stop ();
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync () => await StopAsync ().ConfigureAwait (false);
        }
    }
}
