using AngleSharp.Dom;

namespace Majorsilence.Recital.Epub
{
    /// <summary>
    /// A position in a content document: a character offset within a text node, or an element itself
    /// (offset 0), for positions on images and other non-text content.
    /// </summary>
    public readonly record struct DomPosition (INode Node, int Offset);

    /// <summary>One spine item's content document, parsed, with CFI generation and resolution.</summary>
    public sealed class ContentDocument
    {
        internal ContentDocument (EpubPackage package, SpineItem spineItem, IDocument document, bool parsedAsXml)
        {
            Package = package;
            SpineItem = spineItem;
            Document = document;
            ParsedAsXml = parsedAsXml;
        }

        public EpubPackage Package { get; }
        public SpineItem SpineItem { get; }
        public int SpineIndex => SpineItem.Index;

        /// <summary>
        /// The document as an AngleSharp HTML document. When <see cref="ParsedAsXml"/> is true its tree is
        /// exactly the source XML tree.
        /// </summary>
        public IDocument Document { get; }

        /// <summary>
        /// False when the source was not well-formed XML and went through the forgiving HTML parser, whose
        /// tree (and therefore CFIs) can differ from other readers'.
        /// </summary>
        public bool ParsedAsXml { get; }

        /// <summary>The element CFI steps are taken from (the html element).</summary>
        public IElement Root => Document.DocumentElement;

        /// <summary>Generates the CFI of a position in this document.</summary>
        public EpubCfi GetCfi (DomPosition position) => EpubCfi.Point (GetCfiPoint (position));

        public EpubCfi GetCfi (INode node, int offset = 0) => GetCfi (new DomPosition (node, offset));

        public EpubCfi GetRangeCfi (DomPosition start, DomPosition end)
            => EpubCfi.Range (GetCfiPoint (start), GetCfiPoint (end));

        public CfiPoint GetCfiPoint (DomPosition position)
        {
            var (node, offset) = position;

            var (element, chunk) = node switch {
                IElement e => (e, (Index: 0, Before: 0)),
                IText { ParentElement: { } parent } text => (parent, ChunkOf (text)),
                _ => throw new ArgumentException ("A position must be in a text node or on an element of this document.", nameof (position)),
            };

            var steps = new List<CfiStep> {
                new (Package.SpineCfiStep),
                new (SpineItem.CfiStep, SpineItem.ItemRefId),
            };
            steps.AddRange (ElementPath (element));

            CfiOffset? cfiOffset = null;

            if (node is IText textNode) {
                steps.Add (new CfiStep (chunk.Index));
                cfiOffset = new CfiOffset (chunk.Before + Math.Clamp (offset, 0, textNode.Length));
            } else if (steps.Count == 2) {
                throw new ArgumentException ("A position must be below the html element.", nameof (position));
            }

            return new CfiPoint (steps, [2], cfiOffset);
        }

        /// <summary>
        /// Resolves a CFI's start point (or the range end with <paramref name="end"/>) to a position in this
        /// document. Returns null when the CFI is for another spine item or its path does not exist here,
        /// even after following id assertions. A character offset past the end of its text is clamped.
        /// </summary>
        public DomPosition? Resolve (EpubCfi cfi, bool end = false)
        {
            var point = end ? cfi.End ?? cfi.Start : cfi.Start;
            return Resolve (point);
        }

        public DomPosition? Resolve (CfiPoint point)
        {
            var steps = point.ContentSteps.ToList ();
            if (steps.Count == 0)
                return null;

            IElement current = Root;

            for (var i = 0; i < steps.Count; i++) {
                var step = steps[i];
                var last = i == steps.Count - 1;

                if (!step.IsElement) {
                    // A text chunk is only meaningful as the final step.
                    if (!last)
                        return null;
                    return ResolveChunk (current, step.Index, point.Offset?.Character ?? 0);
                }

                var child = ElementChild (current, step.Index / 2 - 1);

                // A failed id assertion means the document changed; trust the id over the index.
                if (step.Id is { Length: > 0 } id && (child is null || child.Id != id))
                    child = Document.GetElementById (id) ?? child;

                if (child is null)
                    return null;

                current = child;
            }

            return new DomPosition (current, 0);
        }

        /// <summary>
        /// The number of text characters in the body before a position: the <c>charOffsetInChapter</c> of
        /// the fallback locator, used when a CFI no longer resolves. A position on an element counts the
        /// text before the element.
        /// </summary>
        public int GetCharOffset (DomPosition position)
        {
            var count = 0;
            foreach (var node in TextOrder ()) {
                if (node == position.Node)
                    return count + (node is IText text ? Math.Clamp (position.Offset, 0, text.Length) : 0);
                if (node is IText t)
                    count += t.Length;
            }
            return count;
        }

