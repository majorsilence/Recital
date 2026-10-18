using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Majorsilence.Recital.Tts.Piper
{
    /// <summary>
    /// Piper (plan 3.3): offline neural voices, run as the user's own <c>piper</c> program. Recital never ships,
    /// downloads or links it (its current releases and the espeak-ng it phonemizes with are GPL); it starts
    /// the program the user installed and talks to it over pipes.
    /// </summary>
    /// <remarks>
    /// One long-running process per voice and speed (loading a model takes a second): each chunk is a line on
    /// its input, and with <c>--output_dir</c> Piper writes a WAVE file per line and prints its path. Piper
    /// gives no timings, so progress is interpolated through each chunk.
    /// </remarks>
    public sealed class PiperSynthesizer : ISpeechSynthesizer
    {
        private readonly string program;
        private readonly string voices_folder;
        private readonly SemaphoreSlim gate = new (1, 1);
        private readonly string output_folder = Path.Combine (Path.GetTempPath (), "recital-piper-" + Guid.NewGuid ().ToString ("N"));
        private Process? process;
        private string? process_key;

        /// <param name="program">The <c>piper</c> executable.</param>
        /// <param name="voicesFolder">Where the voices are: each a <c>.onnx</c> model with its <c>.onnx.json</c> beside it.</param>
        public PiperSynthesizer (string program, string voicesFolder)
        {
            this.program = program;
            voices_folder = voicesFolder;
        }

        public string Id => "piper";
        public string Name => "Piper (offline)";
        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: false, MaxChunkChars: 1200);

        /// <summary>The <c>piper</c> on the PATH, or null.</summary>
        public static string? FindProgram ()
        {
            var name = OperatingSystem.IsWindows () ? "piper.exe" : "piper";
            foreach (var folder in (Environment.GetEnvironmentVariable ("PATH") ?? "").Split (Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) {
                var candidate = Path.Combine (folder, name);
                if (File.Exists (candidate))
                    return candidate;
            }
            return null;
        }

        public Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TtsVoice>> (Directory.Exists (voices_folder)
                ? Directory.EnumerateFiles (voices_folder, "*.onnx", SearchOption.AllDirectories)
                    .Where (model => File.Exists (model + ".json"))
                    .Select (model => new TtsVoice (Path.GetFileNameWithoutExtension (model), Path.GetFileNameWithoutExtension (model), Language (model + ".json")))
                    .OrderBy (v => v.Name, StringComparer.Ordinal).ToList ()
                : []);

        public async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            await gate.WaitAsync (ct).ConfigureAwait (false);
            try {
                var piper = await ProcessForAsync (settings, language, ct).ConfigureAwait (false);
                // One utterance per line: newlines inside the chunk would start another.
                await piper.StandardInput.WriteLineAsync (chunk.Text.ReplaceLineEndings (" ").AsMemory (), ct).ConfigureAwait (false);
                await piper.StandardInput.FlushAsync (ct).ConfigureAwait (false);
                var path = await piper.StandardOutput.ReadLineAsync (ct).ConfigureAwait (false)
                    ?? throw new IOException ("Piper stopped: " + await piper.StandardError.ReadToEndAsync (ct).ConfigureAwait (false));
                path = path.Trim ();
                try {
                    var (format, pcm) = Wave.Read (await File.ReadAllBytesAsync (path, ct).ConfigureAwait (false));
                    return new SynthesizedAudio (format, pcm, []);
                } finally {
                    File.Delete (path);
                }
            } finally {
                gate.Release ();
            }
        }

        /// <summary>The running process for this voice and speed, started (or restarted) as needed.</summary>
        private async Task<Process> ProcessForAsync (TtsSettings settings, string? language, CancellationToken ct)
        {
            var model = ModelFor (settings.VoiceId, language) ?? throw new IOException ($"No Piper voice in {voices_folder}. Download one (a .onnx and its .onnx.json) into that folder.");
            // Piper's length scale is the inverse of speed.
            var lengthScale = (1 / settings.ClampedRate).ToString ("0.00", CultureInfo.InvariantCulture);
            var key = model + "|" + lengthScale;
            if (process is { HasExited: false } && process_key == key)
                return process;
            await StopProcessAsync ().ConfigureAwait (false);

            Directory.CreateDirectory (output_folder);
            var start = new ProcessStartInfo (program) {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var argument in new[] { "--model", model, "--output_dir", output_folder, "--length_scale", lengthScale, "--sentence_silence", "0.25" })
                start.ArgumentList.Add (argument);
            try {
                process = Process.Start (start) ?? throw new IOException ("Could not start Piper.");
            } catch (System.ComponentModel.Win32Exception ex) {
                throw new IOException ($"Could not start Piper ({program}): {ex.Message}", ex);
            }
            process_key = key;
            return process;
        }

        private string? ModelFor (string? voice, string? language)
        {
            var models = Directory.Exists (voices_folder)
                ? Directory.EnumerateFiles (voices_folder, "*.onnx", SearchOption.AllDirectories).Where (m => File.Exists (m + ".json")).OrderBy (m => m, StringComparer.Ordinal).ToList ()
                : [];
            if (voice is not null && models.FirstOrDefault (m => Path.GetFileNameWithoutExtension (m) == voice) is { } chosen)
                return chosen;
            // The first voice in the book's language, else the first voice.
            var prefix = language?.Split ('-')[0];
            return models.FirstOrDefault (m => prefix is not null && Language (m + ".json")?.StartsWith (prefix, StringComparison.OrdinalIgnoreCase) == true)
                ?? models.FirstOrDefault ();
        }

        /// <summary>The voice's language from its config (<c>language.code</c>, "en_US"), as BCP 47.</summary>
        private static string? Language (string config)
        {
            try {
                return JsonNode.Parse (File.ReadAllText (config))?["language"]?["code"]?.GetValue<string> ()?.Replace ('_', '-');
            } catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) {
                return null;
            }
        }

        private async Task StopProcessAsync ()
        {
            if (process is null)
                return;
            try {
                process.StandardInput.Close ();
                using var timeout = new CancellationTokenSource (TimeSpan.FromSeconds (2));
                await process.WaitForExitAsync (timeout.Token).ConfigureAwait (false);
            } catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) {
                try {
                    process.Kill ();
                } catch (InvalidOperationException) {
                }
            }
            process.Dispose ();
            process = null;
        }

        public async ValueTask DisposeAsync ()
        {
            await StopProcessAsync ().ConfigureAwait (false);
            if (Directory.Exists (output_folder))
                Directory.Delete (output_folder, recursive: true);
            gate.Dispose ();
        }
    }
}
