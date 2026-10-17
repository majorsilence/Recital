using Majorsilence.Recital.Storage;

namespace Majorsilence.Recital.Core.Sync
{
    /// <summary>Where each file lives in the sync home (docs/SYNC_FORMAT.md).</summary>
    public static class SyncPaths
    {
        public const string Root = "Recital";
        public const int FormatVersion = 1;

        public static string Format => StoragePath.Combine (Root, "format.json");
        public static string BooksFolder => StoragePath.Combine (Root, "books");
        public static string MetaFolder => StoragePath.Combine (Root, "meta");
        public static string Book (string bookId) => StoragePath.Combine (Root, "books", Checked (bookId) + ".epub");
        public static string Meta (string bookId) => StoragePath.Combine (Root, "meta", Checked (bookId) + ".json");
        public static string Cover (string bookId) => StoragePath.Combine (Root, "covers", Checked (bookId) + ".jpg");
        public static string StateFolder (string bookId) => StoragePath.Combine (Root, "state", Checked (bookId));
        public static string State (string bookId, string deviceId) => StoragePath.Combine (StateFolder (bookId), Checked (deviceId) + ".json");
        public static string Device (string deviceId) => StoragePath.Combine (Root, "devices", Checked (deviceId) + ".json");
        public static string SettingsFolder => StoragePath.Combine (Root, "settings");
        public static string Settings (string deviceId) => StoragePath.Combine (SettingsFolder, Checked (deviceId) + ".json");

        /// <summary>
        /// What a path in the sync home is to <paramref name="deviceId"/>, which decides how the sync engine
        /// moves it (plan 2.3).
        /// </summary>
        public static SyncFileKind Classify (string path, string deviceId)
        {
            var parts = path.Split ('/');
            if (parts.Length < 2 || parts[0] != Root)
                return SyncFileKind.Ignored;
            if (parts.Length == 2)
                return parts[1] == "format.json" ? SyncFileKind.WriteOnce : SyncFileKind.Ignored;

            switch (parts[1]) {
            case "books" when parts.Length == 3:
                return CanonicalId (parts[2], ".epub") is not null ? SyncFileKind.WriteOnce : SyncFileKind.Ignored;
            case "meta" when parts.Length == 3:
                return CanonicalId (parts[2], ".json") is not null ? SyncFileKind.WriteOnce : SyncFileKind.Ignored;
            case "covers" when parts.Length == 3:
                return CanonicalId (parts[2], ".jpg") is not null ? SyncFileKind.WriteOnce : SyncFileKind.Ignored;
            case "devices" or "settings" when parts.Length == 3:
                return DeviceFile (parts[2], deviceId);
            case "state" when parts.Length == 4 && IsValidId (parts[2]):
                return DeviceFile (parts[3], deviceId);
            default:
                return SyncFileKind.Ignored;
            }
        }

        private static SyncFileKind DeviceFile (string name, string deviceId)
        {
            if (!name.EndsWith (".json", StringComparison.Ordinal) || name.StartsWith ('.'))
                return SyncFileKind.Ignored;
            if (CanonicalId (name, ".json") is not { } id)
                return SyncFileKind.ConflictCopy;
            return id == deviceId ? SyncFileKind.Own : SyncFileKind.OtherDevice;
        }

        private static string? CanonicalId (string name, string extension)
            => name.EndsWith (extension, StringComparison.Ordinal) && name[..^extension.Length] is var id && IsValidId (id) ? id : null;

        /// <summary>Book and device ids are lowercase hex (a SHA-256 or a GUID); anything else is not ours.</summary>
        public static bool IsValidId (string id) => id.Length is > 0 and <= 64 && id.All (c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static string Checked (string id)
            => IsValidId (id) ? id : throw new ArgumentException ($"'{id}' is not a valid id.", nameof (id));
    }

    public enum SyncFileKind
    {
        /// <summary>Not part of the format (a temporary file, a stray, a conflicted copy of a write-once file): never moved.</summary>
        Ignored,

        /// <summary><c>format.json</c>, <c>books/</c>, <c>meta/</c>, <c>covers/</c>: whoever writes first wins; never overwritten in the sync home.</summary>
        WriteOnce,

        /// <summary>This device's own <c>state/*/</c>, <c>devices/</c> or <c>settings/</c> file: only this device writes it.</summary>
        Own,

        /// <summary>Another device's own file: read, never written.</summary>
        OtherDevice,

        /// <summary>A sync client's conflicted copy of a device file (<c>id (1).json</c>): read as an extra merge input, never written.</summary>
        ConflictCopy,
    }
}
