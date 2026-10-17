using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Platform
{
    /// <summary>
    /// The freedesktop Secret Service (GNOME Keyring, KWallet) through libsecret, which the desktop already
    /// has; it is loaded at run time, not shipped. Items are stored under the schema
    /// <c>com.majorsilence.Recital</c> with one attribute, <c>key</c>.
    /// </summary>
    /// <remarks>
    /// libsecret's calls block on D-Bus, so they run on the thread pool. The non-variadic <c>...v_sync</c>
    /// functions are used: a variadic call cannot be declared portably for P/Invoke (arm64 passes variadic
    /// arguments on the stack).
    /// </remarks>
    [SupportedOSPlatform ("linux")]
    public sealed unsafe class LinuxSecretServiceStore : ISecretStore
    {
        private const string LibSecret = "libsecret-1.so.0";
        private const string LibGlib = "libglib-2.0.so.0";
        private const int SchemaSize = 592;

        private static readonly Lazy<Native?> native = new (Native.TryLoad);

        public string Name => "Secret Service (keyring)";
        public bool IsPersistent => true;

        /// <summary>libsecret can be loaded and there is a D-Bus session to find the service on.</summary>
        public static bool IsAvailable ()
            => !string.IsNullOrEmpty (Environment.GetEnvironmentVariable ("DBUS_SESSION_BUS_ADDRESS")) && native.Value is not null;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
            => Task.Run (() => {
                var api = Api ();
                using var call = new Call (api, key);
                IntPtr error = IntPtr.Zero;
                var result = api.Lookup (call.Schema, call.Attributes, IntPtr.Zero, &error);
                Check (api, error);
                if (result == IntPtr.Zero)
                    return null;
                try {
                    return Marshal.PtrToStringUTF8 (result);
                } finally {
                    api.PasswordFree (result);
                }
            }, ct);

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
            => Task.Run (() => {
                var api = Api ();
                using var call = new Call (api, key);
                IntPtr error = IntPtr.Zero;
                if (value is null) {
                    api.Clear (call.Schema, call.Attributes, IntPtr.Zero, &error);
                    Check (api, error);
                    return;
                }
                var label = Marshal.StringToCoTaskMemUTF8 ("Recital: " + key);
                var password = Marshal.StringToCoTaskMemUTF8 (value);
                try {
                    api.Store (call.Schema, call.Attributes, IntPtr.Zero, label, password, IntPtr.Zero, &error);
                    Check (api, error);
                } finally {
                    // Clear the copy of the secret before freeing it.
                    var length = System.Text.Encoding.UTF8.GetByteCount (value);
                    new Span<byte> ((void*)password, length).Clear ();
                    Marshal.FreeCoTaskMem (password);
                    Marshal.FreeCoTaskMem (label);
                }
            }, ct);

        private static Native Api () => native.Value ?? throw new PlatformNotSupportedException ("libsecret is not installed.");

        private static void Check (Native api, IntPtr error)
        {
            if (error == IntPtr.Zero)
                return;
            // GError: GQuark domain; gint code; gchar *message.
            var message = Marshal.PtrToStringUTF8 (Marshal.ReadIntPtr (error, 8)) ?? "unknown error";
            api.ErrorFree (error);
            throw new IOException ("The keyring (Secret Service) refused: " + message);
        }

        /// <summary>The schema and attribute table for one key, freed together.</summary>
        private readonly struct Call : IDisposable
        {
            private readonly Native api;
            private readonly IntPtr schema_name;
            private readonly IntPtr attribute_name;
            private readonly IntPtr key_value;

            public Call (Native api, string key)
            {
                this.api = api;
                schema_name = Marshal.StringToCoTaskMemUTF8 ("com.majorsilence.Recital");
                attribute_name = Marshal.StringToCoTaskMemUTF8 ("key");
                key_value = Marshal.StringToCoTaskMemUTF8 (key);

                // SecretSchema { const gchar *name; SecretSchemaFlags flags; SecretSchemaAttribute attributes[32]; ... }
                Schema = Marshal.AllocCoTaskMem (SchemaSize);
                new Span<byte> ((void*)Schema, SchemaSize).Clear ();
                Marshal.WriteIntPtr (Schema, 0, schema_name);
                Marshal.WriteInt32 (Schema, 8, 0); // SECRET_SCHEMA_NONE
                Marshal.WriteIntPtr (Schema, 16, attribute_name);
                Marshal.WriteInt32 (Schema, 24, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING

                Attributes = api.HashTableNew (api.StrHash, api.StrEqual);
                api.HashTableInsert (Attributes, attribute_name, key_value);
            }

            public IntPtr Schema { get; }
            public IntPtr Attributes { get; }

            public void Dispose ()
            {
                api.HashTableUnref (Attributes);
                Marshal.FreeCoTaskMem (Schema);
                Marshal.FreeCoTaskMem (schema_name);
                Marshal.FreeCoTaskMem (attribute_name);
                Marshal.FreeCoTaskMem (key_value);
            }
        }

        private sealed class Native
        {
            public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int> Store;
            public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr> Lookup;
            public delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int> Clear;
            public delegate* unmanaged<IntPtr, void> PasswordFree;
            public delegate* unmanaged<IntPtr, IntPtr, IntPtr> HashTableNew;
            public delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> HashTableInsert;
            public delegate* unmanaged<IntPtr, void> HashTableUnref;
            public delegate* unmanaged<IntPtr, void> ErrorFree;
            public IntPtr StrHash;
            public IntPtr StrEqual;

            public static Native? TryLoad ()
            {
                if (!NativeLibrary.TryLoad (LibSecret, out var secret) || !NativeLibrary.TryLoad (LibGlib, out var glib))
                    return null;
                try {
                    return new Native {
                        Store = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, int>)NativeLibrary.GetExport (secret, "secret_password_storev_sync"),
                        Lookup = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)NativeLibrary.GetExport (secret, "secret_password_lookupv_sync"),
                        Clear = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)NativeLibrary.GetExport (secret, "secret_password_clearv_sync"),
                        PasswordFree = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport (secret, "secret_password_free"),
                        HashTableNew = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)NativeLibrary.GetExport (glib, "g_hash_table_new"),
                        HashTableInsert = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)NativeLibrary.GetExport (glib, "g_hash_table_insert"),
                        HashTableUnref = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport (glib, "g_hash_table_unref"),
                        ErrorFree = (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport (glib, "g_error_free"),
                        StrHash = NativeLibrary.GetExport (glib, "g_str_hash"),
                        StrEqual = NativeLibrary.GetExport (glib, "g_str_equal"),
                    };
                } catch (EntryPointNotFoundException) {
                    // A libsecret older than 0.18 has no ...v_sync functions.
                    return null;
                }
            }
        }
    }
}
