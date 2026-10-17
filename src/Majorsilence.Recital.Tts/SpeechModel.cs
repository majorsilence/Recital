using AngleSharp.Dom;
using Majorsilence.Recital.Epub;

namespace Majorsilence.Recital.Tts
{
    public enum ChunkKind
    {
        Paragraph,
        Heading,
    }

    public enum ProsodyKind
    {
        /// <summary><c>em</c>, <c>i</c>, <c>cite</c>: moderate emphasis.</summary>
        Emphasis,

        /// <summary><c>strong</c>, <c>b</c>: strong emphasis.</summary>
        Strong,

        /// <summary>Quoted speech: optionally a different voice from the narration.</summary>
        Dialogue,
    }

    /// <summary>Characters <c>[Start, End)</c> of a chunk's text with a prosody.</summary>
    public readonly record struct ProsodySpan (int Start, int End, ProsodyKind Kind);

    /// <summary>Characters <c>[Start, End)</c> of a chunk's text that make one sentence.</summary>
    public readonly record struct SpeechSentence (int Start, int End);

    /// <summary>
    /// One unit sent to a speech engine: a paragraph (or part of a long one), with its sentences, prosody,
    /// pauses, and the DOM position of every character, so what is being spoken can be shown and saved.
    /// Neural voices plan intonation across what they are given, so chunks are paragraphs, not sentences
    /// (plan 3.2).
    /// </summary>
    public sealed class SpeechChunk
    {
        private readonly INode[] nodes;
        private readonly int[] offsets;

        internal SpeechChunk (int spineIndex, int index, ChunkKind kind, string text, INode[] nodes, int[] offsets,
            IReadOnlyList<SpeechSentence> sentences, IReadOnlyList<ProsodySpan> spans, TimeSpan pauseBefore, TimeSpan pauseAfter)
        {
            SpineIndex = spineIndex;
            Index = index;
            Kind = kind;
            Text = text;
            this.nodes = nodes;
            this.offsets = offsets;
            Sentences = sentences;
            Spans = spans;
            PauseBefore = pauseBefore;
            PauseAfter = pauseAfter;
        }

        public int SpineIndex { get; }

        /// <summary>Position of this chunk in its chapter's plan.</summary>
        public int Index { get; }

        public ChunkKind Kind { get; }
        public string Text { get; }
        public IReadOnlyList<SpeechSentence> Sentences { get; }
        public IReadOnlyList<ProsodySpan> Spans { get; }
        public TimeSpan PauseBefore { get; }
        public TimeSpan PauseAfter { get; }

        /// <summary>Identifies the chunk's content, for the audio cache.</summary>
        public string Key => $"{Kind}|{(int)PauseBefore.TotalMilliseconds}|{(int)PauseAfter.TotalMilliseconds}|{string.Join (",", Spans.Select (s => $"{s.Start}-{s.End}{(int)s.Kind}"))}|{Text}";

        /// <summary>The DOM position of character <paramref name="index"/>.</summary>
        public DomPosition PositionAt (int index)
        {
            index = Math.Clamp (index, 0, Text.Length - 1);
            return new DomPosition (nodes[index], offsets[index]);
        }

        /// <summary>The DOM position just after character <paramref name="end"/> - 1.</summary>
        public DomPosition PositionAfter (int end)
        {
            var last = Math.Clamp (end - 1, 0, Text.Length - 1);
            return nodes[last] is IText ? new DomPosition (nodes[last], offsets[last] + 1) : new DomPosition (nodes[last], 0);
        }

        /// <summary>The sentence holding character <paramref name="index"/> (the last one past the end).</summary>
        public int SentenceAt (int index)
        {
            for (var i = 0; i < Sentences.Count; i++)
                if (index < Sentences[i].End)
                    return i;
            return Sentences.Count - 1;
        }

        /// <summary>The word holding (or just after) character <paramref name="index"/>: <c>[start, end)</c>.</summary>
        public (int Start, int End) WordAt (int index)
        {
            index = Math.Clamp (index, 0, Text.Length - 1);
            while (index < Text.Length && char.IsWhiteSpace (Text[index]))
                index++;
            if (index >= Text.Length)
                return (Text.Length, Text.Length);
            var start = index;
            while (start > 0 && !char.IsWhiteSpace (Text[start - 1]))
                start--;
            var end = index;
            while (end < Text.Length && !char.IsWhiteSpace (Text[end]))
                end++;
            return (start, end);
        }

        /// <summary>The starts of the words, for engines that report progress by marks.</summary>
        public IEnumerable<int> WordStarts ()
        {
            for (var i = 0; i < Text.Length; i++)
                if (!char.IsWhiteSpace (Text[i]) && (i == 0 || char.IsWhiteSpace (Text[i - 1])))
                    yield return i;
        }

        /// <summary>This chunk from character <paramref name="from"/> on (a sentence start): where reading resumes mid-paragraph.</summary>
        public SpeechChunk Slice (int from)
        {
            if (from <= 0)
                return this;
            from = Math.Min (from, Text.Length - 1);
            var sentences = Sentences.Where (s => s.End > from).Select (s => new SpeechSentence (Math.Max (0, s.Start - from), s.End - from)).ToList ();
            var spans = Spans.Where (s => s.End > from).Select (s => new ProsodySpan (Math.Max (0, s.Start - from), s.End - from, s.Kind)).ToList ();
            return new SpeechChunk (SpineIndex, Index, Kind, Text[from..], nodes[from..], offsets[from..], sentences, spans, TimeSpan.Zero, PauseAfter) { SliceStart = from };
        }

        /// <summary>Where this chunk starts in the planned chunk it was sliced from (0 when not sliced).</summary>
        public int SliceStart { get; private init; }

        public override string ToString () => $"{SpineIndex}:{Index} {Kind} \"{(Text.Length > 40 ? Text[..40] + "…" : Text)}\"";
    }
}
