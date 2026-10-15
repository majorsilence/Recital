using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Epub;
using Majorsilence.Recital.Epub.Tests;
using Majorsilence.Recital.Layout;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class ReadingSessionTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        private static readonly ReaderPreferences Preferences = new ();
        private static LayoutSettings Phone => Preferences.ToLayoutSettings (360, 640);
        private static LayoutSettings Desktop => Preferences.ToLayoutSettings (1280, 800);

        private static async Task<ReadingSession> OpenAsync (string file, LayoutSettings settings, ReadingLocator? start = null)
            => await ReadingSession.OpenAsync (await Corpus.OpenBookAsync (file), settings, start, Ct);

        /// <summary>The anchor is on the page showing: at or after its start, and before the next page's.</summary>
        private static void AssertAnchorOnCurrentPage (ReadingSession session)
        {
            var position = session.Chapter.Document.Resolve (EpubCfi.Parse (session.Locator.Cfi!));
            Assert.NotNull (position);
            Assert.Same (session.Page, session.Chapter.PageOf (position.Value));
        }

        [Fact]
        public async Task OpensAtTheFirstReadablePage ()
        {
            await using var session = await OpenAsync (Corpus.Alice, Phone);

            Assert.Equal (0, session.PageIndex);
            Assert.False (session.CanGoBack);
            Assert.True (session.CanGoForward);
            Assert.NotNull (session.Locator.Cfi);
            Assert.InRange (session.Locator.Percent, 0, 0.01);
        }

        [Fact]
        public async Task PagesForwardAcrossChaptersAndBackAgain ()
        {
            await using var session = await OpenAsync (Corpus.SePride, Phone);
            var visited = new List<(int Spine, int Page, string? Cfi)> ();

            for (var i = 0; i < 40; i++) {
                visited.Add ((session.SpineIndex, session.PageIndex, session.Locator.Cfi));
                Assert.True (await session.NextPageAsync (Ct));
            }

            Assert.True (visited.Select (v => v.Spine).Distinct ().Count () > 2, "Forty phone pages cross several chapters.");

            for (var i = visited.Count - 1; i >= 0; i--) {
                Assert.True (await session.PreviousPageAsync (Ct));
                Assert.Equal (visited[i], (session.SpineIndex, session.PageIndex, session.Locator.Cfi));
            }
        }

        [Fact]
        public async Task PercentGrowsAsYouRead ()
        {
            await using var session = await OpenAsync (Corpus.Alice, Phone);
            var last = -1.0;
            for (var i = 0; i < 30; i++) {
                Assert.True (session.Locator.Percent >= last);
                last = session.Locator.Percent;
                await session.NextPageAsync (Ct);
            }
            Assert.True (last > 0);
        }

        [Fact]
        public async Task TheLastPageCannotGoForward ()
        {
            await using var session = await OpenAsync (Corpus.Alice, Phone);
            var last = session.Book.Spine.Count - 1;
            await session.GoToAsync (new ReadingLocator (null, last, int.MaxValue, 1), Ct);
            await session.ChapterEdgeAsync (end: true, Ct);

            Assert.False (session.CanGoForward);
            Assert.False (await session.NextPageAsync (Ct));
        }

        [Fact]
        public async Task ResizingKeepsThePositionAndNeverDrifts ()
        {
            await using var session = await OpenAsync (Corpus.SePride, Phone);
            for (var i = 0; i < 25; i++)
                await session.NextPageAsync (Ct);

            var locator = session.Locator;
            var phonePage = (session.SpineIndex, session.PageIndex);

            await session.ChangeSettingsAsync (Desktop, Ct);
            Assert.Equal (locator, session.Locator);
            AssertAnchorOnCurrentPage (session);

            await session.ChangeSettingsAsync (Preferences.ToLayoutSettings (768, 1024), Ct);
            await session.ChangeSettingsAsync (Phone, Ct);
            Assert.Equal (locator, session.Locator);
            Assert.Equal (phonePage, (session.SpineIndex, session.PageIndex));
        }

        [Fact]
        public async Task TypographyChangesKeepThePosition ()
        {
            await using var session = await OpenAsync (Corpus.Frankenstein, Desktop);
            for (var i = 0; i < 12; i++)
                await session.NextPageAsync (Ct);
            var locator = session.Locator;

            await session.ChangeSettingsAsync ((Preferences with { FontSize = 28, LineHeight = 1.8f }).ToLayoutSettings (1280, 800), Ct);

            Assert.Equal (locator, session.Locator);
            AssertAnchorOnCurrentPage (session);
        }

        [Theory]
        [InlineData (Corpus.Alice)]
        [InlineData (Corpus.SePride)]
        [InlineData (Corpus.PgPride)]
        [InlineData (Corpus.Frankenstein)]
        public async Task ASavedLocatorReopensAtTheSamePlaceOnAnotherDevice (string file)
        {
            ReadingLocator saved;
            await using (var phone = await OpenAsync (file, Phone)) {
                for (var i = 0; i < 15; i++)
                    await phone.NextPageAsync (Ct);
                saved = phone.Locator;
            }

            await using var desktop = await OpenAsync (file, Desktop, saved);
            Assert.Equal (saved.SpineIndex, desktop.SpineIndex);
            Assert.Equal (saved, desktop.Locator);
            AssertAnchorOnCurrentPage (desktop);
        }

        [Fact]
        public async Task AnUnresolvableCfiFallsBackToTheCharacterOffset ()
        {
            ReadingLocator saved;
            await using (var first = await OpenAsync (Corpus.Alice, Phone)) {
                for (var i = 0; i < 8; i++)
                    await first.NextPageAsync (Ct);
                saved = first.Locator;
            }

            var broken = saved with { Cfi = "epubcfi(/6/2!/4/999/1:0)" };
            await using var session = await OpenAsync (Corpus.Alice, Phone, broken);

            Assert.Equal (saved.SpineIndex, session.SpineIndex);
            var position = session.Chapter.Document.PositionAtCharOffset (saved.CharOffset);
            Assert.Same (session.Page, session.Chapter.PageOf (position!.Value));
        }

        [Fact]
        public async Task TableOfContentsEntriesGoToTheirFragment ()
        {
            await using var session = await OpenAsync (Corpus.SePride, Phone);
            var chapter = ReadingSession.Flatten (session.Book.TableOfContents).First (e => e.Title.Contains ("XX", StringComparison.Ordinal) || e.Title.Contains ("20", StringComparison.Ordinal));

            Assert.True (await session.GoToAsync (chapter, Ct));

            Assert.Equal (chapter.SpineIndex, session.SpineIndex);
            Assert.Same (chapter, session.CurrentTocEntry ());
        }

        [Fact]
        public async Task FragmentEntriesWithinAChapterAreFound ()
        {
            var epub = TestEpub.Epub3 ().With ("OEBPS/text/ch1.xhtml", TestEpub.Xhtml (
                "<h1>One</h1>" + string.Concat (Enumerable.Range (1, 60).Select (i => $"<p>Paragraph {i} with enough words to fill a line or two.</p>"))
                + "<p id=\"p2\">Part two starts here.</p>" + string.Concat (Enumerable.Range (1, 10).Select (i => $"<p>More {i}.</p>"))));
            await using var session = await ReadingSession.OpenAsync (await epub.OpenAsync (), Phone, null, Ct);

            var partTwo = ReadingSession.Flatten (session.Book.TableOfContents).Single (e => e.Fragment == "p2");
            Assert.Equal ("One", session.CurrentTocEntry ()?.Title);

            await session.GoToAsync (partTwo, Ct);

            Assert.True (session.PageIndex > 0);
            Assert.Contains (session.Page.Lines, l => l.Text.Contains ("Part two", StringComparison.Ordinal));
            Assert.Equal ("One, part two", session.CurrentTocEntry ()?.Title);
        }

        [Fact]
        public async Task ChaptersWithNothingToShowAreSkipped ()
        {
            var epub = TestEpub.Epub3 (
                manifest: """
                    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
                    <item id="c1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
                    <item id="empty" href="text/empty.xhtml" media-type="application/xhtml+xml"/>
                    <item id="c2" href="text/ch2.xhtml" media-type="application/xhtml+xml"/>
                    """,
                spine: """<itemref idref="c1"/><itemref idref="empty"/><itemref idref="c2"/>""")
                .With ("OEBPS/text/empty.xhtml", TestEpub.Xhtml (""));
            await using var session = await ReadingSession.OpenAsync (await epub.OpenAsync (), Phone, null, Ct);

            Assert.True (await session.NextPageAsync (Ct));
            Assert.Contains (session.Page.Lines, l => l.Text == "Two");
            Assert.True (await session.PreviousPageAsync (Ct));
            Assert.Equal (0, session.SpineIndex);
        }
    }
}
