using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// macOS voices through <c>say</c>, written to a WAVE file. Pauses and emphasis use say's embedded commands
    /// (<c>[[slnc 700]]</c>, <c>[[emph +]]</c>). say reports no word timings, so progress is interpolated.
    /// </summary>
    [SupportedOSPlatform ("macos")]
    public sealed class MacSaySynthesizer : ISpeechSynthesizer
    {
        public string Id => "say";
        public string Name => "macOS voices";
        public TtsCapabilities Capabilities => new (Ssml: false, WordTimings: false, MaxChunkChars: 2000);

        public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default)
        {
            // "Alex                en_US    # Most people recognize me by my voice."
            var output = await RunAsync (["-v", "?"], ct).ConfigureAwait (false);
            var voices = new List<TtsVoice> ();
            foreach (var line in output.Split ('\n', StringSplitOptions.RemoveEmptyEntries)) {
                var hash = line.IndexOf ('#');
                var parts = (hash < 0 ? line : line[..hash]).Trim ().Split (' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;
                var language = parts[^1].Replace ('_', '-');
                var name = string.Join (' ', parts[..^1]);
                voices.Add (new TtsVoice (name, name, language));
            }
            return voices;
        }

        public async Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default)
        {
            var file = Path.Combine (Path.GetTempPath (), "recital-say-" + Guid.NewGuid ().ToString ("N") + ".wav");
            var text = Path.Combine (Path.GetTempPath (), "recital-say-" + Guid.NewGuid ().ToString ("N") + ".txt");
            try {
                await File.WriteAllTextAsync (text, Annotate (chunk), ct).ConfigureAwait (false);
                var words = (int)Math.Round (175 * settings.ClampedRate);
                List<string> arguments = ["-o", file, "--file-format=WAVE", "--data-format=LEI16@22050", "-r", words.ToString (CultureInfo.InvariantCulture), "-f", text];
                if (!string.IsNullOrEmpty (settings.VoiceId))
                    arguments.AddRange (["-v", settings.VoiceId]);
                await RunAsync (arguments, ct).ConfigureAwait (false);
                var (format, pcm) = Wave.Read (await File.ReadAllBytesAsync (file, ct).ConfigureAwait (false));
                return new SynthesizedAudio (format, pcm, []);
            } finally {
                File.Delete (file);
                File.Delete (text);
            }
        }

        /// <summary>The chunk with say's embedded commands for its pauses and emphasis.</summary>
        internal static string Annotate (SpeechChunk chunk)
        {
            var text = new StringBuilder ();
            if (chunk.PauseBefore > TimeSpan.Zero)
                text.Append (CultureInfo.InvariantCulture, $"[[slnc {(int)chunk.PauseBefore.TotalMilliseconds}]] ");
            var emphasis = chunk.Spans.Where (s => s.Kind != ProsodyKind.Dialogue).ToList ();
            for (var i = 0; i < chunk.Text.Length; i++) {
                if (emphasis.Any (s => s.Start == i))
                    text.Append ("[[emph +]]");
                // say reads "[[" as the start of a command; there is no escape, so the text's own brackets become parentheses.
                text.Append (chunk.Text[i] switch { '[' => '(', ']' => ')', var c => c });
            }
            if (chunk.PauseAfter > TimeSpan.Zero)
                text.Append (CultureInfo.InvariantCulture, $" [[slnc {(int)chunk.PauseAfter.TotalMilliseconds}]]");
            return text.ToString ();
        }

        private static async Task<string> RunAsync (IEnumerable<string> arguments, CancellationToken ct)
        {
            var start = new ProcessStartInfo ("/usr/bin/say") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
                start.ArgumentList.Add (argument);
            using var process = Process.Start (start) ?? throw new IOException ("Could not start say.");
            var output = process.StandardOutput.ReadToEndAsync (ct);
            var error = process.StandardError.ReadToEndAsync (ct);
            await process.WaitForExitAsync (ct).ConfigureAwait (false);
            if (process.ExitCode != 0)
                throw new IOException ("say failed: " + (await error.ConfigureAwait (false)).Trim ());
            return await output.ConfigureAwait (false);
        }

        public ValueTask DisposeAsync () => ValueTask.CompletedTask;
    }
}
