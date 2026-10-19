using Android.Media;
using Majorsilence.Recital.Tts;

using AndroidEncoding = Android.Media.Encoding;

namespace Majorsilence.Recital.Android
{
    /// <summary>
    /// Plays an engine's PCM through an AudioTrack in streaming mode, for engines that make audio (Recital's own
    /// voices, the cloud voices). The track's play head says how much has been heard: it stands still while paused
    /// and when the queue runs dry.
    /// </summary>
    public sealed class AndroidAudioOutput : IAudioOutput
    {
        // Written in slices, so a stop never waits behind a long blocking write.
        private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds (100);

        private readonly object gate = new ();
        private AudioTrack? track;
        private PcmFormat? current;
        private long frames_written;
        private bool paused;

        public Task StartAsync (PcmFormat format, CancellationToken ct = default)
        {
            lock (gate) {
                if (track is null || current != format) {
                    Release ();
                    current = format;
                    var channels = format.Channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
                    var minimum = AudioTrack.GetMinBufferSize (format.SampleRate, channels, AndroidEncoding.Pcm16bit);
                    track = new AudioTrack.Builder ()
                        .SetAudioAttributes (new AudioAttributes.Builder ()
                            .SetUsage (AudioUsageKind.Media)!
                            .SetContentType (AudioContentType.Speech)!
                            .Build ()!)!
                        .SetAudioFormat (new AudioFormat.Builder ()
                            .SetEncoding (AndroidEncoding.Pcm16bit)!
                            .SetSampleRate (format.SampleRate)!
                            .SetChannelMask (channels)!
                            .Build ()!)!
                        // About half a second queued: room to ride out a slow synthesis, little to drop on a stop.
                        .SetBufferSizeInBytes (Math.Max (minimum, format.BytesPerSecond / 2))!
                        .SetTransferMode (AudioTrackMode.Stream)!
                        .Build ();
                }
                frames_written = 0;
                track.Flush ();
                if (!paused)
                    track.Play ();
            }
            return Task.CompletedTask;
        }

        public async ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            var slice = (int) (current?.Bytes (Slice) ?? pcm.Length);
            for (var at = 0; at < pcm.Length; at += slice) {
                ct.ThrowIfCancellationRequested ();
                var piece = pcm.Slice (at, Math.Min (slice, pcm.Length - at)).ToArray ();
                // A blocking write waits while the track's buffer is full: off the caller's thread.
                await Task.Run (() => {
                    var written = 0;
                    while (written < piece.Length && !ct.IsCancellationRequested) {
                        AudioTrack? playing;
                        lock (gate)
                            playing = track;
                        if (playing is null)
                            return;
                        var result = playing.Write (piece, written, piece.Length - written, WriteMode.Blocking);
                        if (result <= 0)
                            return;   // stopped or released underneath us
                        written += result;
                    }
                }, ct).ConfigureAwait (false);
                lock (gate)
                    frames_written += piece.Length / Math.Max (1, current!.BytesPerFrame);
            }
        }

        public TimeSpan Played {
            get {
                lock (gate) {
                    if (track is null || current is null)
                        return TimeSpan.Zero;
                    // The head counts frames played since the last flush (an unsigned 32-bit count).
                    var head = Math.Min ((long) (uint) track.PlaybackHeadPosition, frames_written);
                    return TimeSpan.FromSeconds (head / (double) current.SampleRate);
                }
            }
        }

        public void Pause ()
        {
            lock (gate) {
                paused = true;
                track?.Pause ();
            }
        }

        public void Unpause ()
        {
            lock (gate) {
                paused = false;
                track?.Play ();
            }
        }

        public Task StopAsync ()
        {
            lock (gate) {
                if (track is not null) {
                    track.Pause ();
                    track.Flush ();
                    track.Stop ();
                }
                frames_written = 0;
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
            track?.Stop ();
            track?.Release ();
            track?.Dispose ();
            track = null;
        }
    }
}
