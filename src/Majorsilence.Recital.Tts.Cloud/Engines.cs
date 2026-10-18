using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Majorsilence.Recital.Tts.Cloud
{
    /// <summary>
    /// ElevenLabs: the most expressive voices (plan 3.3), with character timings, so highlighting is exact.
    /// Plain text with punctuation (its models read emphasis from context, not SSML); PCM at 22.05 kHz.
    /// </summary>
    public sealed class ElevenLabsSynthesizer (HttpClient http, string apiKey, Uri? baseUri = null) : CloudSynthesizer (http, "ElevenLabs")
    {
        private readonly Uri api = baseUri ?? new Uri ("https://api.elevenlabs.io/v1/");

        public override string Id => "elevenlabs";
        public override string Name => "ElevenLabs";
        public override TtsCapabilities Capabilities => new (Ssml: false, WordTimings: true, MaxChunkChars: 2500);

        public override async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            using var response = await SendAsync (() => Request (HttpMethod.Get, "voices"), ct).ConfigureAwait (false);
            var json = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
            return (json?["voices"]?.AsArray () ?? []).Select (v => new TtsVoice (v!["voice_id"]!.GetValue<string> (), v["name"]?.GetValue<string> () ?? "", v["labels"]?["language"]?.GetValue<string> ())).ToList ();
        }

        public override async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var voice = settings.VoiceId ?? "21m00Tcm4TlvDq8ikWAM"; // "Rachel", in every account's library
            var body = new JsonObject {
                ["text"] = chunk.Text,
                ["model_id"] = "eleven_multilingual_v2",
                ["voice_settings"] = new JsonObject { ["speed"] = Math.Clamp (settings.ClampedRate, 0.7, 1.2) },
            }.ToJsonString ();
            using var response = await SendAsync (() => {
                var request = Request (HttpMethod.Post, $"text-to-speech/{Uri.EscapeDataString (voice)}/with-timestamps?output_format=pcm_22050");
                request.Content = new StringContent (body, Encoding.UTF8, "application/json");
                return request;
            }, ct).ConfigureAwait (false);
            var json = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false))!;
            var pcm = Convert.FromBase64String (json["audio_base64"]!.GetValue<string> ());
            var starts = json["alignment"]?["character_start_times_seconds"]?.AsArray ().Select (n => n!.GetValue<double> ()).ToList () ?? [];
            var format = new PcmFormat (22050, 1, 16);
            // A chunk's pauses are silence the channel adds: they are not sent as text.
            return new SynthesizedAudio (format, pcm, WordsFromCharacters (chunk, starts));
        }

        private HttpRequestMessage Request (HttpMethod method, string path)
        {
            var request = new HttpRequestMessage (method, new Uri (api, path));
            request.Headers.TryAddWithoutValidation ("xi-api-key", apiKey);
            return request;
        }
    }

    /// <summary>
    /// OpenAI's speech models: natural voices with steerable delivery (an instruction says "read as a
    /// narrator"), no timings (progress is interpolated). PCM at 24 kHz.
    /// </summary>
    public sealed class OpenAiSynthesizer (HttpClient http, string apiKey, Uri? baseUri = null) : CloudSynthesizer (http, "OpenAI")
    {
        private static readonly string[] Voices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer"];
        private readonly Uri api = baseUri ?? new Uri ("https://api.openai.com/v1/");

        public override string Id => "openai";
        public override string Name => "OpenAI";
        public override TtsCapabilities Capabilities => new (Ssml: false, WordTimings: false, MaxChunkChars: 4000);

        public override Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TtsVoice>> (Voices.Select (v => new TtsVoice (v, char.ToUpperInvariant (v[0]) + v[1..], null)).ToList ());

        public override async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var instructions = chunk.Kind == ChunkKind.Heading
                ? "Read this chapter heading clearly, as an audiobook narrator would."
                : "Read as an audiobook narrator: natural, warm, with the emphasis and tone the text implies; give quoted speech the speaker's tone.";
            var body = new JsonObject {
                ["model"] = "gpt-4o-mini-tts",
                ["voice"] = settings.VoiceId ?? "fable",
                ["input"] = chunk.Text,
                ["instructions"] = instructions,
                ["response_format"] = "pcm",
                ["speed"] = settings.ClampedRate,
            }.ToJsonString ();
            using var response = await SendAsync (() => {
                var request = new HttpRequestMessage (HttpMethod.Post, new Uri (api, "audio/speech")) {
                    Content = new StringContent (body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue ("Bearer", apiKey);
                return request;
            }, ct).ConfigureAwait (false);
            var pcm = await response.Content.ReadAsByteArrayAsync (ct).ConfigureAwait (false);
            return new SynthesizedAudio (new PcmFormat (24000, 1, 16), pcm[..(pcm.Length - pcm.Length % 2)], []);
        }
    }

    /// <summary>
    /// Azure Speech: SSML with styles and emphasis (plan 3.2), 24 kHz PCM. The REST endpoint reports no word
    /// boundaries (only the SDK's socket does), so progress is interpolated.
    /// </summary>
    public sealed class AzureSynthesizer (HttpClient http, string apiKey, string region, Uri? baseUri = null) : CloudSynthesizer (http, "Azure Speech")
    {
        private readonly Uri api = baseUri ?? new Uri ($"https://{region}.tts.speech.microsoft.com/");

        public override string Id => "azure";
        public override string Name => "Azure Speech";
        public override TtsCapabilities Capabilities => new (Ssml: true, WordTimings: false, MaxChunkChars: 3000);

        public override async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            using var response = await SendAsync (() => Request (HttpMethod.Get, "cognitiveservices/voices/list"), ct).ConfigureAwait (false);
            var json = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
            return (json?.AsArray () ?? []).Select (v => new TtsVoice (v!["ShortName"]!.GetValue<string> (), v["DisplayName"]?.GetValue<string> () ?? "", v["Locale"]?.GetValue<string> ())).ToList ();
        }

        public override async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var voice = settings.VoiceId ?? "en-US-AvaMultilingualNeural";
            var ssml = Ssml.Build (chunk, language ?? "en-US", settings.DialogueVoiceId);
            // Azure wants the voice element around the content, and rate as prosody.
            var inner = ssml[(ssml.IndexOf ('>') + 1)..ssml.LastIndexOf ("</speak>", StringComparison.Ordinal)];
            var rate = ((settings.ClampedRate - 1) * 100).ToString ("+0;-0;0", CultureInfo.InvariantCulture) + "%";
            var body = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"{System.Security.SecurityElement.Escape (language ?? "en-US")}\"><voice name=\"{System.Security.SecurityElement.Escape (voice)}\"><prosody rate=\"{rate}\">{inner}</prosody></voice></speak>";
            using var response = await SendAsync (() => {
                var request = Request (HttpMethod.Post, "cognitiveservices/v1");
                request.Content = new StringContent (body, Encoding.UTF8, "application/ssml+xml");
                request.Headers.TryAddWithoutValidation ("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
                request.Headers.TryAddWithoutValidation ("User-Agent", "Recital");
                return request;
            }, ct).ConfigureAwait (false);
            var (format, pcm) = Wave.Read (await response.Content.ReadAsByteArrayAsync (ct).ConfigureAwait (false));
            return new SynthesizedAudio (format, pcm, []);
        }

        private HttpRequestMessage Request (HttpMethod method, string path)
        {
            var request = new HttpRequestMessage (method, new Uri (api, path));
            request.Headers.TryAddWithoutValidation ("Ocp-Apim-Subscription-Key", apiKey);
            return request;
        }
    }

    /// <summary>
    /// Google Cloud Text-to-Speech: SSML, and timepoints for its marks (v1beta1), so highlighting is exact.
    /// 24 kHz LINEAR16.
    /// </summary>
    public sealed class GoogleSynthesizer (HttpClient http, string apiKey, Uri? baseUri = null) : CloudSynthesizer (http, "Google Cloud Text-to-Speech")
    {
        private readonly Uri api = baseUri ?? new Uri ("https://texttospeech.googleapis.com/v1beta1/");

        public override string Id => "google";
        public override string Name => "Google Cloud voices";
        public override TtsCapabilities Capabilities => new (Ssml: true, WordTimings: true, MaxChunkChars: 2000);

        public override async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            using var response = await SendAsync (() => Request (HttpMethod.Get, "voices"), ct).ConfigureAwait (false);
            var json = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false));
            return (json?["voices"]?.AsArray () ?? []).Select (v => new TtsVoice (v!["name"]!.GetValue<string> (), v["name"]!.GetValue<string> (), v["languageCodes"]?.AsArray ().FirstOrDefault ()?.GetValue<string> ())).ToList ();
        }

        public override async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var languageCode = language ?? "en-US";
            var voice = new JsonObject { ["languageCode"] = settings.VoiceId?[..Math.Min (5, settings.VoiceId.Length)] ?? languageCode };
            if (settings.VoiceId is { } name)
                voice["name"] = name;
            var body = new JsonObject {
                ["input"] = new JsonObject { ["ssml"] = Ssml.Build (chunk, languageCode, settings.DialogueVoiceId, marks: true) },
                ["voice"] = voice,
                ["audioConfig"] = new JsonObject { ["audioEncoding"] = "LINEAR16", ["sampleRateHertz"] = 24000, ["speakingRate"] = settings.ClampedRate },
                ["enableTimePointing"] = new JsonArray ("SSML_MARK"),
            }.ToJsonString ();
            using var response = await SendAsync (() => {
                var request = Request (HttpMethod.Post, "text:synthesize");
                request.Content = new StringContent (body, Encoding.UTF8, "application/json");
                return request;
            }, ct).ConfigureAwait (false);
            var json = JsonNode.Parse (await response.Content.ReadAsStringAsync (ct).ConfigureAwait (false))!;
            var (format, pcm) = Wave.Read (Convert.FromBase64String (json["audioContent"]!.GetValue<string> ()));
            var marks = (json["timepoints"]?.AsArray () ?? []).Select (t => (t!["markName"]!.GetValue<string> (), t["timeSeconds"]!.GetValue<double> ()));
            return new SynthesizedAudio (format, pcm, WordsFromMarks (marks));
        }

        // The key goes in a header, not the URL, so it never shows in a logged or echoed address.
        private HttpRequestMessage Request (HttpMethod method, string path)
        {
            var request = new HttpRequestMessage (method, new Uri (api, path));
            request.Headers.TryAddWithoutValidation ("X-Goog-Api-Key", apiKey);
            return request;
        }
    }
}
