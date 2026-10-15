using Majorsilence.Recital.Core.Sync;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    public class HlcTests
    {
        private sealed class FixedTime (DateTimeOffset now) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = now;
            public override DateTimeOffset GetUtcNow () => Now;
        }

        [Fact]
        public void TimestampsAlwaysIncreaseEvenWhenTheWallClockStallsOrGoesBack ()
        {
            var time = new FixedTime (DateTimeOffset.FromUnixTimeMilliseconds (1_000));
            var clock = new HlcClock ("a", time);

            var first = clock.Now ();
            var second = clock.Now ();
            time.Now = DateTimeOffset.FromUnixTimeMilliseconds (500);
            var third = clock.Now ();

            Assert.True (first < second);
            Assert.True (second < third);
            Assert.Equal (1_000, third.WallMilliseconds);
        }

        [Fact]
        public void ObservingARemoteTimestampOrdersTheNextLocalOneAfterIt ()
        {
            var clock = new HlcClock ("a", new FixedTime (DateTimeOffset.FromUnixTimeMilliseconds (1_000)));
            var remote = new Hlc (5_000, 3, "b");

            clock.Observe (remote);

            Assert.True (clock.Now () > remote);
        }

        [Fact]
        public void TheDeviceBreaksTies ()
            => Assert.True (new Hlc (1, 1, "a") < new Hlc (1, 1, "b"));

        [Fact]
        public void RoundTripsAsSortableText ()
        {
            var hlc = new Hlc (1_760_000_000_000, 7, "abc123");
            Assert.Equal ("0001760000000000-0007-abc123", hlc.ToString ());
            Assert.Equal (hlc, Hlc.Parse (hlc.ToString ()));
            Assert.True (string.CompareOrdinal (new Hlc (9, 0, "z").ToString (), new Hlc (10, 0, "a").ToString ()) < 0);
        }
    }
}
