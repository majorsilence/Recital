using Xunit;

namespace Majorsilence.Recital.Epub.Tests
{
    public class CfiSyntaxTests
    {
        [Theory]
        // Examples from the EPUB CFI 1.1 specification.
        [InlineData ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/3:10)")]
        [InlineData ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/2/1:3)")]
        [InlineData ("epubcfi(/6/4[chap01ref]!/4[body01]/16[svgimg])")]
        [InlineData ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/1:0)")]
        [InlineData ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05],/2/1:1,/3:4)")]
        // Assertions on offsets: text location and side bias, kept verbatim.
        [InlineData ("epubcfi(/6/4!/4/2/1:5[;s=a])")]
        [InlineData ("epubcfi(/6/4!/4/2/1:5[yyy,zzz;s=b])")]
        // Escaped id assertion.
        [InlineData ("epubcfi(/6/4[a^[b^]^,c]!/4)")]
        // Range whose ends are in the same text chunk: locals are bare offsets.
        [InlineData ("epubcfi(/6/4!/4/10/3,:10,:20)")]
        // Range across documents: locals start with the indirection.
        [InlineData ("epubcfi(/6,/4!/4/2/1:0,/6!/4/2/1:3)")]
        public void RoundTripsExactly (string text)
        {
            var cfi = EpubCfi.Parse (text);

            Assert.Equal (text, cfi.ToString ());
            Assert.Equal (cfi, EpubCfi.Parse (cfi.ToString ()));
        }

        [Fact]
        public void ParsesStepsIdsAndOffset ()
        {
            var cfi = EpubCfi.Parse ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/3:10)");

            Assert.False (cfi.IsRange);
            Assert.Equal ([new CfiStep (6), new CfiStep (4, "chap01ref")], cfi.Start.PackageSteps);
            Assert.Equal ([new CfiStep (4, "body01"), new CfiStep (10, "para05"), new CfiStep (3)], cfi.Start.ContentSteps);
            Assert.Equal (10, cfi.Start.Offset!.Character);
        }

        [Fact]
        public void UnescapesIdAssertions ()
        {
            var cfi = EpubCfi.Parse ("epubcfi(/6/4[a^[b^]^,c]!/4)");

            Assert.Equal ("a[b],c", cfi.Start.PackageSteps.Last ().Id);
        }

        [Fact]
        public void ReadsSideBias ()
        {
            Assert.Equal ('a', EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:5[;s=a])").Start.Offset!.SideBias);
            Assert.Equal ('b', EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:5[yyy,zzz;s=b])").Start.Offset!.SideBias);
            Assert.Null (EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:5)").Start.Offset!.SideBias);
        }

        [Fact]
        public void RangeEndsAreFullPoints ()
        {
            var cfi = EpubCfi.Parse ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05],/2/1:1,/3:4)");

            Assert.True (cfi.IsRange);
            Assert.Equal ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/2/1:1)", cfi.Start.ToString ());
            Assert.Equal ("epubcfi(/6/4[chap01ref]!/4[body01]/10[para05]/3:4)", cfi.End!.ToString ());
        }

        [Fact]
        public void RangeIsNormalisedToDocumentOrder ()
        {
            var early = EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:0)").Start;
            var late = EpubCfi.Parse ("epubcfi(/6/4!/4/6/1:0)").Start;

            var range = EpubCfi.Range (late, early);

            Assert.Equal (early, range.Start);
            Assert.Equal (late, range.End);
        }

        [Fact]
        public void RangeWithIdenticalElementEndsStillFormatsValidly ()
        {
            var point = EpubCfi.Parse ("epubcfi(/6/4!/4/2)").Start;
            var range = EpubCfi.Range (point, point);

            Assert.Equal (range, EpubCfi.Parse (range.ToString ()));
        }

        [Theory]
        [InlineData ("")]
        [InlineData ("/6/4!/4")]
        [InlineData ("epubcfi()")]
        [InlineData ("epubcfi(6/4)")]
        [InlineData ("epubcfi(/6/04)")]
        [InlineData ("epubcfi(/6/4!)")]
        [InlineData ("epubcfi(/6/4[unclosed)")]
        [InlineData ("epubcfi(/6/4~3.5)")]
        [InlineData ("epubcfi(/6/4@50:50)")]
        [InlineData ("epubcfi(/6/4!/4/2:3,/1:0)")]
        [InlineData ("epubcfi(/6/4!/4,/2,)")]
        [InlineData ("epubcfi(/6/99999999999)")]
        public void RejectsInvalid (string text)
        {
            Assert.False (EpubCfi.TryParse (text, out _));
            Assert.Throws<FormatException> (() => EpubCfi.Parse (text));
        }

        [Fact]
        public void OrdersInDocumentOrder ()
        {
            string[] ordered = [
                "epubcfi(/6/2!/4/2/1:0)",
                "epubcfi(/6/4!/4/2)",          // an element comes before anything inside it
                "epubcfi(/6/4!/4/2/1:0)",
                "epubcfi(/6/4!/4/2/1:5)",
                "epubcfi(/6/4!/4/2/2)",
                "epubcfi(/6/4!/4/2/3:0)",
                "epubcfi(/6/4!/4/4)",
                "epubcfi(/6/4!/4/10/1:0)",     // numeric, not lexical: 10 after 4
                "epubcfi(/6/10!/4/2/1:0)",
            ];

            var shuffled = ordered.Reverse ().Select (EpubCfi.Parse).ToList ();
            shuffled.Sort ();

            Assert.Equal (ordered, shuffled.Select (c => c.ToString ()));
        }

        [Fact]
        public void EqualityIgnoresAssertions ()
        {
            Assert.Equal (EpubCfi.Parse ("epubcfi(/6/4[a]!/4[b]/2/1:3)"), EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:3)"));
            Assert.NotEqual (EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:3)"), EpubCfi.Parse ("epubcfi(/6/4!/4/2/1:4)"));
        }

        [Fact]
        public void EscapesSpecialCharacters ()
        {
            Assert.Equal ("a^[b^]^(c^)^,d^;e^=f^^", EpubCfi.Escape ("a[b](c),d;e=f^"));
        }
    }
}
