using Majorsilence.Recital.Platform;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    public class SecretStoreTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task MemoryStoresAndForgets ()
        {
            var store = new InMemorySecretStore ();
            await store.SetAsync ("k", "v", Ct);
            Assert.Equal ("v", await store.GetAsync ("k", Ct));
            await store.SetAsync ("k", null, Ct);
            Assert.Null (await store.GetAsync ("k", Ct));
            Assert.False (store.IsPersistent);
        }

        /// <summary>
        /// The OS store on this machine. Skipped where there is none to talk to (a CI runner with no keyring
        /// daemon): the app falls back to memory there too.
        /// </summary>
        [Fact]
        public async Task ThePlatformStoreRoundTripsAndDeletes ()
        {
            var store = SecretStores.ForCurrentPlatform ();
            Assert.SkipWhen (!store.IsPersistent, $"No secure store on this machine ({store.Name}).");
            var key = "test:" + Guid.NewGuid ().ToString ("N");
            var secret = "refresh-token ✓ " + Guid.NewGuid ();
            try {
                await store.SetAsync (key, secret, Ct);
            } catch (IOException ex) {
                Assert.Skip ($"{store.Name} is not usable here: {ex.Message}");
            }
            try {
                Assert.Equal (secret, await store.GetAsync (key, Ct));
                await store.SetAsync (key, secret + "2", Ct);
                Assert.Equal (secret + "2", await store.GetAsync (key, Ct));
            } finally {
                await store.SetAsync (key, null, Ct);
            }
            Assert.Null (await store.GetAsync (key, Ct));
            Assert.Null (await store.GetAsync ("test:never-set-" + Guid.NewGuid ().ToString ("N"), Ct));
        }
    }
}
