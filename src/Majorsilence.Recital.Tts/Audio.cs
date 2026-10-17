using System.Buffers.Binary;
using System.Text;

namespace Majorsilence.Recital.Tts
{
    /// <summary>Signed little-endian PCM.</summary>
    public sealed record PcmFormat (int SampleRate, int Channels, int BitsPerSample)
    {
        public static readonly PcmFormat Speech = new (22050, 1, 16);

        public int BytesPerFrame => Channels * BitsPerSample / 8;
        public int BytesPerSecond => SampleRate * BytesPerFrame;

        public TimeSpan Duration (long bytes) => TimeSpan.FromSeconds (bytes / (double)BytesPerSecond);

        /// <summary>The byte count for <paramref name="duration"/>, rounded down to whole frames.</summary>
        public long Bytes (TimeSpan duration) => (long)(duration.TotalSeconds * SampleRate) * BytesPerFrame;
    }

    /// <param name="CharIndex">The word's first character in the chunk text.</param>
    /// <param name="Offset">When it starts, from the start of the chunk's audio.</param>
    public readonly record struct WordTiming (int CharIndex, TimeSpan Offset);

    /// <summary>A chunk's audio, and when its words start if the engine said.</summary>
    public sealed record SynthesizedAudio (PcmFormat Format, byte[] Pcm, IReadOnlyList<WordTiming> Words)
    {
        public TimeSpan Duration => Format.Duration (Pcm.Length);
    }

    /// <summary>An engine that turns a chunk into audio (Windows SAPI, macOS say, Piper, the cloud engines).</summary>
    public interface ISpeechSynthesizer : IAsyncDisposable
    {
        string Id { get; }
        string Name { get; }
        TtsCapabilities Capabilities { get; }
        Task<IReadOnlyList<TtsVoice>> GetVoicesAsync (CancellationToken ct = default);
        Task<SynthesizedAudio> SynthesizeAsync (SpeechChunk chunk, TtsSettings settings, string? language, CancellationToken ct = default);
    }

    /// <summary>
    /// Plays PCM written to it, gaplessly, and says how much has played (plan 3.3). One per platform: a pipe to
    /// PipeWire/PulseAudio on Linux, waveOut on Windows, Audio Queue on macOS.
    /// </summary>
    public interface IAudioOutput : IAsyncDisposable
    {
        /// <summary>Starts (or restarts, after <see cref="StopAsync"/>) in <paramref name="format"/>.</summary>
        Task StartAsync (PcmFormat format, CancellationToken ct = default);

        /// <summary>Queues audio after what is already queued; may wait while the queue is full.</summary>
        ValueTask WriteAsync (ReadOnlyMemory<byte> pcm, CancellationToken ct = default);

        /// <summary>How much audio has been heard since <see cref="StartAsync"/> (it stands still while paused or starved).</summary>
        TimeSpan Played { get; }

        void Pause ();
        void Unpause ();

        /// <summary>Stops at once, dropping what is queued.</summary>
        Task StopAsync ();
    }

    /// <summary>Reading and writing RIFF WAVE files (the PCM some engines produce).</summary>
    public static class Wave
    {
        /// <summary>The PCM and format of a WAVE file: 16-bit integer PCM only.</summary>
        /// <exception cref="InvalidDataException">Not a 16-bit PCM WAVE file.</exception>
        public static (PcmFormat Format, byte[] Pcm) Read (ReadOnlySpan<byte> file)
        {
            if (file.Length < 12 || Encoding.ASCII.GetString (file[..4]) != "RIFF" || Encoding.ASCII.GetString (file[8..12]) != "WAVE")
                throw new InvalidDataException ("Not a WAVE file.");
            PcmFormat? format = null;
            var position = 12;
            while (position + 8 <= file.Length) {
                var id = Encoding.ASCII.GetString (file.Slice (position, 4));
                var size = BinaryPrimitives.ReadInt32LittleEndian (file.Slice (position + 4, 4));
                var body = position + 8;
                // Streamed WAVs (a pipe) give the data chunk a size of 0 or -1: it runs to the end.
                var length = size <= 0 || body + size > file.Length ? file.Length - body : size;
                if (id == "fmt ") {
                    var tag = BinaryPrimitives.ReadInt16LittleEndian (file.Slice (body, 2));
                    var channels = BinaryPrimitives.ReadInt16LittleEndian (file.Slice (body + 2, 2));
                    var rate = BinaryPrimitives.ReadInt32LittleEndian (file.Slice (body + 4, 4));
                    var bits = BinaryPrimitives.ReadInt16LittleEndian (file.Slice (body + 14, 2));
                    if (tag is not (1 or -2) || bits != 16)
                        throw new InvalidDataException ($"Only 16-bit integer PCM is supported (format {tag}, {bits} bits).");
                    format = new PcmFormat (rate, channels, bits);
                } else if (id == "data") {
                    if (format is null)
                        throw new InvalidDataException ("WAVE data before its format.");
                    return (format, file.Slice (body, length - length % format.BytesPerFrame).ToArray ());
                }
                position = body + length + (length & 1);
            }
            throw new InvalidDataException ("WAVE file without data.");
        }

        public static byte[] Write (PcmFormat format, ReadOnlySpan<byte> pcm)
        {
            var file = new byte[44 + pcm.Length];
            Encoding.ASCII.GetBytes ("RIFF").CopyTo (file, 0);
            BinaryPrimitives.WriteInt32LittleEndian (file.AsSpan (4), 36 + pcm.Length);
            Encoding.ASCII.GetBytes ("WAVEfmt ").CopyTo (file, 8);
            BinaryPrimitives.WriteInt32LittleEndian (file.AsSpan (16), 16);
            BinaryPrimitives.WriteInt16LittleEndian (file.AsSpan (20), 1);
            BinaryPrimitives.WriteInt16LittleEndian (file.AsSpan (22), (short)format.Channels);
            BinaryPrimitives.WriteInt32LittleEndian (file.AsSpan (24), format.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian (file.AsSpan (28), format.BytesPerSecond);
            BinaryPrimitives.WriteInt16LittleEndian (file.AsSpan (32), (short)format.BytesPerFrame);
            BinaryPrimitives.WriteInt16LittleEndian (file.AsSpan (34), (short)format.BitsPerSample);
            Encoding.ASCII.GetBytes ("data").CopyTo (file, 36);
            BinaryPrimitives.WriteInt32LittleEndian (file.AsSpan (40), pcm.Length);
            pcm.CopyTo (file.AsSpan (44));
            return file;
        }
    }
}
