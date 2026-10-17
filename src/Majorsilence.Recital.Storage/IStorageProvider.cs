namespace Majorsilence.Recital.Storage
{
    /// <summary>
    /// A place files live: the sync home or an import source (plan 2.4). Paths are logical, '/'-separated
    /// and relative to the provider's root (<c>Recital/state/abc/dev1.json</c>); see <see cref="StoragePath"/>.
    /// </summary>
    public interface IStorageProvider
    {
        /// <summary>"gdrive", "dropbox", "onedrive", "webdav", "icloud", "local", "memory".</summary>
        string Id { get; }

        StorageCapabilities Capabilities { get; }

        /// <summary>The immediate children of a folder; empty when the folder does not exist.</summary>
        Task<IReadOnlyList<StorageEntry>> ListAsync (string folderPath, CancellationToken ct = default);

        /// <exception cref="FileNotFoundException">There is no file at <paramref name="path"/>.</exception>
        Task<Stream> OpenReadAsync (string path, CancellationToken ct = default);

        /// <summary>Creates or replaces a file, creating its folders as needed.</summary>
        /// <param name="ifMatchEtag">When set, the write happens only if the file's current ETag is this one.</param>
        /// <exception cref="StoragePreconditionFailedException">The ETag did not match.</exception>
        Task<StorageEntry> WriteAsync (string path, Stream content, string? ifMatchEtag = null, CancellationToken ct = default);

        /// <summary>The file or folder at <paramref name="path"/>, or null when there is none.</summary>
        Task<StorageEntry?> StatAsync (string path, CancellationToken ct = default);

        /// <summary>
        /// Files changed since <paramref name="cursor"/> (null: every file). Providers without a change feed
        /// answer by listing, so a caller can always poll this.
        /// </summary>
        Task<ChangeSet> GetChangesAsync (string? cursor, CancellationToken ct = default);
    }

    [Flags]
    public enum StorageCapabilities
    {
        None = 0,
        /// <summary><see cref="IStorageProvider.GetChangesAsync"/> is a real change feed rather than a listing.</summary>
        ChangeFeed = 1,
        /// <summary>Entries carry ETags and <see cref="IStorageProvider.WriteAsync"/> honours <c>ifMatchEtag</c>.</summary>
        ETags = 2,
        /// <summary>The user can browse folders (an import source).</summary>
        Browse = 4,
        /// <summary>Large files upload in chunks.</summary>
        LargeUpload = 8,
    }

    /// <param name="Path">Logical path of the entry.</param>
    /// <param name="IsFolder">True for a folder.</param>
    /// <param name="Size">Bytes; 0 for a folder.</param>
    /// <param name="Modified">Last write time as the provider reports it.</param>
    /// <param name="ETag">Changes whenever the content does; null when the provider has none.</param>
    public sealed record StorageEntry (string Path, bool IsFolder, long Size, DateTimeOffset Modified, string? ETag)
    {
        public string Name => StoragePath.Name (Path);
    }

    /// <param name="Changed">Files created or modified since the cursor (folders are not reported).</param>
    /// <param name="Cursor">Pass back to get the changes after this set.</param>
    /// <param name="IsFullListing">True when <paramref name="Changed"/> is every file, not just the changes.</param>
    public sealed record ChangeSet (IReadOnlyList<StorageEntry> Changed, string Cursor, bool IsFullListing);

    /// <summary>A conditional write found a different version of the file than the caller expected.</summary>
    public sealed class StoragePreconditionFailedException : IOException
    {
        public StoragePreconditionFailedException (string path)
            : base ($"'{path}' changed since it was read.")
        {
            Path = path;
        }

        public string Path { get; }
    }
}

namespace Majorsilence.Recital.Storage
{
    /// <summary>The account a cloud provider is signed in to.</summary>
    /// <param name="Id">Stable and unique (Dropbox account id, Graph user id, Drive permission id).</param>
    /// <param name="Name">For display: an email address or name.</param>
    public sealed record StorageAccount (string Id, string Name);

    /// <summary>A provider that signs in to an account (the cloud ones).</summary>
    public interface IStorageAccountProvider
    {
        Task<StorageAccount> GetAccountAsync (CancellationToken ct = default);
    }
}
