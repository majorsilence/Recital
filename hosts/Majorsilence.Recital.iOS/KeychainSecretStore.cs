using Foundation;
using Majorsilence.Recital.Storage;
using Security;

namespace Majorsilence.Recital.iOS
{
    /// <summary>
    /// Secrets (sign-in tokens, voice keys) as generic passwords in the iOS Keychain, service "Recital", one account
    /// per key. Readable after the first unlock (so a sync can run in the background), kept to this device and out of
    /// iCloud Keychain.
    /// </summary>
    public sealed class KeychainSecretStore : ISecretStore
    {
        private const string Service = "Recital";

        public string Name => "iOS Keychain";
        public bool IsPersistent => true;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
            => Task.Run (() => {
                var data = SecKeyChain.QueryAsData (Query (key), false, out var status);
                if (status == SecStatusCode.ItemNotFound)
                    return null;
                Check (status, "read");
                return data?.ToString (NSStringEncoding.UTF8)?.ToString ();
            }, ct);

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
            => Task.Run (() => {
                var removed = SecKeyChain.Remove (Query (key));
                if (removed != SecStatusCode.ItemNotFound)
                    Check (removed, "remove");
                if (value is null)
                    return;
                var record = Query (key);
                record.ValueData = NSData.FromString (value, NSStringEncoding.UTF8);
                record.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
                Check (SecKeyChain.Add (record), "store");
            }, ct);

        private static SecRecord Query (string key) => new (SecKind.GenericPassword) { Service = Service, Account = key, Synchronizable = false };

        private static void Check (SecStatusCode status, string action)
        {
            if (status != SecStatusCode.Success)
                throw new IOException ($"The Keychain could not {action} a secret ({status}).");
        }
    }
}
