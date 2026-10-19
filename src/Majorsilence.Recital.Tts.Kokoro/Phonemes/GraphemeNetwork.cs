using System.Buffers.Binary;
using System.Text.Json;

namespace Majorsilence.Recital.Tts.Kokoro.Phonemes
{
    /// <summary>
    /// Spells out a word the dictionaries do not have: a tiny letters-to-phonemes network (BART, one encoder and one
    /// decoder layer, 128 wide; about 750k weights) trained on misaki's dictionaries, run here in plain C# so it needs
    /// no runtime and works everywhere. The weights are PeterReid/graphemes_to_phonemes_en_us (Apache-2.0).
    /// </summary>
    internal sealed class GraphemeNetwork
    {
        private const int Width = 128;
        private const int PositionOffset = 2;   // BART's learned positions start at index 2
        private const int Bos = 1, Eos = 2, Unknown = 3;

        // Each model's own vocabularies (its config.json): an id is the position in these strings, the first four
        // being pad, <s>, </s> and <unk>. The British model has Q (əʊ), ɒ and ː, and no flap or glottal stop.
        private const string AmericanGraphemes = "____AIOWYbdfhijklmnpstuvwz'-.BCDEFGHJKLMNPQRSTUVXZacegoqrxy";
        private const string AmericanPhonemes = "____AIOWYbdfhijklmnpstuvwzæðŋɑɔəɛɜɡɪɹɾʃʊʌʒʔʤʧˈˌθᵊᵻ";
        private const string BritishGraphemes = "____AIQWYabdfhijklmnpstuvwz'-.BCDEFGHJKLMNOPRSTUVXZcegoqrxy";
        private const string BritishPhonemes = "____AIQWYabdfhijklmnpstuvwzðŋɑɒɔəɛɜɡɪɹʃʊʌʒʤʧˈˌːθᵊ";

        private readonly string grapheme_chars;
        private readonly string phoneme_chars;
        private readonly Dictionary<string, float[]> w;
        private readonly int max_positions;
        private readonly int vocab;

        private GraphemeNetwork (Dictionary<string, float[]> weights, string graphemes, string phonemes)
        {
            grapheme_chars = graphemes;
            phoneme_chars = phonemes;
            w = weights;
            max_positions = weights["model.encoder.embed_positions.weight"].Length / Width - PositionOffset;
            vocab = weights["model.shared.weight"].Length / Width;
        }

        /// <summary>PeterReid/graphemes_to_phonemes_en_us.</summary>
        public static GraphemeNetwork LoadAmerican (Stream safetensors) => new (ReadSafetensors (safetensors), AmericanGraphemes, AmericanPhonemes);

        /// <summary>PeterReid/graphemes_to_phonemes_en_gb: the same shape, other vocabularies.</summary>
        public static GraphemeNetwork LoadBritish (Stream safetensors) => new (ReadSafetensors (safetensors), BritishGraphemes, BritishPhonemes);

        /// <summary>The phonemes for <paramref name="word"/>, or null when it has no letters the network knows.</summary>
        public string? Phonemize (string word)
        {
            var input = new List<int> { Bos };
            foreach (var c in word) {
                var id = grapheme_chars.IndexOf (c, 4);
                input.Add (id < 0 ? Unknown : id);
            }
            input.Add (Eos);
            if (input.Count > max_positions || input.All (i => i is Bos or Eos or Unknown))
                return null;

            var memory = Encode (input);
            var decoder = new Decoder (this, memory);
            var phonemes = new System.Text.StringBuilder ();
            var next = Bos;
            for (var position = 0; position < max_positions - 1; position++) {
                next = decoder.Step (next, position);
                if (next == Eos)
                    break;
                if (next > Unknown && next < phoneme_chars.Length)
                    phonemes.Append (phoneme_chars[next]);
            }
            return phonemes.Length == 0 ? null : phonemes.ToString ();
        }

        private float[][] Encode (List<int> tokens)
        {
            var x = Embed (tokens, "model.encoder");
            x = AddNorm (x, Attention (x, x, "model.encoder.layers.0.self_attn", causal: false), "model.encoder.layers.0.self_attn_layer_norm");
            return AddNorm (x, FeedForward (x, "model.encoder.layers.0"), "model.encoder.layers.0.final_layer_norm");
        }

