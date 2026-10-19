using System.Diagnostics;
using System.Runtime.InteropServices;
using AVFoundation;
using Majorsilence.Recital.Tts;

namespace Majorsilence.Recital.iOS
{
    /// <summary>
    /// Plays an engine's PCM through AVAudioEngine, for engines that make audio (Recital's own voices, the cloud
    /// voices). Audio goes to the player node in short buffers; what has been heard is the buffers played back,
    /// plus the clock's time into the one playing, so it stands still while paused and when the queue runs dry.
    /// </summary>
    public sealed class AppleAudioOutput : IAudioOutput
    {
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds (100);
        // About half a second queued: room to ride out a slow synthesis, little to drop on a stop.
        private static readonly TimeSpan Ahead = TimeSpan.FromMilliseconds (500);

        private readonly object gate = new ();
        private readonly Stopwatch clock = new ();
        private readonly Queue<TimeSpan> scheduled = new ();
        private AVAudioEngine? engine;
        private AVAudioPlayerNode? player;
        private AVAudioFormat? node_format;
        private PcmFormat? current;
        private TimeSpan played;
        private int generation;
        private bool paused;

        public Task StartAsync (PcmFormat format, CancellationToken ct = default)
        {
            AppleSpeechEngine.PrepareSession ();
            lock (gate) {
                if (engine is null || current != format) {
                    Release ();
                    current = format;
                    engine = new AVAudioEngine ();
                    player = new AVAudioPlayerNode ();
                    node_format = new AVAudioFormat (format.SampleRate, (uint) format.Channels);
                    engine.AttachNode (player);
                    engine.Connect (player, engine.MainMixerNode, node_format);
                    engine.Prepare ();
                    if (!engine.StartAndReturnError (out var error))
                        throw new InvalidOperationException ($"The audio output did not start: {error?.LocalizedDescription}");
                }
                generation++;
                scheduled.Clear ();
                played = TimeSpan.Zero;
                clock.Reset ();
                if (!paused)
                    player!.Play ();
            }
            return Task.CompletedTask;
        }

        public async ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            if (current is null)
                return;
            var slice = (int) current.Bytes (Slice);
            for (var at = 0; at < pcm.Length; at += slice) {
                // Wait while enough is queued, as a blocking output would.
                while (Queued () > Ahead)
                    await Task.Delay (Slice / 2, ct).ConfigureAwait (false);
                Schedule (pcm.Slice (at, Math.Min (slice, pcm.Length - at)).Span);
            }
        }

        private TimeSpan Queued ()
        {
            lock (gate)
                return scheduled.Aggregate (TimeSpan.Zero, (sum, d) => sum + d) - Into ();
        }

        // 16-bit samples to the node's float buffer, one channel after another.
        private void Schedule (ReadOnlySpan<byte> pcm)
        {
            lock (gate) {
                if (player is null || node_format is null || current is null)
                    return;
                var channels = current.Channels;
                var frames = pcm.Length / current.BytesPerFrame;
                var buffer = new AVAudioPcmBuffer (node_format, (uint) frames) { FrameLength = (uint) frames };
                var samples = new float[frames];
                for (var c = 0; c < channels; c++) {
                    for (var f = 0; f < frames; f++)
                        samples[f] = BitConverter.ToInt16 (pcm.Slice ((f * channels + c) * 2, 2)) / 32768f;
                    Marshal.Copy (samples, 0, Marshal.ReadIntPtr (buffer.FloatChannelData, c * IntPtr.Size), frames);
                }
                var duration = TimeSpan.FromSeconds (frames / (double) current.SampleRate);
                var mine = generation;
                if (scheduled.Count == 0 && !paused)
                    clock.Restart ();   // the queue had run dry: this buffer starts now
                scheduled.Enqueue (duration);
                player.ScheduleBuffer (buffer, AVAudioPlayerNodeCompletionCallbackType.PlayedBack, _ => PlayedBack (mine));
            }
        }

        private void PlayedBack (int mine)
        {
            lock (gate) {
                if (mine != generation || scheduled.Count == 0)
                    return;
                played += scheduled.Dequeue ();
                if (scheduled.Count > 0 && !paused)
                    clock.Restart ();
                else
                    clock.Reset ();
            }
        }

        // How far into the buffer now playing, by the clock: never past its end.
        private TimeSpan Into () => scheduled.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks (Math.Min (clock.Elapsed.Ticks, scheduled.Peek ().Ticks));

        public TimeSpan Played {
            get {
                lock (gate)
                    return played + Into ();
            }
        }

        public void Pause ()
        {
            lock (gate) {
                paused = true;
                clock.Stop ();
                player?.Pause ();
            }
        }

        public void Unpause ()
        {
            lock (gate) {
                paused = false;
                if (scheduled.Count > 0)
                    clock.Start ();
                player?.Play ();
            }
        }

        public Task StopAsync ()
        {
            lock (gate) {
                generation++;
                player?.Stop ();
                scheduled.Clear ();
                played = TimeSpan.Zero;
                clock.Reset ();
                paused = false;
            }
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync ()
        {
            lock (gate)
                Release ();
            return ValueTask.CompletedTask;
        }

        private void Release ()
        {
            generation++;
            player?.Stop ();
            engine?.Stop ();
            player?.Dispose ();
            engine?.Dispose ();
            player = null;
            engine = null;
            node_format?.Dispose ();
            node_format = null;
        }
    }
}
