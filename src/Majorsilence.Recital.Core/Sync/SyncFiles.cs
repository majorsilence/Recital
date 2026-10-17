using System.Text.Json;
using System.Text.Json.Serialization;
using Majorsilence.Recital.Core.Library;
using Majorsilence.Recital.Core.Reading;

namespace Majorsilence.Recital.Core.Sync
{
    /// <summary><c>format.json</c>.</summary>
    public sealed record FormatFile (int FormatVersion);

    /// <summary><c>devices/&lt;deviceId&gt;.json</c>.</summary>
    public sealed record DeviceFile (int FormatVersion, string DeviceId, string Name, string Platform, DateTimeOffset LastSeen);

    /// <summary>
    /// <c>state/&lt;bookId&gt;/&lt;deviceId&gt;.json</c>: one device's progress and annotations for one book.
    /// Annotations arrive in Phase 6; until then they are carried through unchanged, so a newer build's
    /// annotations survive this build rewriting the file.
    /// </summary>
    public sealed record BookStateFile (int FormatVersion, string BookId, string DeviceId, ReadingProgress? Progress, IReadOnlyList<JsonElement> Annotations);

    /// <summary>A position plus when (by HLC) the device was there.</summary>
    public sealed record ReadingProgress (ReadingLocator Locator, Hlc Hlc, DateTimeOffset UpdatedAt);

    /// <summary><c>settings/&lt;deviceId&gt;.json</c>: synced preferences, last-writer-wins per key across devices.</summary>
    public sealed record SettingsFile (int FormatVersion, string DeviceId, IReadOnlyDictionary<string, SettingValue> Values);

    public sealed record SettingValue (string Value, Hlc Hlc);

    [JsonSourceGenerationOptions (
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable (typeof (FormatFile))]
    [JsonSerializable (typeof (DeviceFile))]
    [JsonSerializable (typeof (BookMeta))]
    [JsonSerializable (typeof (BookStateFile))]
    [JsonSerializable (typeof (SettingsFile))]
    [JsonSerializable (typeof (LocalDeviceFile))]
    [JsonSerializable (typeof (SyncOutboxFile))]
    [JsonSerializable (typeof (SyncIndexFile))]
    [JsonSerializable (typeof (SyncHomeSettings))]
    internal sealed partial class SyncJson : JsonSerializerContext
    {
    }
}
