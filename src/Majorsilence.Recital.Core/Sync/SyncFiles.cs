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
    /// <c>Annotations</c> holds this device's latest version of every annotation it created, changed or
    /// deleted (tombstones included); the library merges them with every other device's.
    /// </summary>
    public sealed record BookStateFile (int FormatVersion, string BookId, string DeviceId, ReadingProgress? Progress, IReadOnlyList<Annotations.Annotation>? Annotations);

    /// <summary>A position plus when (by HLC) the device was there.</summary>
    public sealed record ReadingProgress (ReadingLocator Locator, Hlc Hlc, DateTimeOffset UpdatedAt);

    /// <summary><c>settings/&lt;deviceId&gt;.json</c>: synced preferences, last-writer-wins per key across devices.</summary>
    public sealed record SettingsFile (int FormatVersion, string DeviceId, IReadOnlyDictionary<string, SettingValue> Values);

    public sealed record SettingValue (string Value, Hlc Hlc);

    /// <summary>Enums in sync files are camelCase strings ("highlight"); numbers, as early Phase 6 builds wrote, still read.</summary>
    public sealed class CamelCaseEnumConverter<T> () : JsonStringEnumConverter<T> (JsonNamingPolicy.CamelCase) where T : struct, Enum;

    [JsonSourceGenerationOptions (
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable (typeof (FormatFile))]
    [JsonSerializable (typeof (DeviceFile))]
    [JsonSerializable (typeof (BookMeta))]
    [JsonSerializable (typeof (BookStateFile))]
    [JsonSerializable (typeof (Annotations.Annotation))]
    [JsonSerializable (typeof (SettingsFile))]
    [JsonSerializable (typeof (LocalDeviceFile))]
    [JsonSerializable (typeof (SyncOutboxFile))]
    [JsonSerializable (typeof (SyncIndexFile))]
    [JsonSerializable (typeof (SyncHomeSettings))]
    [JsonSerializable (typeof (SyncHomeKind))]
    internal sealed partial class SyncJson : JsonSerializerContext
    {
    }
}
