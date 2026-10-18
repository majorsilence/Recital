using System.Text.Json;
using System.Text.Json.Serialization;
using Majorsilence.Recital.Storage;
using Majorsilence.Recital.Tts.Cloud;
using Majorsilence.Recital.Tts.Piper;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// <c>tts.json</c> in local app data: where Piper and its voices are, and the Azure region. Device-specific,
    /// so never synced; and never a key (keys are in the secure store, <see cref="TtsEngines.KeyFor"/>).
    /// </summary>
    public sealed record TtsEngineSettings (string? PiperProgram = null, string? PiperVoices = null, string? AzureRegion = null)
    {
        private const string File = "tts.json";

        public static async Task<TtsEngineSettings> LoadAsync (string? dataDirectory, CancellationToken ct = default)
        {
            var path = dataDirectory is null ? null : Path.Combine (dataDirectory, File);
            if (path is null || !System.IO.File.Exists (path))
                return new ();
            try {
                await using var stream = System.IO.File.OpenRead (path);
                return await JsonSerializer.DeserializeAsync (stream, TtsJson.Default.TtsEngineSettings, ct).ConfigureAwait (false) ?? new ();
            } catch (JsonException) {
                return new ();
            }
        }

        public async Task SaveAsync (string dataDirectory, CancellationToken ct = default)
        {
            Directory.CreateDirectory (dataDirectory);
            await using var stream = System.IO.File.Create (Path.Combine (dataDirectory, File));
            await JsonSerializer.SerializeAsync (stream, this, TtsJson.Default.TtsEngineSettings, ct).ConfigureAwait (false);
        }

        /// <summary>Piper's default voices folder: <c>piper-voices</c> in local app data.</summary>
        public string VoicesFolder (string? dataDirectory)
            => PiperVoices ?? (dataDirectory is null ? "" : Path.Combine (dataDirectory, "piper-voices"));
    }

    [JsonSourceGenerationOptions (PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable (typeof (TtsEngineSettings))]
    internal sealed partial class TtsJson : JsonSerializerContext
    {
    }

    /// <summary>
    /// The reading-aloud engines this device can use: Recital's bundled voices, the OS voices, Piper when installed,
    /// and each cloud service with a key.
    /// </summary>
    public static class TtsEngines
    {
        /// <summary>The cloud engines Recital can use with the user's own key, by id.</summary>
        public static IReadOnlyList<(string Id, string Name)> CloudServices { get; } = [
            ("elevenlabs", "ElevenLabs"), ("openai", "OpenAI"), ("azure", "Azure Speech"), ("google", "Google Cloud"),
        ];

        /// <summary>Where a cloud service's key is kept in the secure store.</summary>
        public static string KeyFor (string service) => "tts:" + service;

        /// <summary>The OS voices only (no Piper, no cloud), for when settings and secrets are not to hand.</summary>
        public static IReadOnlyList<ITtsEngine> ForCurrentPlatform (string? cacheDirectory)
        {
            var cache = cacheDirectory is null ? null : new AudioCache (cacheDirectory);
            var engines = new List<ITtsEngine> ();
            // Recital's own voices first, when this build carries them: they need nothing installed and stay on
            // the device. Their audio is quick to make again, so it is not cached.
            if (Kokoro.KokoroSynthesizer.FindFolder () is { } kokoro && AudioOutputFactory () is { } output)
                engines.Add (new AudioTtsEngine (new Kokoro.KokoroSynthesizer (kokoro), output));
            if (OperatingSystem.IsWindows ())
                engines.Add (new AudioTtsEngine (new SapiSynthesizer (), WindowsOutput, cache));
            else if (OperatingSystem.IsMacOS () && File.Exists ("/usr/bin/say") && AfplayAudioOutput.IsAvailable ())
                engines.Add (new AudioTtsEngine (new MacSaySynthesizer (), MacOutput, cache));
            else if (SpeechDispatcherEngine.IsAvailable ())
                engines.Add (new SpeechDispatcherEngine ());
            return engines;
        }

        /// <summary>Every engine this device can use, in the order offered: the OS voices, Piper, then the cloud services.</summary>
        /// <param name="handler">The HTTP stack for the cloud services (tests pass fakes); null for the default.</param>
        public static async Task<IReadOnlyList<ITtsEngine>> AvailableAsync (string? dataDirectory, ISecretStore secrets, HttpMessageHandler? handler = null, CancellationToken ct = default)
        {
            var cacheDirectory = dataDirectory is null ? null : Path.Combine (dataDirectory, "tts-cache");
            var cache = cacheDirectory is null ? null : new AudioCache (cacheDirectory);
            var engines = new List<ITtsEngine> (ForCurrentPlatform (cacheDirectory));
            var output = AudioOutputFactory ();
            if (output is null)
                return engines;

            var settings = await TtsEngineSettings.LoadAsync (dataDirectory, ct).ConfigureAwait (false);
            var piper = settings.PiperProgram ?? PiperSynthesizer.FindProgram ();
            var voices = settings.VoicesFolder (dataDirectory);
            if (piper is not null && Directory.Exists (voices) && Directory.EnumerateFiles (voices, "*.onnx", SearchOption.AllDirectories).Any ())
                engines.Add (new AudioTtsEngine (new PiperSynthesizer (piper, voices), output, cache));

            var http = new HttpClient (handler ?? new SocketsHttpHandler (), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds (60) };
            foreach (var (id, _) in CloudServices) {
                if (await secrets.GetAsync (KeyFor (id), ct).ConfigureAwait (false) is not { Length: > 0 } key)
                    continue;
                ISpeechSynthesizer? synthesizer = id switch {
                    "elevenlabs" => new ElevenLabsSynthesizer (http, key),
                    "openai" => new OpenAiSynthesizer (http, key),
                    "azure" when !string.IsNullOrEmpty (settings.AzureRegion) => new AzureSynthesizer (http, key, settings.AzureRegion),
                    "google" => new GoogleSynthesizer (http, key),
                    _ => null,
                };
                if (synthesizer is not null)
                    engines.Add (new AudioTtsEngine (synthesizer, output, cache));
            }
            return engines;
        }

        /// <summary>This OS's audio output for engines that make audio, or null where there is none yet.</summary>
        public static Func<IAudioOutput>? AudioOutputFactory ()
        {
            if (OperatingSystem.IsWindows ())
                return WindowsOutput;
            if (OperatingSystem.IsMacOS () && AfplayAudioOutput.IsAvailable ())
                return MacOutput;
            if (OperatingSystem.IsLinux () && PipeAudioOutput.IsAvailable ())
                return LinuxOutput;
            return null;
        }

        [System.Runtime.Versioning.SupportedOSPlatform ("windows")]
        private static WaveOutAudioOutput WindowsOutput () => new ();

        [System.Runtime.Versioning.SupportedOSPlatform ("macos")]
        private static AfplayAudioOutput MacOutput () => new ();

        [System.Runtime.Versioning.SupportedOSPlatform ("linux")]
        private static PipeAudioOutput LinuxOutput () => new ();
    }
}
