using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Platform
{
    /// <summary>Generic credentials in the Windows Credential Manager (encrypted with the user's logon, per user).</summary>
    [SupportedOSPlatform ("windows")]
    public sealed partial class WindowsCredentialStore : ISecretStore
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        public string Name => "Windows Credential Manager";
        public bool IsPersistent => true;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
        {
            if (!CredRead (Target (key), CredTypeGeneric, 0, out var handle)) {
                var error = Marshal.GetLastPInvokeError ();
                return error == ErrorNotFound ? Task.FromResult<string?> (null) : throw new Win32Exception (error);
            }
            try {
                var credential = Marshal.PtrToStructure<Credential> (handle);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                    return Task.FromResult<string?> ("");
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy (credential.CredentialBlob, bytes, 0, bytes.Length);
                return Task.FromResult<string?> (Encoding.UTF8.GetString (bytes));
            } finally {
                CredFree (handle);
            }
        }

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
        {
            if (value is null) {
                if (!CredDelete (Target (key), CredTypeGeneric, 0) && Marshal.GetLastPInvokeError () is var error and not ErrorNotFound)
                    throw new Win32Exception (error);
                return Task.CompletedTask;
            }

            var bytes = Encoding.UTF8.GetBytes (value);
            var blob = Marshal.AllocHGlobal (Math.Max (1, bytes.Length));
            var target = Marshal.StringToHGlobalUni (Target (key));
            var user = Marshal.StringToHGlobalUni (Environment.UserName);
            try {
                Marshal.Copy (bytes, 0, blob, bytes.Length);
                var credential = new Credential {
                    Type = CredTypeGeneric,
                    TargetName = target,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredPersistLocalMachine,
                    UserName = user,
                };
                if (!CredWrite (ref credential, 0))
                    throw new Win32Exception (Marshal.GetLastPInvokeError ());
            } finally {
                // The copy of the secret in unmanaged memory is cleared before it is freed.
                Marshal.Copy (new byte[bytes.Length], 0, blob, bytes.Length);
                Marshal.FreeHGlobal (blob);
                Marshal.FreeHGlobal (target);
                Marshal.FreeHGlobal (user);
            }
            return Task.CompletedTask;
        }

        private static string Target (string key) => SecretStores.Service + ":" + key;

        [StructLayout (LayoutKind.Sequential)]
        private struct Credential
        {
            public int Flags;
            public int Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [LibraryImport ("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static partial bool CredRead (string target, int type, int flags, out IntPtr credential);

        [LibraryImport ("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static partial bool CredWrite (ref Credential credential, int flags);

        [LibraryImport ("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs (UnmanagedType.Bool)]
        private static partial bool CredDelete (string target, int type, int flags);

        [LibraryImport ("advapi32.dll", EntryPoint = "CredFree")]
        private static partial void CredFree (IntPtr buffer);
    }
}
