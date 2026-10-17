using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Platform
{
    /// <summary>Generic passwords in the user's login keychain (service "Recital", account = key).</summary>
    /// <remarks>
    /// Uses the SecKeychain generic-password calls: deprecated in favour of SecItem, but still in
    /// Security.framework, and they take plain buffers rather than CoreFoundation dictionaries. The calls are
    /// quick local IPC and run on the thread pool.
    /// </remarks>
    [SupportedOSPlatform ("macos")]
    public sealed partial class MacKeychainStore : ISecretStore
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const int ErrSecItemNotFound = -25300;

        public string Name => "macOS Keychain";
        public bool IsPersistent => true;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
            => Task.Run (() => {
                var service = Encoding.UTF8.GetBytes (SecretStores.Service);
                var account = Encoding.UTF8.GetBytes (key);
                var status = SecKeychainFindGenericPassword (IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, out var item);
                if (status == ErrSecItemNotFound)
                    return null;
                Check (status);
                try {
                    var bytes = new byte[length];
                    Marshal.Copy (data, bytes, 0, (int)length);
                    return Encoding.UTF8.GetString (bytes);
                } finally {
                    _ = SecKeychainItemFreeContent (IntPtr.Zero, data);
                    if (item != IntPtr.Zero)
                        CFRelease (item);
                }
            }, ct);

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
            => Task.Run (() => {
                var service = Encoding.UTF8.GetBytes (SecretStores.Service);
                var account = Encoding.UTF8.GetBytes (key);
                var status = SecKeychainFindGenericPassword (IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, out _, out var data, out var item);
                if (status != ErrSecItemNotFound)
                    Check (status);
                if (data != IntPtr.Zero)
                    _ = SecKeychainItemFreeContent (IntPtr.Zero, data);
                try {
                    if (value is null) {
                        if (item != IntPtr.Zero)
                            Check (SecKeychainItemDelete (item));
                        return;
                    }
                    var password = Encoding.UTF8.GetBytes (value);
                    try {
                        Check (item != IntPtr.Zero
                            ? SecKeychainItemModifyAttributesAndData (item, IntPtr.Zero, (uint)password.Length, password)
                            : SecKeychainAddGenericPassword (IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account, (uint)password.Length, password, IntPtr.Zero));
                    } finally {
                        Array.Clear (password);
                    }
                } finally {
                    if (item != IntPtr.Zero)
                        CFRelease (item);
                }
            }, ct);

        private static void Check (int status)
        {
            if (status != 0)
                throw new IOException ($"The keychain refused (OSStatus {status}).");
        }

        [LibraryImport (Security)]
        private static partial int SecKeychainFindGenericPassword (IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

        [LibraryImport (Security)]
        private static partial int SecKeychainAddGenericPassword (IntPtr keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);

        [LibraryImport (Security)]
        private static partial int SecKeychainItemModifyAttributesAndData (IntPtr item, IntPtr attributes, uint length, byte[] data);

        [LibraryImport (Security)]
        private static partial int SecKeychainItemDelete (IntPtr item);

        [LibraryImport (Security)]
        private static partial int SecKeychainItemFreeContent (IntPtr attributes, IntPtr data);

        [LibraryImport (CoreFoundation)]
        private static partial void CFRelease (IntPtr item);
    }
}
