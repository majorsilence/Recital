using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class LibraryQueryTests
    {
        private static LibraryBook Book (string id, string title, string author, double percent = 0, int addedDay = 1)
            => new (new BookMeta { BookId = id, Title = title, Authors = [author], AddedAt = new DateTimeOffset (2026, 1, addedDay, 0, 0, 0, TimeSpan.Zero) },
                percent == 0 ? null : new ReadingProgress (new ReadingLocator (null, 0, 0, percent), Hlc.Zero, new DateTimeOffset (2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));

        private static readonly LibraryBook[] Books = [
            Book ("a1", "The Time Machine", "H. G. Wells", addedDay: 3),
            Book ("b2", "Pride and Prejudice", "Jane Austen", 0.4),
            Book ("c3", "Frankenstein", "Mary Shelley", addedDay: 5),
            Book ("d4", "Emma", "Jane Austen", 0.9),
        ];

        private static string[] Titles (IEnumerable<LibraryBook> books) => books.Select (b => b.Title).ToArray ();

        [Fact]
        public void SortsByTitleIgnoringArticles ()
            => Assert.Equal (["Emma", "Frankenstein", "Pride and Prejudice", "The Time Machine"], Titles (LibraryQuery.Apply (Books, null, LibrarySort.Title)));

        [Fact]
        public void SortsByAuthorSurname ()
            => Assert.Equal (["Emma", "Pride and Prejudice", "Frankenstein", "The Time Machine"], Titles (LibraryQuery.Apply (Books, null, LibrarySort.Author)));

        [Fact]
        public void SortsByRecentActivityThenProgress ()
        {
            Assert.Equal (["Emma", "Pride and Prejudice", "Frankenstein", "The Time Machine"], Titles (LibraryQuery.Apply (Books, "", LibrarySort.Recent)));
            Assert.Equal (["Emma", "Pride and Prejudice", "Frankenstein", "The Time Machine"], Titles (LibraryQuery.Apply (Books, "", LibrarySort.Progress)));
        }

        [Fact]
        public void SearchMatchesEveryWordInTitleOrAuthor ()
        {
            Assert.Equal (["Emma", "Pride and Prejudice"], Titles (LibraryQuery.Apply (Books, "austen", LibrarySort.Title)));
            Assert.Equal (["Pride and Prejudice"], Titles (LibraryQuery.Apply (Books, "JANE pride", LibrarySort.Title)));
            Assert.Empty (LibraryQuery.Apply (Books, "dickens", LibrarySort.Title));
        }
    }
}
