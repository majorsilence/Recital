using System.Globalization;
using System.Runtime.Versioning;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// Windows' SAPI voices (System.Speech): SSML in, 16-bit PCM out, and word timings from the SSML marks
    /// (each word's <c>mark</c> reports where in the audio it was reached).
    /// </summary>
    [SupportedOSPlatform ("windows")]
    public sealed class SapiSynthesizer : ISpeechSynthesizer
    {
        private readonly SemaphoreSlim gate = new (1, 1);

        public string Id => "sapi";
        public string Name => "Windows voices";
        public TtsCapabilities Capabilities => new (Ssml: true, WordTimings: true, MaxChunkChars: 2000);

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
            => Task.Run<IReadOnlyList<TtsVoice>> (() => {
                using var synthesizer = new SpeechSynthesizer ();
#pragma warning disable CA1304 // Every installed voice, whatever the user's locale: the culture overload filters by language.
                var installed = synthesizer.GetInstalledVoices ();
#pragma warning restore CA1304
                return installed.Where (v => v.Enabled)
                    .Select (v => new TtsVoice (v.VoiceInfo.Name, v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name)).ToList ();
            }, ct);

        public async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            // SAPI synthesis blocks; one at a time, on a worker thread.
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                return await Task.Run (() => Synthesize (chunk, settings, language), ct).ConfigureAwait (false);
            } finally {
                gate.Release ();
            }
        }

        private static SynthesizedAudio Synthesize (SpeechChunk chunk, TtsSettings settings, string? language)
        {
            var format = PcmFormat.Speech;
            using var synthesizer = new SpeechSynthesizer ();
            if (!string.IsNullOrEmpty (settings.VoiceId))
                synthesizer.SelectVoice (settings.VoiceId);
            // SAPI's rate is -10..10; 10 is about three times normal speed.
            synthesizer.Rate = (int)Math.Round (Math.Clamp (settings.ClampedRate >= 1 ? (settings.ClampedRate - 1) * 5 : (settings.ClampedRate - 1) * 10, -10, 10));
            using var audio = new MemoryStream ();
            synthesizer.SetOutputToAudioStream (audio, new SpeechAudioFormatInfo (format.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var words = new List<WordTiming> ();
            synthesizer.BookmarkReached += (_, e) => {
                if (e.Bookmark.StartsWith ('c') && int.TryParse (e.Bookmark.AsSpan (1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    words.Add (new WordTiming (index, e.AudioPosition));
            };
            synthesizer.SpeakSsml (Ssml.Build (chunk, language, settings.DialogueVoiceId, marks: true));
            return new SynthesizedAudio (format, audio.ToArray (), words);
        }

        public ValueTask DisposeAsync ()
        {
            gate.Dispose ();
            return ValueTask.CompletedTask;
        }
    }
}
