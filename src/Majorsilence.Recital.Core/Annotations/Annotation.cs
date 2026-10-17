using System.Text.Json;
using System.Text.Json.Serialization;
using Majorsilence.Recital.Core.Sync;

namespace Majorsilence.Recital.Core.Annotations
{
    public enum AnnotationKind
    {
        /// <summary>A coloured passage, optionally with a note.</summary>
        Highlight,

        /// <summary>A marked page: a point, not a passage.</summary>
        Bookmark,
    }

    /// <summary>
    /// A highlight (with or without a note) or a bookmark, anchored by CFI (plan 1.4) so it lands on the
    /// same words at any page size. Stored in each device's <c>state/&lt;bookId&gt;/&lt;deviceId&gt;.json</c>
    /// and merged across devices by <see cref="Id"/>: the highest <see cref="Hlc"/> wins, except that a
    /// deletion is final (docs/SYNC_FORMAT.md).
    /// </summary>
    public sealed record Annotation
    {
        public required Guid Id { get; init; }
        public AnnotationKind Kind { get; init; } = AnnotationKind.Highlight;

        /// <summary>A range CFI for a highlight, a point CFI for a bookmark.</summary>
        public string? Cfi { get; init; }

        /// <summary>The spine item it is in, and the character offset of its start there: the fallback when the CFI no longer resolves.</summary>
        public int SpineIndex { get; init; }

        public int CharOffset { get; init; }

        /// <summary>How far through the book, for ordering and display.</summary>
        public double Percent { get; init; }

        /// <summary>The highlighted words (a bookmark: the start of the page), for lists and exports.</summary>
        public string? Text { get; init; }

        /// <summary>One of <see cref="HighlightColors"/>; null for a bookmark.</summary>
        public string? Color { get; init; }

        public string? Note { get; init; }

        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public Hlc Hlc { get; init; }

        /// <summary>A tombstone: deleted on some device. Kept, so the deletion reaches every device and stays.</summary>
        public bool Deleted { get; init; }

        /// <summary>Properties a newer Recital wrote, kept when this build rewrites the annotation.</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }

        public bool HasNote => !string.IsNullOrWhiteSpace (Note);
    }

    /// <summary>The highlight colours, by their stored names, as 0xAARRGGBB fills that read on light and dark pages.</summary>
    public static class HighlightColors
    {
        public const string Default = "yellow";

        public static IReadOnlyList<(string Name, string Label, uint Argb)> All { get; } = [
            ("yellow", "Yellow", 0x70F5D020),
            ("green", "Green", 0x6060C060),
            ("blue", "Blue", 0x6050A0F0),
            ("pink", "Pink", 0x60F070A8),
            ("purple", "Purple", 0x60A070E0),
        ];

        /// <summary>The fill for a stored colour name; unknown names (from a newer build) draw yellow.</summary>
        public static uint ArgbOf (string? name)
            => All.FirstOrDefault (c => c.Name == name).Argb is var argb and not 0 ? argb : All[0].Argb;
    }

    /// <summary>Merging annotations from every device's state file.</summary>
    public static class AnnotationMerge
    {
        /// <summary>
        /// One annotation per id: a tombstone if any device deleted it (deletion is final, so a device that
        /// was offline and still has the old version cannot bring it back), otherwise the version with the
        /// highest HLC.
        /// </summary>
        public static IReadOnlyDictionary<Guid, Annotation> Merge (IEnumerable<Annotation> versions)
        {
            var merged = new Dictionary<Guid, Annotation> ();
            foreach (var version in versions) {
                if (version.Id == Guid.Empty)
                    continue;
                if (!merged.TryGetValue (version.Id, out var current) || Wins (version, current))
                    merged[version.Id] = version;
            }
            return merged;
        }

        /// <summary>Whether <paramref name="candidate"/> replaces <paramref name="current"/>.</summary>
        public static bool Wins (Annotation candidate, Annotation current)
        {
            if (candidate.Deleted != current.Deleted)
                return candidate.Deleted;
            return candidate.Hlc > current.Hlc;
        }

        /// <summary>Live (not deleted) annotations in reading order.</summary>
        public static IReadOnlyList<Annotation> Live (IEnumerable<Annotation> merged)
            => merged.Where (a => !a.Deleted)
                .OrderBy (a => a.SpineIndex).ThenBy (a => a.CharOffset).ThenBy (a => a.CreatedAt).ThenBy (a => a.Id)
                .ToList ();
    }
}