        /// <summary>
        /// Greedy decoding one token at a time, keeping each step's keys and values (and the encoder's, projected
        /// once for the cross-attention), so a step costs one position rather than the whole prefix.
        /// </summary>
        private sealed class Decoder (GraphemeNetwork net, float[][] memory)
        {
            private const string Layer = "model.decoder.layers.0";
            private readonly float[][] memory_keys = net.Linear (memory, Layer + ".encoder_attn.k_proj");
            private readonly float[][] memory_values = net.Linear (memory, Layer + ".encoder_attn.v_proj");
            private readonly List<float[]> keys = [];
            private readonly List<float[]> values = [];

            public int Step (int token, int position)
            {
                float[][] y = [net.EmbedOne (token, position, "model.decoder")];
                var query = net.Linear (y, Layer + ".self_attn.q_proj")[0];
                keys.Add (net.Linear (y, Layer + ".self_attn.k_proj")[0]);
                values.Add (net.Linear (y, Layer + ".self_attn.v_proj")[0]);
                y = net.AddNorm (y, net.Linear ([Attend (query, keys, values)], Layer + ".self_attn.out_proj"), Layer + ".self_attn_layer_norm");

                var cross = net.Linear (y, Layer + ".encoder_attn.q_proj")[0];
                y = net.AddNorm (y, net.Linear ([Attend (cross, memory_keys, memory_values)], Layer + ".encoder_attn.out_proj"), Layer + ".encoder_attn_layer_norm");
                y = net.AddNorm (y, net.FeedForward (y, Layer), Layer + ".final_layer_norm");

                // The output projection is the shared embedding (tied), plus a bias.
                var last = y[0];
                var shared = net.w["model.shared.weight"];
                var bias = net.w["final_logits_bias"];
                var best = Eos;
                var bestScore = float.NegativeInfinity;
                for (var t = 0; t < net.vocab; t++) {
                    var score = bias[t] + Dot (last, shared, t * Width);
                    if (score > bestScore) {
                        bestScore = score;
                        best = t;
                    }
                }
                return best;
            }

            private static float[] Attend (float[] query, IReadOnlyList<float[]> keys, IReadOnlyList<float[]> values)
            {
                var scale = 1 / MathF.Sqrt (Width);
                var scores = new float[keys.Count];
                var max = float.NegativeInfinity;
                for (var j = 0; j < keys.Count; j++)
                    max = MathF.Max (max, scores[j] = Dot (query, keys[j], 0) * scale);
                var sum = 0f;
                for (var j = 0; j < scores.Length; j++)
                    sum += scores[j] = MathF.Exp (scores[j] - max);
                var mixed = new float[Width];
                for (var j = 0; j < scores.Length; j++) {
                    var p = scores[j] / sum;
                    var v = values[j];
                    for (var d = 0; d < Width; d++)
                        mixed[d] += p * v[d];
                }
                return mixed;
            }
        }

        private float[] EmbedOne (int token, int position, string prefix)
        {
            var shared = w["model.shared.weight"];
            var positions = w[prefix + ".embed_positions.weight"];
            var x = new float[Width];
            for (var d = 0; d < Width; d++)
                x[d] = shared[token * Width + d] + positions[(position + PositionOffset) * Width + d];
            return Norm ([x], prefix + ".layernorm_embedding")[0];
        }

        private float[][] Embed (List<int> tokens, string prefix)
        {
            var shared = w["model.shared.weight"];
            var positions = w[prefix + ".embed_positions.weight"];
            var x = new float[tokens.Count][];
            for (var i = 0; i < tokens.Count; i++) {
                x[i] = new float[Width];
                for (var d = 0; d < Width; d++)
                    x[i][d] = shared[tokens[i] * Width + d] + positions[(i + PositionOffset) * Width + d];
            }
            return Norm (x, prefix + ".layernorm_embedding");
        }

        private float[][] Attention (float[][] query, float[][] keys, string prefix, bool causal)
        {
            var scale = 1 / MathF.Sqrt (Width);   // one head, the full width
            var q = Linear (query, prefix + ".q_proj");
            var k = Linear (keys, prefix + ".k_proj");
            var v = Linear (keys, prefix + ".v_proj");
            var result = new float[query.Length][];
            var scores = new float[keys.Length];
            for (var i = 0; i < query.Length; i++) {
                var count = causal ? i + 1 : keys.Length;
                var max = float.NegativeInfinity;
                for (var j = 0; j < count; j++) {
                    scores[j] = Dot (q[i], k[j], 0) * scale;
                    max = MathF.Max (max, scores[j]);
                }
                var sum = 0f;
                for (var j = 0; j < count; j++)
                    sum += scores[j] = MathF.Exp (scores[j] - max);
                var mixed = new float[Width];
                for (var j = 0; j < count; j++) {
                    var p = scores[j] / sum;
                    for (var d = 0; d < Width; d++)
                        mixed[d] += p * v[j][d];
                }
                result[i] = mixed;
            }
            return Linear (result, prefix + ".out_proj");
        }

