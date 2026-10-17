using System.Text;
using AngleSharp.Dom;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Layout;

namespace Majorsilence.Recital.Tts
{
    /// <summary>
    /// Turns a chapter into <see cref="SpeechChunk"/>s (plan 3.1, 3.2): one per paragraph or heading, split at
    /// sentence ends when longer than an engine accepts. The rule-based prosody annotator runs as part of
    /// it: <c>em</c>/<c>i</c> and <c>strong</c>/<c>b</c> become emphasis, quoted speech becomes dialogue,
    /// headings get pauses around them, and scene breaks (<c>hr</c>, <c>* * *</c>) a pause before what follows.
    /// </summary>
    /// <remarks>
    /// Block structure comes from element names, not CSS (the planner does not lay out). Text that is not
    /// read aloud is skipped: footnotes and endnotes (<c>epub:type</c>), note references, ruby annotations,
    /// scripts, styles, and anything <c>hidden</c>.
    /// </remarks>
    public static class UtterancePlanner
    {
        public static readonly TimeSpan HeadingPause = TimeSpan.FromMilliseconds (700);
        public static readonly TimeSpan SceneBreakPause = TimeSpan.FromMilliseconds (1200);

        private static readonly HashSet<string> Blocks = new (StringComparer.OrdinalIgnoreCase) {
            "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "li", "ul", "ol", "dl", "dt", "dd", "blockquote",
            "section", "article", "header", "footer", "main", "aside", "nav", "figure", "figcaption", "table",
            "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "pre", "address", "hgroup", "body",
        };

        private static readonly HashSet<string> Skipped = new (StringComparer.OrdinalIgnoreCase) {
            "script", "style", "head", "rt", "rp", "template", "svg", "math", "img", "audio", "video",
        };

        /// <param name="maxChunkChars">The longest chunk the engine accepts; longer paragraphs split at sentence ends.</param>
        public static IReadOnlyList<SpeechChunk> Plan (ContentDocument document, int maxChunkChars = 2000)
        {
            var builder = new Builder (document.SpineIndex, Math.Max (200, maxChunkChars));
            builder.Walk ((INode?)document.Document.Body ?? document.Root);
            builder.Flush (ChunkKind.Paragraph);
            return builder.Chunks;
        }

        /// <summary>The index of the chunk holding <paramref name="position"/> (or the first after it), and the sentence in it to start at.</summary>
        public static (int Chunk, int Sentence) Locate (IReadOnlyList<SpeechChunk> chunks, DomPosition position)
        {
            for (var c = 0; c < chunks.Count; c++) {
                var chunk = chunks[c];
                if (DomOrder.Compare (chunk.PositionAfter (chunk.Text.Length), position) <= 0)
                    continue;
                for (var s = 0; s < chunk.Sentences.Count; s++)
                    if (DomOrder.Compare (chunk.PositionAfter (chunk.Sentences[s].End), position) > 0)
                        return (c, s);
                return (c, chunk.Sentences.Count - 1);
            }
            return (chunks.Count, 0);
        }

        private sealed class Builder (int spine, int max)
        {
            private readonly StringBuilder text = new ();
            private readonly List<INode> nodes = [];
            private readonly List<int> offsets = [];
            private readonly List<ProsodySpan> spans = [];
            private readonly Stack<(ProsodyKind Kind, int Start)> open = new ();
            private bool after_space = true;
            private TimeSpan pending_pause;

            public List<SpeechChunk> Chunks { get; } = [];

            public void Walk (INode node)
            {
                foreach (var child in node.ChildNodes) {
                    switch (child) {
                    case IText t:
                        Append (t);
                        break;
                    case IElement e:
                        Element (e);
                        break;
                    }
                }
            }

            private void Element (IElement e)
            {
                var name = e.LocalName;
                if (Skipped.Contains (name) || e.HasAttribute ("hidden") || IsNote (e))
                    return;
                if (name == "br") {
                    Space (e);
                    return;
                }
                if (name == "hr") {
                    Flush (ChunkKind.Paragraph);
                    pending_pause += SceneBreakPause;
                    return;
                }

                var heading = name.Length == 2 && name[0] is 'h' or 'H' && name[1] is >= '1' and <= '6';
                if (Blocks.Contains (name)) {
                    Flush (ChunkKind.Paragraph);
                    Walk (e);
                    Flush (heading ? ChunkKind.Heading : ChunkKind.Paragraph);
                    return;
                }

                ProsodyKind? kind = name switch {
                    "em" or "i" or "cite" or "dfn" => ProsodyKind.Emphasis,
                    "strong" or "b" => ProsodyKind.Strong,
                    _ => null,
                };
                if (kind is { } k)
                    open.Push ((k, text.Length));
                Walk (e);
                if (kind is not null) {
                    var (opened, start) = open.Pop ();
                    var end = text.Length;
                    while (end > start && text[end - 1] == ' ')
                        end--;
                    if (end > start)
                        spans.Add (new ProsodySpan (start, end, opened));
                }
            }

