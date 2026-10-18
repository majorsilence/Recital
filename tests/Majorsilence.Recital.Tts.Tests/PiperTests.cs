using Majorsilence.Recital.Tts.Piper;
using Xunit;

namespace Majorsilence.Recital.Tts.Tests
{
    /// <summary>
    /// Piper through a stand-in program that speaks its protocol (a line in, a WAVE file's path out). With
    /// RECITAL_PIPER and RECITAL_PIPER_VOICES set, the same test runs against a real Piper.
    /// </summary>
    public sealed class PiperTests : IDisposable
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private readonly string folder = Path.Combine (Path.GetTempPath (), "recital-piper-test-" + Guid.NewGuid ().ToString ("N"));

        /// <summary>A shell script standing in for piper: for each input line, a silent WAVE file of 50 ms per character.</summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform ("windows")]
        private string FakePiper ()
        {
            Directory.CreateDirectory (folder);
            var script = Path.Combine (folder, "piper");
            File.WriteAllText (script, """
                #!/bin/sh
                out=""
                while [ $# -gt 0 ]; do
                  case "$1" in --output_dir) out="$2"; shift 2;; *) shift;; esac
                done
                n=0
                while IFS= read -r line; do
                  n=$((n+1))
                  f="$out/$n.wav"
                  bytes=$(( ${#line} * 2205 ))
                  printf 'RIFF\000\000\000\000WAVEfmt \020\000\000\000\001\000\001\000\042\126\000\000\104\254\000\000\002\000\020\000data\000\000\000\000' > "$f"
                  head -c "$bytes" /dev/zero >> "$f"
                  echo "$f"
                done
                """.ReplaceLineEndings ("\n"));
            File.SetUnixFileMode (script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var voices = Path.Combine (folder, "voices");
            Directory.CreateDirectory (voices);
            File.WriteAllBytes (Path.Combine (voices, "en_GB-alba-medium.onnx"), [0]);
            File.WriteAllText (Path.Combine (voices, "en_GB-alba-medium.onnx.json"), """{ "language": { "code": "en_GB" }, "audio": { "sample_rate": 22050 } }""");
            File.WriteAllBytes (Path.Combine (voices, "de_DE-thorsten-medium.onnx"), [0]);
            File.WriteAllText (Path.Combine (voices, "de_DE-thorsten-medium.onnx.json"), """{ "language": { "code": "de_DE" } }""");
            return script;
        }

        [Fact (Timeout = 60_000)]
        public async Task SpeaksEachChunkThroughOneRunningProcess ()
        {
            Assert.SkipWhen (OperatingSystem.IsWindows (), "The stand-in is a shell script.");
            if (OperatingSystem.IsWindows ())
                return;
            var real = Environment.GetEnvironmentVariable ("RECITAL_PIPER");
            var program = real ?? FakePiper ();
            var voices = Environment.GetEnvironmentVariable ("RECITAL_PIPER_VOICES") ?? Path.Combine (folder, "voices");
            await using var piper = new PiperSynthesizer (program, voices);

            var list = await piper.GetVoicesAsync (Ct);
            Assert.NotEmpty (list);
            if (real is null)
                Assert.Equal (["de_DE-thorsten-medium", "en_GB-alba-medium"], list.Select (v => v.Id));

            await using var book = await PlannerTests.OpenAsync ("pg-11-alice-epub3.epub");
            var chunks = (await PlannerTests.AllChunksAsync (book)).Where (c => c.Kind == ChunkKind.Paragraph && c.Text.Length is > 40 and < 300).Take (3).ToList ();
            foreach (var chunk in chunks) {
                var audio = await piper.SynthesizeAsync (chunk, new TtsSettings (Rate: 1.2), "en-GB", Ct);
                Assert.True (audio.Duration > TimeSpan.Zero);
                Assert.Empty (audio.Words);
                if (real is null)
                    Assert.InRange (audio.Duration.TotalMilliseconds, 50 * chunk.Text.Length - 1, 50 * chunk.Text.Length);
            }
        }

        [Fact]
        public async Task SaysWhenThereIsNoVoice ()
        {
            Directory.CreateDirectory (folder);
            await using var piper = new PiperSynthesizer ("/nonexistent/piper", folder);
            Assert.Empty (await piper.GetVoicesAsync (Ct));
            await using var book = await PlannerTests.OpenAsync ("pg-11-alice-epub3.epub");
            var chunk = (await PlannerTests.AllChunksAsync (book)).First ();
            var ex = await Assert.ThrowsAsync<IOException> (() => piper.SynthesizeAsync (chunk, new TtsSettings (), "en", Ct));
            Assert.Contains ("No Piper voice", ex.Message, StringComparison.Ordinal);
        }

        public void Dispose ()
        {
            if (Directory.Exists (folder))
                Directory.Delete (folder, recursive: true);
        }
    }
}
