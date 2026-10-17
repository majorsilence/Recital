using Majorsilence.Recital.Core.Sync;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Sync.Tests
{
    /// <summary>A clock that runs with the simulation's world time, offset by a fixed skew.</summary>
    internal sealed class SimClock (Func<DateTimeOffset> world, TimeSpan skew) : TimeProvider
    {
        public TimeSpan Skew { get; } = skew;

        public override DateTimeOffset GetUtcNow () => world () + Skew;
    }

    /// <summary>
    /// One device's view of the shared sync home: it can be offline (every call fails), flaky (calls fail at
    /// random, including writes that land but report failure), and it checks that the device writes only
    /// what plan 2.3 allows it to. <paramref name="written"/> sees each write's path after it lands.
    /// </summary>
    internal sealed class DeviceStorage (IStorageProvider shared, string deviceId, Random random, SimHistory history, Action<string> written) : IStorageProvider
    {
        public bool Offline { get; set; }
        public double FailureRate { get; set; }
        public List<string> Violations { get; } = [];

        public string Id => shared.Id;
        public StorageCapabilities Capabilities => shared.Capabilities;

        public async Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default)
        {
            Fail ();
            return await shared.ListAsync (folderPath, ct);
        }

        public async Task<Stream> OpenReadAsync (string path, CancellationToken ct = default)
        {
            Fail ();
            return await shared.OpenReadAsync (path, ct);
        }

        public async Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default)
        {
            Fail ();
            return await shared.StatAsync (path, ct);
        }

        public async Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default)
        {
            Fail ();
            return await shared.GetChangesAsync (cursor, ct);
        }

        public async Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default)
        {
            Fail ();
            var buffer = new MemoryStream ();
            await content.CopyToAsync (buffer, ct);
            var bytes = buffer.ToArray ();

            switch (SyncPaths.Classify (path, deviceId)) {
            case SyncFileKind.Own:
                history.Record (path, bytes);
                break;
            case SyncFileKind.WriteOnce:
                if (await shared.StatAsync (path, ct) is not null)
                    Violations.Add ($"{deviceId} overwrote write-once {path}");
                break;
            default:
                Violations.Add ($"{deviceId} wrote {path}, which is not its own");
                break;
            }

            var entry = await shared.WriteAsync (path, new MemoryStream (bytes), ifMatchEtag, ct);
            written (path);
            // The write landed, but the reply was lost.
            Fail ();
            return entry;
        }

        private void Fail ()
        {
            if (Offline)
                throw new IOException ("Simulated: offline.");
            if (FailureRate > 0 && random.NextDouble () < FailureRate)
                throw new IOException ("Simulated: the request failed.");
        }
    }

    /// <summary>Every version of every device file written to the sync home, for the sync client's stale copies.</summary>
    internal sealed class SimHistory
    {
        private readonly Dictionary<string, List<byte[]>> versions = new (StringComparer.Ordinal);

        public void Record (string path, byte[] bytes)
        {
            if (!versions.TryGetValue (path, out var list))
                versions[path] = list = [];
            list.Add (bytes);
        }

        public IReadOnlyList<byte[]> Versions (string path) => versions.TryGetValue (path, out var list) ? list : [];
    }
}
