using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Majorsilence.Recital.Tts.Platform
{
    /// <summary>
    /// Windows audio through waveOut (winmm): every write becomes a queued buffer, played back to back, and
    /// the position comes from the device (<c>waveOutGetPosition</c>), so it is exact.
    /// </summary>
    [SupportedOSPlatform ("windows")]
    public sealed partial class WaveOutAudioOutput : IAudioOutput
    {
        private const uint WaveMapper = unchecked ((uint)-1);
        private const int TimeBytes = 4;
        private const int WhdrDone = 1;

        private readonly object gate = new ();
        private readonly List<(IntPtr Header, IntPtr Data)> queued = [];
        private IntPtr device;
        private PcmFormat current = PcmFormat.Speech;

        public Task StartAsync (PcmFormat format, CancellationToken ct = default)
        {
            lock (gate) {
                Close ();
                current = format;
                var wave = new WaveFormat {
                    FormatTag = 1, Channels = (short)current.Channels, SamplesPerSecond = current.SampleRate,
                    AverageBytesPerSecond = current.BytesPerSecond, BlockAlign = (short)current.BytesPerFrame,
                    BitsPerSample = (short)current.BitsPerSample, Size = 0,
                };
                Check (waveOutOpen (out device, WaveMapper, ref wave, IntPtr.Zero, IntPtr.Zero, 0), "open");
            }
            return Task.CompletedTask;
        }

        public ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        {
            lock (gate) {
                if (device == IntPtr.Zero)
                    throw new InvalidOperationException ("StartAsync first.");
                Collect ();
                var data = Marshal.AllocHGlobal (Math.Max (1, pcm.Length));
                Marshal.Copy (pcm.ToArray (), 0, data, pcm.Length);
                var header = Marshal.AllocHGlobal (Marshal.SizeOf<WaveHeader> ());
                Marshal.StructureToPtr (new WaveHeader { Data = data, BufferLength = pcm.Length }, header, false);
                Check (waveOutPrepareHeader (device, header, Marshal.SizeOf<WaveHeader> ()), "prepare");
                Check (waveOutWrite (device, header, Marshal.SizeOf<WaveHeader> ()), "write");
                queued.Add ((header, data));
            }
            return ValueTask.CompletedTask;
        }

        public TimeSpan Played {
            get {
                lock (gate) {
                    if (device == IntPtr.Zero)
                        return TimeSpan.Zero;
                    var time = new MmTime { Type = TimeBytes };
                    return waveOutGetPosition (device, ref time, Marshal.SizeOf<MmTime> ()) == 0 ? current.Duration (time.Bytes) : TimeSpan.Zero;
                }
            }
        }

        public void Pause ()
        {
            lock (gate)
                if (device != IntPtr.Zero)
                    _ = waveOutPause (device);
        }

        public void Unpause ()
        {
            lock (gate)
                if (device != IntPtr.Zero)
                    _ = waveOutRestart (device);
        }

        public Task StopAsync ()
        {
            lock (gate)
                Close ();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync ()
        {
            lock (gate)
                Close ();
            return ValueTask.CompletedTask;
        }

        /// <summary>Frees the buffers the device has finished with.</summary>
        private void Collect ()
        {
            for (var i = queued.Count - 1; i >= 0; i--) {
                var (header, data) = queued[i];
                if ((Marshal.PtrToStructure<WaveHeader> (header).Flags & WhdrDone) == 0)
                    continue;
                _ = waveOutUnprepareHeader (device, header, Marshal.SizeOf<WaveHeader> ());
                Marshal.FreeHGlobal (data);
                Marshal.FreeHGlobal (header);
                queued.RemoveAt (i);
            }
        }

        private void Close ()
        {
            if (device == IntPtr.Zero)
                return;
            _ = waveOutReset (device);
            foreach (var (header, data) in queued) {
                _ = waveOutUnprepareHeader (device, header, Marshal.SizeOf<WaveHeader> ());
                Marshal.FreeHGlobal (data);
                Marshal.FreeHGlobal (header);
            }
            queued.Clear ();
            _ = waveOutClose (device);
            device = IntPtr.Zero;
        }

        private static void Check (int result, string what)
        {
            if (result != 0)
                throw new IOException ($"Windows audio ({what}) failed: MMSYSERR {result}.");
        }

        [StructLayout (LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public short FormatTag;
            public short Channels;
            public int SamplesPerSecond;
            public int AverageBytesPerSecond;
            public short BlockAlign;
            public short BitsPerSample;
            public short Size;
        }

        [StructLayout (LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public int BufferLength;
            public int BytesRecorded;
            public IntPtr User;
            public int Flags;
            public int Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [StructLayout (LayoutKind.Explicit)]
        private struct MmTime
        {
            [FieldOffset (0)] public int Type;
            [FieldOffset (4)] public int Bytes;
            [FieldOffset (4)] private long padding;
        }

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutOpen (out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, int flags);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutPrepareHeader (IntPtr device, IntPtr header, int size);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutUnprepareHeader (IntPtr device, IntPtr header, int size);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutWrite (IntPtr device, IntPtr header, int size);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutGetPosition (IntPtr device, ref MmTime time, int size);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutPause (IntPtr device);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutRestart (IntPtr device);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutReset (IntPtr device);

        [LibraryImport ("winmm.dll")]
        private static partial int waveOutClose (IntPtr device);
    }
}
