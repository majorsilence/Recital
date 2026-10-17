using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Majorsilence.Recital.Tts
{
    /// <summary>
    /// Synthesized chunks on disk, keyed by engine, voice, settings and text (plan 3.3), so listening again
    /// costs nothing and a BYO-key cloud engine is not paid twice. A local cache, never synced; the oldest
    /// files go when it grows past its limit.
    /// </summary>
    public sealed class AudioCache (string directory, long maxBytes = 512L * 1024 * 1024)
    {
        public string Directory { get; } = directory;

        public static string Key (string engine, TtsSettings settings, string? language, SpeechChunk chunk)
        {
            var text = string.Join ('\n', engine, settings.VoiceId, settings.DialogueVoiceId, settings.ClampedRate.ToString ("0.00", CultureInfo.InvariantCulture), language, chunk.Key);
            return Convert.ToHexStringLower (SHA256.HashData (Encoding.UTF8.GetBytes (text)));
        }

        public async Task<SynthesizedAudio?> TryGetAsync (string key, CancellationToken ct = default)
        {
            var path = PathOf (key);
            if (!File.Exists (path))
                return null;
            try {
                var bytes = await File.ReadAllBytesAsync (path, ct).ConfigureAwait (false);
                File.SetLastAccessTimeUtc (path, DateTime.UtcNow);
                return Decode (bytes);
            } catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) {
                return null;
            }
        }

        public async Task PutAsync (string key, SynthesizedAudio audio, CancellationToken ct = default)
        {
            System.IO.Directory.CreateDirectory (Directory);
            var path = PathOf (key);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync (temp, Encode (audio), ct).ConfigureAwait (false);
            File.Move (temp, path, overwrite: true);
            Trim ();
        }

        private string PathOf (string key) => Path.Combine (Directory, key + ".tts");

        /// <summary>Format, word timings, then PCM: [rate, channels, bits, word count, (char, ticks)*, pcm].</summary>
        private static byte[] Encode (SynthesizedAudio audio)
        {
            var header = 16 + audio.Words.Count * 12;
            var bytes = new byte[header + audio.Pcm.Length];
            var span = bytes.AsSpan ();
            BinaryPrimitives.WriteInt32LittleEndian (span, audio.Format.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian (span[4..], audio.Format.Channels);
            BinaryPrimitives.WriteInt32LittleEndian (span[8..], audio.Format.BitsPerSample);
            BinaryPrimitives.WriteInt32LittleEndian (span[12..], audio.Words.Count);
            for (var i = 0; i < audio.Words.Count; i++) {
                BinaryPrimitives.WriteInt32LittleEndian (span[(16 + i * 12)..], audio.Words[i].CharIndex);
                BinaryPrimitives.WriteInt64LittleEndian (span[(20 + i * 12)..], audio.Words[i].Offset.Ticks);
            }
            audio.Pcm.CopyTo (span[header..]);
            return bytes;
        }

        private static SynthesizedAudio Decode (byte[] bytes)
        {
            var span = bytes.AsSpan ();
            var format = new PcmFormat (BinaryPrimitives.ReadInt32LittleEndian (span), BinaryPrimitives.ReadInt32LittleEndian (span[4..]), BinaryPrimitives.ReadInt32LittleEndian (span[8..]));
            var count = BinaryPrimitives.ReadInt32LittleEndian (span[12..]);
            if (count < 0 || 16 + count * 12 > bytes.Length || format.BytesPerSecond <= 0)
                throw new InvalidDataException ("Damaged cache entry.");
            var words = new WordTiming[count];
            for (var i = 0; i < count; i++)
                words[i] = new WordTiming (BinaryPrimitives.ReadInt32LittleEndian (span[(16 + i * 12)..]), TimeSpan.FromTicks (BinaryPrimitives.ReadInt64LittleEndian (span[(20 + i * 12)..])));
            return new SynthesizedAudio (format, span[(16 + count * 12)..].ToArray (), words);
        }

        private void Trim ()
        {
            var files = new DirectoryInfo (Directory).EnumerateFiles ("*.tts").ToList ();
            var total = files.Sum (f => f.Length);
            foreach (var file in files.OrderBy (f => f.LastAccessTimeUtc)) {
                if (total <= maxBytes)
                    break;
                total -= file.Length;
                try {
                    file.Delete ();
                } catch (IOException) {
                    // In use: next time.
                }
            }
        }
    }
}
