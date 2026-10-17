namespace Majorsilence.Recital.Storage
{
    /// <summary>The storage could not be reached (offline, DNS, timeout, a server error): try again later.</summary>
    public class StorageUnavailableException : IOException
    {
        public StorageUnavailableException (string message, Exception? inner = null) : base (message, inner)
        {
        }
    }

    /// <summary>
    /// The storage refused the credentials (expired or revoked token, wrong password). Retrying will not
    /// help; the user has to sign in again.
    /// </summary>
    public sealed class StorageAuthenticationException : StorageUnavailableException
    {
        public StorageAuthenticationException (string message, Exception? inner = null) : base (message, inner)
        {
        }
    }
}
