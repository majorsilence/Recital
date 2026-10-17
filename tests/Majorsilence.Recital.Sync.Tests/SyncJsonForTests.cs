using System.Text.Json;

namespace Majorsilence.Recital.Sync.Tests
{
    /// <summary>Reads sync-home files the way the format describes them (camelCase JSON), independently of Core's serializer.</summary>
    internal static class SyncJsonForTests
    {
        public static readonly JsonSerializerOptions Options = new (JsonSerializerDefaults.Web);
    }
}
