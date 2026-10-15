using Majorsilence.Recital.Epub;
using Xunit;

namespace Majorsilence.Recital.Core.Tests
{
    internal static class Corpus
    {
        public const string Alice = "pg-11-alice-epub3.epub";
        public const string SePride = "se-austen-pride-and-prejudice.epub";
        public const string PgPride = "pg-1342-pride-and-prejudice-epub2.epub";
        public const string Frankenstein = "se-shelley-frankenstein.epub";

        public static string[] All => [Alice, SePride, PgPride, Frankenstein];

        public static string PathOf (string file) => Path.Combine (AppContext.BaseDirectory, "corpus", file);

        public static FileStream Open (string file) => File.OpenRead (PathOf (file));

        public static Task<EpubBook> OpenBookAsync (string file) => EpubBook.OpenAsync (PathOf (file), TestContext.Current.CancellationToken);
    }
}
