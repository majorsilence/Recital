using Majorsilence.Recital.Core.Annotations;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Layout;
using Majorsilence.Recital.Storage;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class AnnotationTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly ReaderPreferences Preferences = new ();
        private static LayoutSettings Phone => Preferences.ToLayoutSettings (360, 640);
        private static LayoutSettings Desktop => Preferences.ToLayoutSettings (1280, 800);

        private static async Task<ReadingSession> OpenAsync (string file, LayoutSettings settings)
            => await ReadingSession.OpenAsync (await Corpus.OpenBookAsync (file), settings, null, Ct);

        /// <summary>From a word on one line of the page to a word on a later line, as a reader drags.</summary>
        private static SelectedPassage SelectAcrossLines (ReadingSession session, int fromLine, int toLine)
        {
            var lines = session.Page.Lines.Where (l => l.Text.Trim ().Length > 12).ToList ();
            Assert.True (lines.Count > toLine, "The page has enough lines of text.");
            var from = lines[fromLine];
            var to = lines[toLine];
            var start = session.Page.WordAt (from.X + from.Width * 0.3f, from.Y + from.Height / 2)!.Value.Start;
            var end = session.Page.WordAt (to.X + to.Width * 0.2f, to.Y + to.Height / 2)!.Value.End;
            return session.Select (start, end)!;
        }

        [Theory]
        [InlineData (Corpus.MobyDick, 0.1)]
        [InlineData (Corpus.SePride, 0.5)]
        [InlineData (Corpus.Sherlock, 0.9)]
        public async Task AHighlightCoversTheSameWordsAtAnotherPageSize (string book, double percent)
        {
            await using var session = await OpenAsync (book, Desktop);
            // Roughly percent through the book, on a page with plenty of text.
            while (session.Locator.Percent < percent || session.Page.Lines.Count (l => l.Text.Trim ().Length > 12) < 6)
                Assert.True (await session.NextPageAsync (Ct));

            var selection = SelectAcrossLines (session, 1, 4);
            var highlight = ReadingSession.NewHighlight (selection, "green");
            Assert.Contains (session.HighlightsOnPage ([highlight]), h => h.Annotation == highlight && h.Rects.Count >= 4);

            await session.ChangeSettingsAsync (Phone, Ct);
            Assert.True (await session.GoToAsync (highlight, Ct));
            var (start, end) = session.Locate (highlight)!.Value;
            Assert.Equal (selection.Text, session.Chapter.Document.GetText (start, end));

            // The highlight is drawn on the phone page where it starts, and its rectangles cover its words.
            var shown = Assert.Single (session.HighlightsOnPage ([highlight]));
            var drawnText = string.Join (" ", session.Page.Lines
                .Where (l => shown.Rects.Any (r => Math.Abs (r.Y - l.Y) < 0.5f))
                .Select (l => l.Text.Trim ()));
            var words = selection.Text.Split (' ');
            Assert.Contains (words[0] + " " + words[1], drawnText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AWordUnderThePointerIsTheWholeWord ()
        {
            await using var session = await OpenAsync (Corpus.Alice, Desktop);
            while (session.Page.Lines.Count () < 10)
                Assert.True (await session.NextPageAsync (Ct));
            var line = session.Page.Lines.First (l => l.Text.Trim ().Split (' ').Length > 6);
            var word = session.Page.WordAt (line.X + line.Width / 2, line.Y + 1)!.Value;
            var text = session.Chapter.Document.GetText (word.Start, word.End);

            Assert.Matches (@"^[\p{L}\p{N}'’-]+$", text);
            Assert.Contains (text, line.Text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ABookmarkStaysOnThePageItMarksAfterAResize ()
        {
            await using var session = await OpenAsync (Corpus.Sherlock, Desktop);
            for (var i = 0; i < 8; i++)
                await session.NextPageAsync (Ct);
            var bookmark = session.NewBookmark ();
            Assert.Same (bookmark, session.BookmarkOnPage ([bookmark]));

            await session.ChangeSettingsAsync (Phone, Ct);
            Assert.Same (bookmark, session.BookmarkOnPage ([bookmark]));
            await session.NextPageAsync (Ct);
            await session.NextPageAsync (Ct);
            Assert.Null (session.BookmarkOnPage ([bookmark]));
        }

        [Fact]
        public async Task AnnotationsSyncAndADeletionIsFinal ()
        {
            var home = new InMemoryStorageProvider ();
            var now = new DateTimeOffset (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            var laptop = new LibraryService (home, new DeviceIdentity ("aaaa", "Laptop", "linux"), new FixedClock (() => now));
            var phone = new LibraryService (home, new DeviceIdentity ("bbbb", "Phone", "linux"), new FixedClock (() => now - TimeSpan.FromMinutes (10)));
            await using var file = Corpus.Open (Corpus.Alice);
            var id = (await laptop.ImportAsync (file, "alice.epub", Ct)).Book.Id;

            var highlight = new Annotation { Id = Guid.NewGuid (), Cfi = "epubcfi(/6/8!/4/2,/1:0,/1:10)", SpineIndex = 3, Text = "Alice was", Color = "yellow", Note = "Opening" };
            var saved = await laptop.SaveAnnotationAsync (id, highlight, Ct);
            Assert.NotNull (saved);

            // The phone (clock ten minutes slow) changes the colour; its version wins because it saw the laptop's.
            now += TimeSpan.FromMinutes (1);
            var onPhone = Assert.Single (await phone.GetAnnotationsAsync (id, Ct));
            await phone.SaveAnnotationAsync (id, onPhone with { Color = "blue" }, Ct);
            Assert.Equal ("blue", Assert.Single (await laptop.GetAnnotationsAsync (id, Ct)).Color);

            // The laptop deletes it. The phone, which still has its blue version, cannot bring it back.
            await laptop.DeleteAnnotationAsync (id, highlight.Id, Ct);
            Assert.Empty (await phone.GetAnnotationsAsync (id, Ct));
            Assert.Null (await phone.SaveAnnotationAsync (id, onPhone with { Color = "pink" }, Ct));
            Assert.Empty (await laptop.GetAnnotationsAsync (id, Ct));

            // The tombstone keeps no text; reading progress in the same file is untouched.
            var progress = await laptop.SaveProgressAsync (id, new ReadingLocator (null, 3, 0, 0.1), Ct);
            Assert.Equal (progress.Locator, (await phone.GetProgressAsync (id, Ct))?.Locator);
            await using var stream = await home.OpenReadAsync (SyncPaths.State (id, "aaaa"), Ct);
            var json = await new StreamReader (stream).ReadToEndAsync (Ct);
            Assert.DoesNotContain ("Opening", json, StringComparison.Ordinal);
            Assert.Contains ("\"deleted\": true", json, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDeletionWinsWhateverTheClocks ()
        {
            var id = Guid.NewGuid ();
            var late = new Annotation { Id = id, Color = "pink", Hlc = new Hlc (9_000, 0, "b") };
            var tombstone = new Annotation { Id = id, Deleted = true, Hlc = new Hlc (1_000, 0, "a") };
            var merged = AnnotationMerge.Merge ([late, tombstone]);
            Assert.True (merged[id].Deleted);
            Assert.True (AnnotationMerge.Merge ([tombstone, late])[id].Deleted);
            Assert.Empty (AnnotationMerge.Live (merged.Values));
        }

        [Fact]
        public void ExportsMarkdownGroupedByChapter ()
        {
            var at = new DateTimeOffset (2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
            Annotation[] annotations = [
                new () { Id = Guid.NewGuid (), SpineIndex = 2, CharOffset = 40, Percent = 0.05, Text = "Call me Ishmael.", Color = "yellow", CreatedAt = at },
                new () { Id = Guid.NewGuid (), SpineIndex = 2, CharOffset = 900, Percent = 0.06, Text = "a damp, drizzly November in my soul", Color = "blue", Note = "Mood *exactly*.\nSecond line.", CreatedAt = at },
                new () { Id = Guid.NewGuid (), Kind = AnnotationKind.Bookmark, SpineIndex = 3, Percent = 0.1, Text = "CHAPTER 2. The Carpet-Bag.", CreatedAt = at },
                new () { Id = Guid.NewGuid (), SpineIndex = 3, Text = "gone", Deleted = true, CreatedAt = at },
            ];

            var markdown = AnnotationExport.ToMarkdown ("Moby Dick; Or, The Whale", "Herman Melville", annotations,
                a => a.SpineIndex == 2 ? "CHAPTER 1. Loomings." : "CHAPTER 2. The Carpet-Bag.");

            Assert.Equal ("""
                # Moby Dick; Or, The Whale

                *Herman Melville*

                ## CHAPTER 1. Loomings.

                > Call me Ishmael.

                *Yellow, 5%*

                > a damp, drizzly November in my soul

                *Blue, 6%*

                Mood *exactly*.
                Second line.

                ## CHAPTER 2. The Carpet-Bag.

                - Bookmark (10%): CHAPTER 2. The Carpet-Bag.

                """.ReplaceLineEndings (), markdown.ReplaceLineEndings ());
        }

        private sealed class FixedClock (Func<DateTimeOffset> now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow () => now ();
        }
    }
}
