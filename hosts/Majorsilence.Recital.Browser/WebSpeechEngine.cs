using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Majorsilence.Recital.Tts;

namespace Majorsilence.Recital.Browser
{
    /// <summary>
    /// The browser's own voices (the Web Speech API's speechSynthesis): no download, no account, and word
    /// boundaries for the voices that report them. The JavaScript half is in wwwroot/main.js.
    /// </summary>
    [SupportedOSPlatform ("browser")]
    public sealed partial class WebSpeechEngine : ITtsEngine
    {
        private const string Module = "recital-speech";
        private static readonly Dictionary<int, Channel> channels = [];
        private static int next_channel;

        public string Id => "web";
        public string Name => "Browser voices";

        // Chrome stops an utterance that runs much past 15 seconds, so chunks stay a few sentences long.
        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: true, MaxChunkChars: 300);

        public static bool IsAvailable => Available ();

        public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            var json = await Voices ().WaitAsync (ct).ConfigureAwait (false);
            return JsonSerializer.Deserialize (json, WebSpeechJson.Default.WebVoiceArray) is { } voices
                ? [.. voices.Select (v => new TtsVoice (v.Id, v.Name, string.IsNullOrEmpty (v.Lang) ? null : v.Lang))]
                : [];
        }

        public ISpeechChannel OpenChannel (TtsSettings settings, string? language)
        {
            var channel = new Channel (++next_channel, settings, language);
            channels[channel.Id] = channel;
            return channel;
        }

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;

        [JSImport ("available", Module)]
        private static partial bool Available ();

        [JSImport ("voices", Module)]
        private static partial Task<string> Voices ();

        [JSImport ("speak", Module)]
        private static partial void Speak (int channel, int chunk, string text, string language, string voice, double rate);

        [JSImport ("pause", Module)]
        private static partial void PauseAll ();

        [JSImport ("resume", Module)]
        private static partial void ResumeAll ();

        [JSImport ("cancel", Module)]
        private static partial void CancelAll ();

        /// <summary>An utterance event from main.js: start, word (with the character it is at), end or error.</summary>
        [JSExport]
        internal static void OnEvent (int channel, int chunk, string kind, int charIndex)
        {
            if (channels.TryGetValue (channel, out var target))
                target.OnEvent (chunk, kind, charIndex);
        }

        private sealed class Channel (int id, TtsSettings settings, string? language) : ISpeechChannel
        {
            private readonly List<(int Token, SpeechChunk Chunk)> queued = [];
            private int next_token;
            private bool stopped;

            public int Id => id;
            public int Pending => queued.Count;
            public bool IsPaused { get; private set; }

            public event EventHandler<SpeechChunk>? ChunkStarted;
            public event EventHandler<SpeechProgress>? Progress;
            public event EventHandler<SpeechChunk>? ChunkFinished;
            public event EventHandler<Exception>? Failed;

            public void Enqueue (SpeechChunk chunk)
            {
                if (stopped)
                    return;
                var token = ++next_token;
                queued.Add ((token, chunk));
                // The browser queues utterances itself and speaks them back to back.
                Speak (id, token, chunk.Text, language ?? string.Empty, settings.VoiceId ?? string.Empty, settings.ClampedRate);
            }

            internal void OnEvent (int token, string kind, int charIndex)
            {
                var index = queued.FindIndex (q => q.Token == token);
                if (stopped || index < 0)
                    return;
                var chunk = queued[index].Chunk;
                switch (kind) {
                case "start":
                    ChunkStarted?.Invoke (this, chunk);
                    break;
                case "word":
                    if (charIndex >= 0 && charIndex < chunk.Text.Length)
                        Progress?.Invoke (this, new SpeechProgress (chunk, charIndex));
                    break;
                case "end":
                    queued.RemoveAt (index);
                    ChunkFinished?.Invoke (this, chunk);
                    break;
                default:
                    stopped = true;
                    queued.Clear ();
                    Failed?.Invoke (this, new InvalidOperationException ($"The browser could not speak: {kind}."));
                    break;
                }
            }

            public void Pause ()
            {
                IsPaused = true;
                PauseAll ();
            }

            public void Unpause ()
            {
                IsPaused = false;
                ResumeAll ();
            }

            public Task StopAsync ()
            {
                if (!stopped) {
                    stopped = true;
                    queued.Clear ();
                    channels.Remove (id);
                    CancelAll ();
                }
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync () => await StopAsync ().ConfigureAwait (false);
        }
    }

    internal sealed record WebVoice (string Id, string Name, string Lang);

    [System.Text.Json.Serialization.JsonSourceGenerationOptions (PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
    [System.Text.Json.Serialization.JsonSerializable (typeof (WebVoice[]))]
    internal sealed partial class WebSpeechJson : System.Text.Json.Serialization.JsonSerializerContext;
}
