using System.Security.Cryptography;

namespace Majorsilence.Recital.Tts.Kokoro
{
    /// <summary>
    /// The model and voice files, pinned to one revision and checked by SHA-256: the same list
    /// build/Kokoro.targets bundles on desktop (a test keeps the two equal). Phones and tablets do not bundle
    /// them; they are fetched only when the user asks for Recital's voices (D-033).
    /// </summary>
    public static class KokoroFiles
    {
        public const string Revision = "1939ad2a8e416c0acfeecc08a694d14ef25f2231";
        private const string Base = "https://huggingface.co/onnx-community/Kokoro-82M-v1.0-ONNX/resolve/" + Revision;

        /// <param name="Path">Where it goes in the voices folder.</param>
        /// <param name="Source">Its path in the pinned revision.</param>
        public sealed record File (string Path, string Source, string Sha256, long Size)
        {
            public Uri Url => new ($"{Base}/{Source}");
        }

        public static IReadOnlyList<File> All { get; } = [
            new ("model.onnx", "onnx/model_uint8f16.onnx", "883333e03c597584b532eebea0f8310f25f0c9ade58fe864792c12d969944a9a", 114_209_226),
            Voice ("af_heart", "d583ccff3cdca2f7fae535cb998ac07e9fcb90f09737b9a41fa2734ec44a8f0b"),
            Voice ("af_bella", "f69d836209b78eb8c66e75e3cda491e26ea838a3674257e9d4e5703cbaf55c8b"),
            Voice ("am_michael", "1d1f21dd8da39c30705cd4c75d039d265e9bc4a2a93ed09bc9e1b1225eb95ba1"),
            Voice ("am_puck", "fcf73c989033e9233e0b98713eca600c8c74dcc1614b37009d5450ff4a2274a0"),
            Voice ("bf_emma", "669fe0647f9dd04fcab92f1439a40eeb4c8b4ab1f82e4996fe3d918ce4a63b73"),
            Voice ("bm_george", "c4b235a4c1f2cd3b939fed08b899ce9385638b763f7b73a59616c4fc9bd6c9bc"),
        ];

        private static File Voice (string id, string sha256) => new ($"voices/{id}.bin", $"voices/{id}.bin", sha256, 522_240);

        public static long TotalSize => All.Sum (f => f.Size);

        /// <summary>
        /// Fetches every file <paramref name="folder"/> lacks, each to a <c>.part</c> file that is checked against
        /// its SHA-256 before it takes its place, so the folder never holds a partial or altered file.
        /// </summary>
        /// <param name="progress">Bytes fetched so far (counting files already present) out of <see cref="TotalSize"/>.</param>
        public static Task DownloadAsync (HttpClient http, string folder, IProgress<long>? progress = null, CancellationToken ct = default)
            => DownloadAsync (http, folder, All, progress, ct);

        internal static async Task DownloadAsync (HttpClient http, string folder, IReadOnlyList<File> files, IProgress<long>? progress, CancellationToken ct)
        {
            long done = 0;
            foreach (var file in files) {
                var final = System.IO.Path.Combine (folder, file.Path);
                if (System.IO.File.Exists (final)) {
                    done += file.Size;
                    progress?.Report (done);
                    continue;
                }
                Directory.CreateDirectory (System.IO.Path.GetDirectoryName (final)!);
                var part = final + ".part";
                try {
                    using (var response = await http.GetAsync (file.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait (false)) {
                        response.EnsureSuccessStatusCode ();
                        await using var source = await response.Content.ReadAsStreamAsync (ct).ConfigureAwait (false);
                        await using var target = System.IO.File.Create (part);
                        using var hash = IncrementalHash.CreateHash (HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync (buffer, ct).ConfigureAwait (false)) > 0) {
                            hash.AppendData (buffer, 0, read);
                            await target.WriteAsync (buffer.AsMemory (0, read), ct).ConfigureAwait (false);
                            done += read;
                            progress?.Report (done);
                        }
                        if (!Convert.ToHexStringLower (hash.GetHashAndReset ()).Equals (file.Sha256, StringComparison.Ordinal))
                            throw new InvalidDataException ($"{file.Path} did not match its checksum; it was not kept.");
                    }
                    System.IO.File.Move (part, final, overwrite: true);
                } finally {
                    if (System.IO.File.Exists (part))
                        System.IO.File.Delete (part);
                }
            }
        }
    }
}
