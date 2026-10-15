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

        /// <summary>Book and device ids are lowercase hex (a SHA-256 or a GUID); anything else is not ours.</summary>
        public static bool IsValidId (string id) => id.Length is > 0 and <= 64 && id.All (c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        private static string Checked (string id)
            => IsValidId (id) ? id : throw new ArgumentException ($"'{id}' is not a valid id.", nameof (id));
    }
}
