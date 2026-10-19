using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Platform
{
    public static class SecretStores
    {
        /// <summary>
        /// The secure store for this platform, or memory when there is none (the browser, by design; Android
        /// and iOS until their stores are wired up; a Linux desktop without a Secret Service).
        /// </summary>
        /// <summary>
        /// The secure store a phone or tablet host registers at start-up (Android Keystore, iOS Keychain), which
        /// this library cannot reach itself. Used before any other.
        /// </summary>
        public static ISecretStore? Host { get; set; }

        public static ISecretStore ForCurrentPlatform ()
        {
            if (Host is { } host)
                return host;
            if (OperatingSystem.IsWindows ())
                return new WindowsCredentialStore ();
            if (OperatingSystem.IsMacOS ())
                return new MacKeychainStore ();
            if (OperatingSystem.IsLinux () && !OperatingSystem.IsAndroid () && LinuxSecretServiceStore.IsAvailable ())
                return new LinuxSecretServiceStore ();
            return new InMemorySecretStore ();
        }

        /// <summary>Secret keys are namespaced so other apps' entries are never touched.</summary>
        internal const string Service = "Recital";
    }
}
