using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// The play position of an output that cannot report one (a pipe to a player process): audio counts as
    /// played as the clock runs from the first write, never past what was written, and not while paused. A
    /// fixed latency is subtracted for the player's own buffer.
    /// </summary>
    internal sealed class ClockedPlayback (TimeSpan latency)
    {
        private readonly object gate = new ();
        private readonly Stopwatch clock = Stopwatch.StartNew ();
        private TimeSpan written;
        private TimeSpan played;
        private TimeSpan last;
        private bool paused;
        private bool running;

        public void Reset ()
        {
            lock (gate) {
                written = played = TimeSpan.Zero;
                running = false;
                paused = false;
            }
        }

        public void Wrote (TimeSpan duration)
        {
            lock (gate) {
                Update ();
                if (!running) {
                    running = true;
                    last = clock.Elapsed;
                }
                written += duration;
            }
        }

        public TimeSpan Played {
            get {
                lock (gate) {
                    Update ();
                    return played - latency > TimeSpan.Zero ? played - latency : TimeSpan.Zero;
                }
            }
        }

        public void Pause ()
        {
            lock (gate) {
                Update ();
                paused = true;
            }
        }

        public void Unpause ()
        {
            lock (gate) {
                Update ();
                paused = false;
            }
        }

        private void Update ()
        {
            var now = clock.Elapsed;
            if (running && !paused)
                played = played + (now - last) < written + latency ? played + (now - last) : written + latency;
            last = now;
        }
    }

    /// <summary>Stopping and continuing a player process (SIGSTOP/SIGCONT), for pause on Linux and macOS.</summary>
    internal static partial class Signals
    {
        public static void Stop (Process process) => Send (process, OperatingSystem.IsMacOS () ? 17 : 19);

        public static void Continue (Process process) => Send (process, OperatingSystem.IsMacOS () ? 19 : 18);

        private static void Send (Process process, int signal)
        {
            try {
                if (!process.HasExited)
                    _ = kill (process.Id, signal);
            } catch (InvalidOperationException) {
                // Not started, or gone.
            }
        }

        [LibraryImport ("libc", SetLastError = true)]
        private static partial int kill (int pid, int signal);
    }
}