        private float[][] FeedForward (float[][] x, string prefix)
        {
            var hidden = Linear (x, prefix + ".fc1");
            foreach (var row in hidden)
                for (var d = 0; d < row.Length; d++)
                    row[d] = Gelu (row[d]);
            return Linear (hidden, prefix + ".fc2");
        }

        // BART normalises after each residual (post-norm).
        private float[][] AddNorm (float[][] x, float[][] sublayer, string norm)
        {
            for (var i = 0; i < x.Length; i++)
                for (var d = 0; d < Width; d++)
                    sublayer[i][d] += x[i][d];
            return Norm (sublayer, norm);
        }

        private float[][] Norm (float[][] x, string prefix)
        {
            var gain = w[prefix + ".weight"];
            var bias = w[prefix + ".bias"];
            foreach (var row in x) {
                var mean = row.Average ();
                var variance = row.Select (v => (v - mean) * (v - mean)).Average ();
                var inv = 1 / MathF.Sqrt (variance + 1e-5f);
                for (var d = 0; d < row.Length; d++)
                    row[d] = (row[d] - mean) * inv * gain[d] + bias[d];
            }
            return x;
        }

        // PyTorch's Linear: weight is [out, in].
        private float[][] Linear (float[][] x, string prefix)
        {
            var weight = w[prefix + ".weight"];
            var bias = w[prefix + ".bias"];
            var outputs = bias.Length;
            var result = new float[x.Length][];
            for (var i = 0; i < x.Length; i++) {
                var row = new float[outputs];
                for (var o = 0; o < outputs; o++)
                    row[o] = bias[o] + Dot (x[i], weight, o * x[i].Length);
                result[i] = row;
            }
            return result;
        }

        private static float Dot (float[] a, float[] b, int offset)
        {
            var x = a.AsSpan ();
            var y = b.AsSpan (offset, a.Length);
            var acc = System.Numerics.Vector<float>.Zero;
            var width = System.Numerics.Vector<float>.Count;
            var d = 0;
            for (; d <= x.Length - width; d += width)
                acc += new System.Numerics.Vector<float> (x[d..]) * new System.Numerics.Vector<float> (y[d..]);
            var sum = System.Numerics.Vector.Sum (acc);
            for (; d < x.Length; d++)
                sum += x[d] * y[d];
            return sum;
        }

        // The exact GELU, x·Φ(x).
        private static float Gelu (float x) => 0.5f * x * (1 + Erf (x / MathF.Sqrt (2)));

        // Abramowitz and Stegun 7.1.26: absolute error under 1.5e-7, well below float noise here.
        private static float Erf (float x)
        {
            var sign = MathF.Sign (x);
            x = MathF.Abs (x);
            var t = 1 / (1 + 0.3275911f * x);
            var y = 1 - ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp (-x * x);
            return sign * y;
        }

        /// <summary>Reads float32 tensors from a safetensors file: a little-endian length, a JSON header, then the data.</summary>
        private static Dictionary<string, float[]> ReadSafetensors (Stream stream)
        {
            using var memory = new MemoryStream ();
            stream.CopyTo (memory);
            var bytes = memory.GetBuffer ().AsSpan (0, (int) memory.Length);
            var headerLength = checked ((int) BinaryPrimitives.ReadUInt64LittleEndian (bytes));
            using var header = JsonDocument.Parse (bytes.Slice (8, headerLength).ToArray ());
            var data = bytes[(8 + headerLength)..];
            var tensors = new Dictionary<string, float[]> (StringComparer.Ordinal);
            foreach (var tensor in header.RootElement.EnumerateObject ()) {
                if (tensor.Name == "__metadata__")
                    continue;
                if (tensor.Value.GetProperty ("dtype").GetString () != "F32")
                    throw new InvalidDataException ($"{tensor.Name}: only F32 tensors are supported.");
                var offsets = tensor.Value.GetProperty ("data_offsets");
                var start = offsets[0].GetInt32 ();
                var end = offsets[1].GetInt32 ();
                var values = new float[(end - start) / 4];
                for (var i = 0; i < values.Length; i++)
                    values[i] = BinaryPrimitives.ReadSingleLittleEndian (data.Slice (start + i * 4, 4));
                tensors[tensor.Name] = values;
            }
            return tensors;
        }
    }
}