            /// <summary>Footnotes, endnotes and the reference marks pointing at them are not read in the flow.</summary>
            private static bool IsNote (IElement e)
            {
                var type = e.GetAttribute ("http://www.idpf.org/2007/ops", "type") ?? e.GetAttribute ("epub:type") ?? "";
                return type.Contains ("noteref", StringComparison.Ordinal) || type.Contains ("footnote", StringComparison.Ordinal)
                    || type.Contains ("endnote", StringComparison.Ordinal) || type.Contains ("rearnote", StringComparison.Ordinal)
                    || type.Contains ("pagebreak", StringComparison.Ordinal);
            }

            private void Append (IText node)
            {
                var data = node.Data;
                for (var i = 0; i < data.Length; i++) {
                    var c = data[i];
                    if (char.IsWhiteSpace (c) || c == '­') {
                        if (c != '­' && !after_space && text.Length > 0) {
                            Add (' ', node, i);
                            after_space = true;
                        }
                        continue;
                    }
                    Add (c, node, i);
                    after_space = false;
                }
            }

            private void Space (INode node)
            {
                if (!after_space && text.Length > 0) {
                    Add (' ', node, 0);
                    after_space = true;
                }
            }

            private void Add (char c, INode node, int offset)
            {
                text.Append (c);
                nodes.Add (node);
                offsets.Add (offset);
            }

            public void Flush (ChunkKind kind)
            {
                // Trailing space out; spans still open (a paragraph inside an em) close here.
                while (text.Length > 0 && text[^1] == ' ') {
                    text.Length--;
                    nodes.RemoveAt (nodes.Count - 1);
                    offsets.RemoveAt (offsets.Count - 1);
                }
                var content = text.ToString ();
                var spansNow = spans.Concat (open.Select (o => new ProsodySpan (o.Start, content.Length, o.Kind)))
                    .Where (s => s.End > s.Start && s.Start < content.Length).ToList ();
                text.Clear ();
                spans.Clear ();
                var opened = open.ToArray ();
                open.Clear ();
                foreach (var o in opened.Reverse ())
                    open.Push ((o.Kind, 0));
                var chunkNodes = nodes.ToArray ();
                var chunkOffsets = offsets.ToArray ();
                nodes.Clear ();
                offsets.Clear ();
                after_space = true;

                if (content.Length == 0 || !content.Any (char.IsLetterOrDigit)) {
                    // "* * *", "⁂": a scene break, heard as a pause.
                    if (content.Length > 0)
                        pending_pause += SceneBreakPause;
                    return;
                }

                spansNow.AddRange (Dialogue (content));
                var sentences = SentenceSplitter.Split (content);
                var before = pending_pause + (kind == ChunkKind.Heading ? HeadingPause : TimeSpan.Zero);
                var after = kind == ChunkKind.Heading ? HeadingPause : TimeSpan.Zero;
                pending_pause = TimeSpan.Zero;

                // Split at sentence ends to fit the engine; each piece keeps its sentences, spans and positions.
                var pieceStart = 0;
                var pieceSentences = new List<SpeechSentence> ();
                for (var i = 0; i < sentences.Count; i++) {
                    var sentence = sentences[i];
                    if (pieceSentences.Count > 0 && sentence.End - pieceStart > max) {
                        Emit (kind, content, chunkNodes, chunkOffsets, spansNow, pieceStart, pieceSentences[^1].End, pieceSentences, before, TimeSpan.Zero);
                        before = TimeSpan.Zero;
                        pieceStart = sentence.Start;
                        pieceSentences = [];
                    }
                    pieceSentences.Add (sentence);
                }
                if (pieceSentences.Count > 0)
                    Emit (kind, content, chunkNodes, chunkOffsets, spansNow, pieceStart, content.Length, pieceSentences, before, after);
            }

            private void Emit (ChunkKind kind, string content, INode[] allNodes, int[] allOffsets, List<ProsodySpan> allSpans,
                int start, int end, List<SpeechSentence> sentences, TimeSpan before, TimeSpan after)
            {
                var piece = content[start..end];
                var trimmed = piece.TrimEnd ();
                end = start + trimmed.Length;
                var pieceSpans = allSpans.Where (s => s.End > start && s.Start < end)
                    .Select (s => new ProsodySpan (Math.Max (0, s.Start - start), Math.Min (end, s.End) - start, s.Kind))
                    .OrderBy (s => s.Start).ToList ();
                var pieceSentences = sentences.Select (s => new SpeechSentence (s.Start - start, Math.Min (s.End, end) - start)).ToList ();
                Chunks.Add (new SpeechChunk (spine, Chunks.Count, kind, trimmed, allNodes[start..end], allOffsets[start..end], pieceSentences, pieceSpans, before, after));
            }

            /// <summary>Quoted speech: “...”, «...», and straight double quotes in pairs. An unclosed quote runs to the end (it continues in the next paragraph).</summary>
            private static IEnumerable<ProsodySpan> Dialogue (string content)
            {
                var start = -1;
                for (var i = 0; i < content.Length; i++) {
                    var c = content[i];
                    var opens = c is '“' or '«' || c == '"' && start < 0;
                    var closes = c is '”' or '»' || c == '"' && start >= 0;
                    if (start < 0 && opens)
                        start = i;
                    else if (start >= 0 && closes) {
                        yield return new ProsodySpan (start, i + 1, ProsodyKind.Dialogue);
                        start = -1;
                    }
                }
                if (start >= 0)
                    yield return new ProsodySpan (start, content.Length, ProsodyKind.Dialogue);
            }
        }
    }
}
