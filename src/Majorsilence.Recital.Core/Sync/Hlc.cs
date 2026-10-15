using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Majorsilence.Recital.Core.Sync
{
    /// <summary>
    /// A hybrid logical clock timestamp: wall-clock milliseconds, a counter that orders events within one
    /// millisecond (or while the wall clock runs behind a timestamp already seen), and the device that made
    /// it as the final tie-break. Ordering is total, so "highest HLC wins" always picks one writer.
    /// </summary>
    [JsonConverter (typeof (HlcJsonConverter))]
    public readonly record struct Hlc (long WallMilliseconds, int Counter, string Node) : IComparable<Hlc>
    {
        public static readonly Hlc Zero = new (0, 0, string.Empty);

        public int CompareTo (Hlc other)
        {
            var c = WallMilliseconds.CompareTo (other.WallMilliseconds);
            if (c != 0)
                return c;
            c = Counter.CompareTo (other.Counter);
            return c != 0 ? c : string.CompareOrdinal (Node, other.Node);
        }

        public static bool operator < (Hlc a, Hlc b) => a.CompareTo (b) < 0;
        public static bool operator > (Hlc a, Hlc b) => a.CompareTo (b) > 0;
        public static bool operator <= (Hlc a, Hlc b) => a.CompareTo (b) <= 0;
        public static bool operator >= (Hlc a, Hlc b) => a.CompareTo (b) >= 0;

        /// <summary><c>0001760000000000-0000-node</c>: fixed-width numbers, so the strings sort like the clocks.</summary>
        public override string ToString ()
            => string.Create (CultureInfo.InvariantCulture, $"{WallMilliseconds:D16}-{Counter:D4}-{Node}");

        public static bool TryParse (string? text, out Hlc hlc)
        {
            hlc = Zero;
            if (text is null)
                return false;

            var first = text.IndexOf ('-');
            var second = first < 0 ? -1 : text.IndexOf ('-', first + 1);
            if (second < 0
                || !long.TryParse (text.AsSpan (0, first), NumberStyles.None, CultureInfo.InvariantCulture, out var wall)
                || !int.TryParse (text.AsSpan (first + 1, second - first - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
                return false;

            hlc = new Hlc (wall, counter, text[(second + 1)..]);
            return true;
        }

        public static Hlc Parse (string text)
            => TryParse (text, out var hlc) ? hlc : throw new FormatException ($"'{text}' is not an HLC timestamp.");
    }

    /// <summary>Issues <see cref="Hlc"/> timestamps for one device. Thread-safe.</summary>
    public sealed class HlcClock
    {
        private readonly object gate = new ();
        private readonly TimeProvider time;
        private Hlc last;

        public HlcClock (string node, TimeProvider? time = null)
        {
            Node = node;
            this.time = time ?? TimeProvider.System;
            last = new Hlc (0, 0, node);
        }

        public string Node { get; }

        /// <summary>A timestamp later than every one this clock has issued or observed.</summary>
        public Hlc Now ()
        {
            lock (gate) {
                var wall = time.GetUtcNow ().ToUnixTimeMilliseconds ();
                last = wall > last.WallMilliseconds
                    ? new Hlc (wall, 0, Node)
                    : new Hlc (last.WallMilliseconds, last.Counter + 1, Node);
                return last;
            }
        }

        /// <summary>Moves the clock past a timestamp read from another device, so the next local event orders after it.</summary>
        public void Observe (Hlc remote)
        {
            lock (gate) {
                if (remote.WallMilliseconds > last.WallMilliseconds
                    || remote.WallMilliseconds == last.WallMilliseconds && remote.Counter > last.Counter)
                    last = new Hlc (remote.WallMilliseconds, remote.Counter, Node);
            }
        }
    }

    internal sealed class HlcJsonConverter : JsonConverter<Hlc>
    {
        public override Hlc Read (ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Hlc.TryParse (reader.GetString (), out var hlc) ? hlc : throw new JsonException ("Not an HLC timestamp.");

        public override void Write (Utf8JsonWriter writer, Hlc value, JsonSerializerOptions options)
            => writer.WriteStringValue (value.ToString ());
    }
}
