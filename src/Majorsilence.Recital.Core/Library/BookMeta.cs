using Majorsilence.Recital.Core.Sync;

namespace Majorsilence.Recital.Core.Library
{
    /// <summary><c>meta/&lt;bookId&gt;.json</c>: what the library shows without opening the book. Write-once.</summary>
    public sealed record BookMeta
    {
        public int FormatVersion { get; init; } = SyncPaths.FormatVersion;

        /// <summary>Lowercase hex SHA-256 of the EPUB file.</summary>
        public required string BookId { get; init; }

        public required string Title { get; init; }
        public IReadOnlyList<string> Authors { get; init; } = [];
        public string? Language { get; init; }

        /// <summary>The package's unique <c>dc:identifier</c>, for spotting another copy of the same book.</summary>
        public string? Identifier { get; init; }

        public long FileSize { get; init; }
        public string? FileName { get; init; }
        public bool HasCover { get; init; }
        public DateTimeOffset AddedAt { get; init; }
        public string? AddedByDevice { get; init; }
    }

    /// <summary>A book in the library with the newest progress any device has saved for it.</summary>
    public sealed record LibraryBook (BookMeta Meta, ReadingProgress? Progress)
    {
        public string Id => Meta.BookId;
        public string Title => Meta.Title;
        public string Authors => string.Join (", ", Meta.Authors);
        public double Percent => Progress?.Locator.Percent ?? 0;

        /// <summary>When it was last read on any device, else when it was added.</summary>
        public DateTimeOffset LastActivity => Progress?.UpdatedAt ?? Meta.AddedAt;
    }

    public sealed record ImportResult (LibraryBook Book, bool AlreadyInLibrary);

    /// <summary>A file that cannot be added: not an EPUB, or locked with DRM.</summary>
    public sealed class BookImportException : Exception
    {
        public BookImportException (string message, Exception? inner = null) : base (message, inner)
        {
        }
    }
}
