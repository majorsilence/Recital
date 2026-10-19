using SHA256 = System.Security.Cryptography.SHA256;
using System.Text;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Android
{
    /// <summary>
    /// Secrets (sign-in tokens, voice keys) encrypted with an AES-GCM key that lives in the Android Keystore and
    /// cannot be read out of it, each stored as IV and ciphertext in the app's no-backup folder: private to the app,
    /// never synced, and not restored to another device (where the key would not exist anyway).
    /// </summary>
    public sealed class AndroidKeystoreSecretStore (string folder) : ISecretStore
    {
        private const string Provider = "AndroidKeyStore";
        private const string Alias = "recital-secrets";
        private const int TagBits = 128;

        public string Name => "Android Keystore";
        public bool IsPersistent => true;

        public Task<string?> GetAsync (string key, CancellationToken ct = default)
            => Task.Run (() => {
                var path = PathFor (key);
                if (!File.Exists (path))
                    return null;
                var stored = File.ReadAllBytes (path);
                var ivLength = stored[0];
                var cipher = Cipher.GetInstance ("AES/GCM/NoPadding")!;
                cipher.Init (CipherMode.DecryptMode, Key (), new GCMParameterSpec (TagBits, stored, 1, ivLength));
                return Encoding.UTF8.GetString (cipher.DoFinal (stored, 1 + ivLength, stored.Length - 1 - ivLength)!);
            }, ct);

        public Task SetAsync (string key, string? value, CancellationToken ct = default)
            => Task.Run (() => {
                var path = PathFor (key);
                if (value is null) {
                    File.Delete (path);
                    return;
                }
                var cipher = Cipher.GetInstance ("AES/GCM/NoPadding")!;
                cipher.Init (CipherMode.EncryptMode, Key ());
                var iv = cipher.GetIV ()!;
                var sealed_ = cipher.DoFinal (Encoding.UTF8.GetBytes (value))!;
                var stored = new byte[1 + iv.Length + sealed_.Length];
                stored[0] = (byte) iv.Length;
                iv.CopyTo (stored, 1);
                sealed_.CopyTo (stored, 1 + iv.Length);
                Directory.CreateDirectory (folder);
                var part = path + ".part";
                File.WriteAllBytes (part, stored);
                File.Move (part, path, overwrite: true);
            }, ct);

        // The file name says nothing about the key: its hash.
        private string PathFor (string key)
            => System.IO.Path.Combine (folder, Convert.ToHexStringLower (SHA256.HashData (Encoding.UTF8.GetBytes (SecretsService + key))) + ".secret");

        private const string SecretsService = "Recital:";

        private static IKey Key ()
        {
            var store = KeyStore.GetInstance (Provider)!;
            store.Load (null);
            if (store.GetKey (Alias, null) is { } existing)
                return existing;
            var generator = KeyGenerator.GetInstance (KeyProperties.KeyAlgorithmAes, Provider)!;
            generator.Init (new KeyGenParameterSpec.Builder (Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes (KeyProperties.BlockModeGcm)!
                .SetEncryptionPaddings (KeyProperties.EncryptionPaddingNone)!
                .SetKeySize (256)!
                .Build ());
            return generator.GenerateKey ()!;
        }
    }
}
