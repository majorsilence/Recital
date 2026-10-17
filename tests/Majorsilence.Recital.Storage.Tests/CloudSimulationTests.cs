using System.Globalization;
using Majorsilence.Recital.Sync.Tests;
using Xunit;

namespace Majorsilence.Recital.Storage.Tests
{
    /// <summary>
    /// The three-device sync simulation (Phase 5) over each provider's fake API instead of a local folder:
    /// the engine must converge with each API's own paging, change feed, ETags and quirks (Drive's same-named
    /// folders, Graph's parent ids). RECITAL_CLOUD_SIM_RUNS sets the runs per provider (default 100).
    /// </summary>
    public class CloudSimulationTests
    {
        public static TheoryData<string> Providers => ["webdav", "dropbox", "onedrive", "gdrive"];

        [Theory (Timeout = 600_000)]
        [MemberData (nameof (Providers))]
        public async Task EveryRunConverges (string provider)
        {
            var runs = int.TryParse (Environment.GetEnvironmentVariable ("RECITAL_CLOUD_SIM_RUNS"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 100;
            var failures = new List<string> ();
            await Parallel.ForEachAsync (Enumerable.Range (1, runs), new ParallelOptions {
                MaxDegreeOfParallelism = Math.Max (2, Environment.ProcessorCount / 2),
                CancellationToken = TestContext.Current.CancellationToken,
            }, async (seed, ct) => {
                var (factory, dispose) = Backend (provider);
                try {
                    using var simulation = new SyncSimulation (seed, factory);
                    var failure = await simulation.RunAsync (60, ct);
                    if (failure is not null)
                        lock (failures)
                            failures.Add (failure);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    lock (failures)
                        failures.Add ($"Seed {seed}: threw {ex}");
                } finally {
                    dispose ();
                }
            });
            if (failures.Count > 0)
                Assert.Fail ($"{failures.Count} of {runs} {provider} runs did not converge. First:\n{failures.OrderBy (f => f, StringComparer.Ordinal).First ()}");
        }

        /// <summary>One fake service, and a factory of clients of it (each device has its own).</summary>
        private static (Func<IStorageProvider> Factory, Action Dispose) Backend (string provider)
        {
            switch (provider) {
            case "webdav": {
                var server = new FakeWebDavServer ();
                var http = WebDav.WebDavStorageProvider.CreateClient ("u", "p", server);
                return (() => new WebDav.WebDavStorageProvider (server.BaseUri, http), () => { http.Dispose (); server.Dispose (); });
            }
            case "dropbox": {
                var server = new FakeDropboxServer ();
                var http = new HttpClient (server);
                return (() => new Dropbox.DropboxStorageProvider (http, feedFolder: "Recital"), () => { http.Dispose (); server.Dispose (); });
            }
            case "onedrive": {
                var server = new FakeGraphServer ();
                var http = new HttpClient (server, disposeHandler: false);
                return (() => new OneDrive.OneDriveStorageProvider (http, http, feedFolder: "Recital"), () => { http.Dispose (); server.Dispose (); });
            }
            default: {
                var server = new FakeDriveServer ();
                var http = new HttpClient (server);
                return (() => new GoogleDrive.GoogleDriveStorageProvider (http, feedFolder: "Recital"), () => { http.Dispose (); server.Dispose (); });
            }
            }
        }
    }
}