        /// <summary>The position <paramref name="offset"/> text characters into the body, clamped to its text.</summary>
        public DomPosition? PositionAtCharOffset (int offset)
        {
            IText? last = null;
            foreach (var node in TextOrder ()) {
                if (node is not IText text || text.Length == 0)
                    continue;
                if (offset < text.Length)
                    return new DomPosition (text, Math.Max (0, offset));
                offset -= text.Length;
                last = text;
            }
            return last is null ? null : new DomPosition (last, last.Length);
        }

        /// <summary>
        /// The text between two positions in document order, with runs of whitespace (including block
        /// boundaries) collapsed to one space: a highlight's excerpt.
        /// </summary>
        public string GetText (DomPosition start, DomPosition end)
        {
            var text = new System.Text.StringBuilder ();
            var on = false;
            foreach (var node in TextOrder ()) {
                var isStart = !on && node == start.Node;
                var isEnd = node == end.Node;
                if (isStart)
                    on = true;
                if (!on)
                    continue;

                if (node is IText t) {
                    var from = isStart ? Math.Clamp (start.Offset, 0, t.Length) : 0;
                    var to = isEnd ? Math.Clamp (end.Offset, from, t.Length) : t.Length;
                    text.Append (t.Data, from, to - from);
                } else if (node is IElement { LocalName: "br" or "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "td" or "tr" }) {
                    text.Append (' ');
                }
                if (isEnd)
                    break;
            }
            return System.Text.RegularExpressions.Regex.Replace (text.ToString (), @"\s+", " ").Trim ();
        }

        /// <summary>The number of text characters in the body.</summary>
        public int TextLength => text_length ??= TextOrder ().OfType<IText> ().Sum (t => t.Length);

        private int? text_length;

        // Elements and text nodes of the body (or the whole document without one) in document order.
        private IEnumerable<INode> TextOrder ()
        {
            var start = (INode?)Document.Body ?? Root;
            var stack = new Stack<INode> ();
            stack.Push (start);
            while (stack.Count > 0) {
                var node = stack.Pop ();
                yield return node;
                for (var i = node.ChildNodes.Length - 1; i >= 0; i--)
                    if (node.ChildNodes[i] is IElement or IText)
                        stack.Push (node.ChildNodes[i]);
            }
        }

        /// <summary>The element a <c>#fragment</c> names, if any.</summary>
        public IElement? FindFragment (string fragment) => Document.GetElementById (fragment);

        private List<CfiStep> ElementPath (IElement target)
        {
            var path = new List<CfiStep> ();

            for (var element = target; element is not null && element != Root; element = element.ParentElement) {
                if (element.ParentElement is null)
                    throw new ArgumentException ("The node is not in this document.");

                path.Add (new CfiStep (2 * (ElementIndex (element) + 1), string.IsNullOrEmpty (element.Id) ? null : element.Id));
            }

            path.Reverse ();
            return path;
        }

        private static int ElementIndex (IElement element)
        {
            var index = 0;
            for (var sibling = element.PreviousElementSibling; sibling is not null; sibling = sibling.PreviousElementSibling)
                index++;
            return index;
        }

        private static IElement? ElementChild (IElement parent, int index)
        {
            if (index < 0)
                return null;

            var i = 0;
            foreach (var child in parent.Children) {
                if (i == index)
                    return child;
                i++;
            }
            return null;
        }

        /// <summary>
        /// The odd CFI index of the chunk a text node is in, and the number of characters in that chunk's
        /// earlier text nodes. A chunk is everything between two child elements (comments do not split it).
        /// </summary>
        private static (int Index, int Before) ChunkOf (IText text)
        {
            var elementsBefore = 0;
            var charactersBefore = 0;
            var inChunk = true;

            for (var sibling = text.PreviousSibling; sibling is not null; sibling = sibling.PreviousSibling) {
                if (sibling is IElement) {
                    elementsBefore++;
                    inChunk = false;
                } else if (inChunk && sibling is IText earlier) {
                    charactersBefore += earlier.Length;
                }
            }

            return (2 * elementsBefore + 1, charactersBefore);
        }

        private static DomPosition ResolveChunk (IElement parent, int chunkIndex, int offset)
        {
            var elementsToSkip = (chunkIndex - 1) / 2;
            IText? lastText = null;
            var remaining = offset;
            var elementsSeen = 0;
            INode? following = null;

            foreach (var child in parent.ChildNodes) {
                if (child is IElement) {
                    if (elementsSeen == elementsToSkip) {
                        following = child;
                        break;
                    }
                    elementsSeen++;
                    continue;
                }

                if (elementsSeen != elementsToSkip || child is not IText text)
                    continue;

                if (remaining <= text.Length)
                    return new DomPosition (text, remaining);

                remaining -= text.Length;
                lastText = text;
            }

            // Offset past the chunk's end: clamp to its last character.
            if (lastText is not null)
                return new DomPosition (lastText, lastText.Length);

            // An empty chunk (no text between these elements): the nearest element.
            return new DomPosition (following ?? parent, 0);
        }
    }
}
