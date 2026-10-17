using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Majorsilence.Recital.Sync.Tests
{
    /// <summary>
    /// The Phase 5 acceptance test: seeded random runs of <see cref="SyncSimulation"/>, every one of which
    /// must converge. RECITAL_SYNC_RUNS sets the number of runs (default 1000) and RECITAL_SYNC_SEED the
    /// first seed, so a failure reported as "Seed N" replays with RECITAL_SYNC_SEED=N RECITAL_SYNC_RUNS=1.
    /// </summary>
    public class SyncSimulationTests (ITestOutputHelper output)
    {
        private const int Steps = 80;

        [Fact (Timeout = 900_000)]
        public async Task EveryRunConverges ()
        {
            var runs = EnvironmentNumber ("RECITAL_SYNC_RUNS", 1000);
            var first = EnvironmentNumber ("RECITAL_SYNC_SEED", 1);
            var watch = Stopwatch.StartNew ();

            var failures = new List<string> ();
            // Runs are independent (each has its own folder), so they run a few at a time.
            await Parallel.ForEachAsync (Enumerable.Range (first, runs), new ParallelOptions {
                MaxDegreeOfParallelism = Math.Max (2, Environment.ProcessorCount / 2),
                CancellationToken = TestContext.Current.CancellationToken,
            }, async (seed, ct) => {
                using var simulation = new SyncSimulation (seed);
                string? failure;
                try {
                    failure = await simulation.RunAsync (Steps, ct);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    failure = $"Seed {seed}: threw {ex}";
                }
                if (failure is not null)
                    lock (failures)
                        failures.Add (failure);
            });

            output.WriteLine ($"{runs} runs of {Steps} steps in {watch.Elapsed.TotalSeconds:F1} s.");
            if (failures.Count > 0)
                Assert.Fail ($"{failures.Count} of {runs} runs did not converge. First:\n{failures.OrderBy (f => f, StringComparer.Ordinal).First ()}");
        }

        private static int EnvironmentNumber (string name, int fallback)
            => int.TryParse (Environment.GetEnvironmentVariable (name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;
    }
}
