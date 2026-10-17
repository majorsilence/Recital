using System.Diagnostics;
using System.Runtime.Versioning;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// macOS audio through <c>afplay</c>: what has been written is played in segments (a temporary WAVE file
    /// each), one after another. Each segment costs afplay's start-up, well under the 300 ms the plan allows
    /// between chunks, since a segment is at least a whole chunk.
    /// </summary>
    [SupportedOSPlatform ("macos")]
    public sealed class AfplayAudioOutput : IAudioOutput
    {
        private static readonly TimeSpan Startup = TimeSpan.FromMilliseconds (80);
        private readonly object gate = new ();
        private readonly MemoryStream pending = new ();
        private readonly ClockedPlayback clock = new (Startup);
        private PcmFormat current = PcmFormat.Speech;
        private CancellationTokenSource stop = new ();
        private Task? loop;
        private Process? player;
        private bool paused;

        public static bool IsAvailable () => OperatingSystem.IsMacOS () && File.Exists ("/usr/bin/afplay");

        public Task StartAsync (PcmFormat format, CancellationToken ct = default)
        {
            current = format;
            clock.Reset ();
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            lock (gate) {
                pending.Write (pcm.Span);
                clock.Wrote (current.Duration (pcm.Length));
                if (loop is null || loop.IsCompleted) {
                    var token = stop.Token;
                    loop = Task.Run (() => PlayLoopAsync (token), CancellationToken.None);
                }
            }
            return ValueTask.CompletedTask;
        }

        private async Task PlayLoopAsync (CancellationToken ct)
        {
            while (!ct.IsCancellationRequested) {
                byte[] segment;
                lock (gate) {
                    if (pending.Length == 0)
                        return;
                    segment = pending.ToArray ();
                    pending.SetLength (0);
                }
                var file = Path.Combine (Path.GetTempPath (), "recital-play-" + Guid.NewGuid ().ToString ("N") + ".wav");
                try {
                    await File.WriteAllBytesAsync (file, Wave.Write (current, segment), ct).ConfigureAwait (false);
                    var process = Process.Start (new ProcessStartInfo ("/usr/bin/afplay", [file]) { UseShellExecute = false, CreateNoWindow = true });
                    lock (gate) {
                        player = process;
                        if (paused && process is not null)
                            Signals.Stop (process);
                    }
                    if (process is not null)
                        await process.WaitForExitAsync (ct).ConfigureAwait (false);
                } finally {
                    lock (gate) {
                        player?.Dispose ();
                        player = null;
                    }
                    File.Delete (file);
                }
            }
        }

        public TimeSpan Played => clock.Played;

        public void Pause ()
        {
            lock (gate) {
                paused = true;
                clock.Pause ();
                if (player is not null)
                    Signals.Stop (player);
            }
        }

        public void Unpause ()
        {
            lock (gate) {
                paused = false;
                if (player is not null)
                    Signals.Continue (player);
                clock.Unpause ();
            }
        }

        public async Task StopAsync ()
        {
            CancellationTokenSource old;
            lock (gate) {
                old = stop;
                stop = new CancellationTokenSource ();
                pending.SetLength (0);
                try {
                    if (player is { HasExited: false } p) {
                        Signals.Continue (p);
                        p.Kill ();
                    }
                } catch (InvalidOperationException) {
                }
            }
            await old.CancelAsync ().ConfigureAwait (false);
            if (loop is not null)
                try {
                    await loop.ConfigureAwait (false);
                } catch (OperationCanceledException) {
                }
            old.Dispose ();
            clock.Reset ();
            paused = false;
        }

        public async ValueTask DisposeAsync () => await StopAsync ().ConfigureAwait (false);
    }
}
