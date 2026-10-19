using AVFoundation;
using Foundation;
using Majorsilence.Recital.Tts;

namespace Majorsilence.Recital.iOS
{
    /// <summary>
    /// The device's own voices (AVSpeechSynthesizer): built into iOS, offline, quick to start and light on the
    /// battery, including the Enhanced and Premium voices the user can download in Settings. Each word is
    /// reported as it starts, and pausing waits for the end of the word.
    /// </summary>
    public sealed class AppleSpeechEngine : ITtsEngine
    {
        private static bool session_ready;

        public string Id => "apple";
        public string Name => "Device voices";

        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: true, MaxChunkChars: 1000);

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TtsVoice>> ([.. AVSpeechSynthesisVoice.GetSpeechVoices ()
                .OrderBy (v => v.Language, StringComparer.Ordinal).ThenByDescending (v => (int) v.Quality).ThenBy (v => v.Name, StringComparer.Ordinal)
                .Select (v => new TtsVoice (v.Identifier, DisplayName (v), v.Language))]);

        private static string DisplayName (AVSpeechSynthesisVoice voice)
        {
            // Quality: 1 default, 2 enhanced, 3 premium (iOS 16).
            var quality = (int) voice.Quality switch { 3 => ", Premium", 2 => ", Enhanced", _ => "" };
            return $"{voice.Name} ({voice.Language}{quality})";
        }

        /// <summary>The voice asked for if installed, else the best installed one for the language.</summary>
        private static AVSpeechSynthesisVoice? Choose (string? voiceId, string? language)
        {
            if (voiceId is not null && AVSpeechSynthesisVoice.FromIdentifier (voiceId) is { } chosen)
                return chosen;
            var wanted = language is { Length: > 0 } ? language : AVSpeechSynthesisVoice.CurrentLanguageCode;
            var prefix = wanted.Split ('-')[0];
            return AVSpeechSynthesisVoice.GetSpeechVoices ()
                .Where (v => v.Language.Split ('-')[0] == prefix)
                .OrderByDescending (v => v.Language == wanted).ThenByDescending (v => (int) v.Quality)
                .FirstOrDefault () ?? AVSpeechSynthesisVoice.FromLanguage (wanted);
        }

        /// <summary>
        /// Recital's rate (1 normal, 0.5 to 3) on Apple's scale (0 to 1, 0.5 normal, not linear): a doubling of
        /// Recital's rate moves Apple's by about half the room left, which sounds close at 1.5x and 2x.
        /// </summary>
        private static float Rate (double rate)
        {
            var normal = AVSpeechUtterance.DefaultSpeechRate;
            var steps = Math.Log2 (rate);
            var value = steps >= 0
                ? normal + (AVSpeechUtterance.MaximumSpeechRate - normal) * steps / Math.Log2 (3)
                : normal + (normal - AVSpeechUtterance.MinimumSpeechRate) * steps / 2;
            return (float) Math.Clamp (value, AVSpeechUtterance.MinimumSpeechRate, AVSpeechUtterance.MaximumSpeechRate);
        }

        // Spoken audio: plays with the silent switch on and, with the audio background mode, with the screen locked.
        private static void PrepareSession ()
        {
            if (session_ready)
                return;
            var session = AVAudioSession.SharedInstance ();
            session.SetCategory (AVAudioSessionCategory.Playback);
            session.SetMode (AVAudioSessionMode.SpokenAudio.GetConstant ()!, out _);
            session.SetActive (true, out _);
            session_ready = true;
        }

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language) => new Channel (settings, language);

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;

        private sealed class Channel : AVSpeechSynthesizerDelegate, ISpeechChannel
        {
            private readonly AVSpeechSynthesizer synthesizer = new ();
            private readonly TtsSettings settings;
            private readonly string? language;
            private readonly object gate = new ();
            private readonly List<(AVSpeechUtterance Utterance, SpeechChunk Chunk)> queued = [];
            // Queued while paused with nothing speaking: the synthesizer ignores a pause then, so they wait here.
            private readonly List<AVSpeechUtterance> held = [];
            private bool stopped;
            private bool paused;

            public Channel (TtsSettings settings, string? language)
            {
                this.settings = settings;
                this.language = language;
                synthesizer.Delegate = this;
            }

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
            public event EventHandler<Exception>? Failed { add { } remove { } }

            public void Enqueue (SpeechChunk chunk)
            {
                PrepareSession ();
                var utterance = new AVSpeechUtterance (chunk.Text) {
                    Voice = Choose (settings.VoiceId, language),
                    Rate = Rate (settings.ClampedRate),
                };
                lock (gate) {
                    if (stopped)
                        return;
                    queued.Add ((utterance, chunk));
                    if (paused && !synthesizer.Speaking) {
                        held.Add (utterance);
                        return;
                    }
                }
                // The synthesizer queues utterances itself and speaks them back to back.
                synthesizer.SpeakUtterance (utterance);
            }

            private SpeechChunk? ChunkOf (AVSpeechUtterance utterance)
            {
                lock (gate)
                    return stopped ? null : queued.FirstOrDefault (q => q.Utterance.Handle == utterance.Handle).Chunk;
            }

            public override void DidStartSpeechUtterance (AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
            {
                if (ChunkOf (utterance) is { } chunk)
                    ChunkStarted?.Invoke (this, chunk);
            }

            public override void WillSpeakRangeOfSpeechString (AVSpeechSynthesizer synthesizer, NSRange characterRange, AVSpeechUtterance utterance)
            {
                // NSString ranges count UTF-16 code units, as .NET strings do.
                if (ChunkOf (utterance) is { } chunk && characterRange.Location >= 0 && characterRange.Location < chunk.Text.Length)
                    Progress?.Invoke (this, new SpeechProgress (chunk, (int) characterRange.Location));
            }

            public override void DidFinishSpeechUtterance (AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
            {
                SpeechChunk? chunk;
                lock (gate) {
                    var index = stopped ? -1 : queued.FindIndex (q => q.Utterance.Handle == utterance.Handle);
                    if (index < 0)
                        return;
                    chunk = queued[index].Chunk;
                    queued.RemoveAt (index);
                }
                ChunkFinished?.Invoke (this, chunk);
            }

            public void Pause ()
            {
                lock (gate)
                    paused = true;
                if (synthesizer.Speaking)
                    synthesizer.PauseSpeaking (AVSpeechBoundary.Word);
            }

            public void Unpause ()
            {
                List<AVSpeechUtterance> waiting;
                lock (gate) {
                    paused = false;
                    waiting = [.. held];
                    held.Clear ();
                }
                if (synthesizer.Paused)
                    synthesizer.ContinueSpeaking ();
                foreach (var utterance in waiting)
                    synthesizer.SpeakUtterance (utterance);
            }

            public Task StopAsync ()
            {
                lock (gate) {
                    if (stopped)
                        return Task.CompletedTask;
                    stopped = true;
                    queued.Clear ();
                    held.Clear ();
                }
                synthesizer.StopSpeaking (AVSpeechBoundary.Immediate);
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync ()
            {
                await StopAsync ().ConfigureAwait (false);
                synthesizer.Delegate = null!;
                synthesizer.Dispose ();
                Dispose ();
            }
        }
    }
}
