namespace Majorsilence.Recital.Core.Library
{
    public enum LibrarySort
    {
        /// <summary>Most recently read (or added) first.</summary>
        Recent,
        Title,
        Author,
        /// <summary>Furthest read first.</summary>
        Progress,
    }

    /// <summary>The library screen's search box and sort order.</summary>
    public static class LibraryQuery
    {
        /// <summary>
        /// Books whose title or authors contain every word of <paramref name="search"/> (any case, any
        /// order), sorted. Ties fall back to the title, then the id, so the order is stable.
        /// </summary>
        public static IReadOnlyList<LibraryBook> Apply (IEnumerable<LibraryBook> books, string? search, LibrarySort sort)
        {
            var words = (search ?? string.Empty).Split ((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var matching = books.Where (book => words.All (word =>
                book.Title.Contains (word, StringComparison.CurrentCultureIgnoreCase)
                || book.Authors.Contains (word, StringComparison.CurrentCultureIgnoreCase)));

            var ordered = sort switch {
                LibrarySort.Title => matching.OrderBy (b => SortableTitle (b.Title), StringComparer.CurrentCultureIgnoreCase),
                LibrarySort.Author => matching.OrderBy (b => SortableAuthor (b), StringComparer.CurrentCultureIgnoreCase),
                LibrarySort.Progress => matching.OrderByDescending (b => b.Percent),
                _ => matching.OrderByDescending (b => b.LastActivity),
            };

            return ordered.ThenBy (b => SortableTitle (b.Title), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy (b => b.Id, StringComparer.Ordinal)
                .ToList ();
        }

        /// <summary>A title without a leading English article: "The Time Machine" files under T for Time.</summary>
        public static string SortableTitle (string title)
        {
            foreach (var article in new[] { "The ", "A ", "An " })
                if (title.StartsWith (article, StringComparison.OrdinalIgnoreCase) && title.Length > article.Length)
                    return title[article.Length..];
            return title;
        }

        /// <summary>The first author's surname: the last word of their name.</summary>
        private static string SortableAuthor (LibraryBook book)
        {
            var first = book.Meta.Authors.FirstOrDefault () ?? string.Empty;
            var space = first.LastIndexOf (' ');
            return space < 0 ? first : first[(space + 1)..] + " " + first[..space];
        }
    }
}
