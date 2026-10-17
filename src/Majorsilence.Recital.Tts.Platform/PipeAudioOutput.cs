using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// Linux audio: PCM piped into one long-running player (<c>pw-cat</c>, else <c>pacat</c>, else
    /// <c>aplay</c>) for the whole session, so chunks play back to back with no gap. The position is the
    /// clock less the player's buffer; pause stops the player process.
    /// </summary>
    [SupportedOSPlatform ("linux")]
    public sealed class PipeAudioOutput : IAudioOutput
    {
        private static readonly TimeSpan Latency = TimeSpan.FromMilliseconds (150);
        private static readonly string[] Players = ["/usr/bin/pw-cat", "/usr/bin/pacat", "/usr/bin/aplay"];
        private readonly ClockedPlayback clock = new (Latency);
        private Process? player;
        private Stream? input;
        private PcmFormat current = PcmFormat.Speech;

        /// <summary>The player found on this system, or null.</summary>
        public static string? FindPlayer ()
            => Players.FirstOrDefault (File.Exists);

        public static bool IsAvailable () => OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid () && FindPlayer () is not null;

        public Task StartAsync (PcmFormat format, CancellationToken ct = default)
        {
            Kill ();
            current = format;
            var path = FindPlayer () ?? throw new IOException ("No audio player found (pw-cat, pacat or aplay).");
            var rate = current.SampleRate.ToString (CultureInfo.InvariantCulture);
            var channels = current.Channels.ToString (CultureInfo.InvariantCulture);
            string[] arguments = Path.GetFileName (path) switch {
                "pw-cat" => ["--playback", "--raw", "--format", "s16", "--rate", rate, "--channels", channels, "--latency", "150ms", "--media-role", "Accessibility", "-"],
                "pacat" => ["--playback", "--raw", "--format=s16le", "--rate=" + rate, "--channels=" + channels, "--latency-msec=150", "--client-name=Recital", "--stream-name=Reading aloud"],
                _ => ["-q", "-t", "raw", "-f", "S16_LE", "-r", rate, "-c", channels, "--buffer-time=150000", "-"],
            };
            var start = new ProcessStartInfo (path) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add (argument);
            player = Process.Start (start) ?? throw new IOException ("Could not start " + path);
            input = player.StandardInput.BaseStream;
            clock.Reset ();
            return Task.CompletedTask;
        }

        public async ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            var stream = input ?? throw new InvalidOperationException ("StartAsync first.");
            clock.Wrote (current.Duration (pcm.Length));
            try {
                await stream.WriteAsync (pcm, ct).ConfigureAwait (false);
                await stream.FlushAsync (ct).ConfigureAwait (false);
            } catch (IOException ex) {
                throw new IOException ("The audio player stopped: " + ex.Message, ex);
            }
        }

        public TimeSpan Played => clock.Played;

        public void Pause ()
        {
            clock.Pause ();
            if (player is not null)
                Signals.Stop (player);
        }

        public void Unpause ()
        {
            if (player is not null)
                Signals.Continue (player);
            clock.Unpause ();
        }

        public Task StopAsync ()
        {
            Kill ();
            clock.Reset ();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync ()
        {
            Kill ();
            return ValueTask.CompletedTask;
        }

        private void Kill ()
        {
            if (player is null)
                return;
            try {
                Signals.Continue (player);
                if (!player.HasExited)
                    player.Kill ();
            } catch (InvalidOperationException) {
            }
            player.Dispose ();
            player = null;
            input = null;
        }
    }
}
