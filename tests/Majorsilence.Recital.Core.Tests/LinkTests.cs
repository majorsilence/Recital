using Majorsilence.Recital.Core.Reading;
using Majorsilence.Recital.Layout;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class LinkTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;
        private static LayoutSettings Desktop => new ReaderPreferences ().ToLayoutSettings (1280, 800);

        // Pages forward until the page shows a link, probing every line across its width; returns the point and link.
        private static async Task<(float X, float Y, BookLink Link)> FindLinkAsync (ReadingSession session, Func<BookLink, bool> wanted)
        {
            for (var turns = 0; turns < 400; turns++) {
                foreach (var line in session.Page.Lines)
                    for (var x = line.X; x < line.X + line.Width; x += 3) {
                        var y = line.Y + line.Height / 2;
                        if (session.LinkAt (x, y) is { } link && wanted (link))
                            return (x, y, link);
                    }
                Assert.True (await session.NextPageAsync (Ct), "ran out of pages looking for a link");
            }
            throw new InvalidOperationException ("no link found");
        }

        // The Gutenberg Pride and Prejudice lists its illustrations with page-number links into the chapters.
        [Fact]
        public async Task FollowingALinkGoesToItsTargetAndBackReturns ()
        {
            await using var session = await ReadingSession.OpenAsync (await Corpus.OpenBookAsync (Corpus.PgPride), Desktop, null, Ct);
            var (x, y, link) = await FindLinkAsync (session, l => l.SpineIndex is not null && l.Fragment is not null);
            var before = (session.SpineIndex, session.PageIndex);
            Assert.False (session.CanReturn);
            Assert.NotEmpty (link.Rects);

            Assert.True (await session.FollowAsync (link, Ct));

            Assert.Equal (link.SpineIndex, session.SpineIndex);
            var target = session.Chapter.Document.FindFragment (link.Fragment!);
            Assert.NotNull (target);
            Assert.Same (session.Page, session.Chapter.PageOf (new Epub.DomPosition (target, 0)));
            Assert.True (session.CanReturn);

            Assert.True (await session.ReturnAsync (Ct));
            Assert.Equal (before, (session.SpineIndex, session.PageIndex));
            Assert.False (session.CanReturn);
            Assert.Equal (link.Href, session.LinkAt (x, y)?.Href);
        }

        [Fact]
        public async Task PlainTextAndMarginsAreNotLinks ()
        {
            await using var session = await ReadingSession.OpenAsync (await Corpus.OpenBookAsync (Corpus.PgPride), Desktop, null, Ct);
            var (_, y, link) = await FindLinkAsync (session, l => l.SpineIndex is not null);
            var right = link.Rects.Max (r => r.X + r.Width);

            Assert.Null (session.LinkAt (right + 40, y));
            Assert.Null (session.LinkAt (-50, y));
            Assert.Null (session.LinkAt (link.Rects[0].X + 1, y - 400));
        }
    }
}
