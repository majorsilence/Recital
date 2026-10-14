using AngleSharp.Dom;
using Majorsilence.Recital.Epub;

namespace Majorsilence.Recital.Layout
{
    /// <summary>Document order for DOM positions: an element comes before anything inside it.</summary>
    public static class DomOrder
    {
        public static int Compare (DomPosition a, DomPosition b)
        {
            if (ReferenceEquals (a.Node, b.Node))
                return a.Offset.CompareTo (b.Offset);

            var relation = a.Node.CompareDocumentPosition (b.Node);

            if (relation.HasFlag (DocumentPositions.Following) || relation.HasFlag (DocumentPositions.ContainedBy))
                return -1;
            if (relation.HasFlag (DocumentPositions.Preceding) || relation.HasFlag (DocumentPositions.Contains))
                return 1;
            return 0;
        }
    }
}
