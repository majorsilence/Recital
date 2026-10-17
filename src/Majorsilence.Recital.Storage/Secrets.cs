namespace Majorsilence.Recital.Storage
{
    /// <summary>
    /// Where secrets live: OAuth refresh tokens and WebDAV passwords (plan 0.3). Implementations use the
    /// platform's secure store (Windows Credential Manager, macOS Keychain, libsecret on Linux), or memory
    /// where there is none. Nothing here ever reaches the sync folder or a log.
    /// </summary>
    public interface ISecretStore
    {
        /// <summary>A short name for settings screens ("Windows Credential Manager", "memory only").</summary>
        string Name { get; }

        /// <summary>False when secrets last only until the app closes.</summary>
        bool IsPersistent { get; }

        Task<string?> GetAsync (string key, CancellationToken ct = default);

        /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>; null removes it.</summary>
        Task SetAsync (string key, string? value, CancellationToken ct = default);
    }

    /// <summary>Secrets kept in memory only: the browser (plan 0.3) and tests.</summary>
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> values = new (StringComparer.Ordinal);

        public string Name => "memory only";
        public bool IsPersistent => false;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
        {
            lock (values)
                return Task.FromResult (values.TryGetValue (key, out var value) ? value : null);
        }

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
        {
            lock (values) {
                if (value is null)
                    values.Remove (key);
                else
                    values[key] = value;
            }
            return Task.CompletedTask;
        }
    }
}
