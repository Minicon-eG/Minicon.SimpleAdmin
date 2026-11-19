using System.Globalization;
using System.Text;
using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

internal static class BizTalkInstanceMapper
{
    internal const int MaxErrorBytes = 32 * 1024;

    internal static BizTalkInstance? Map(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !Guid.TryParse(Value(element, "Id"), out var id) || id == Guid.Empty) return null;
        var raw = Value(element, "InstanceStatus");
        var normalized = new string((raw ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
        // An unfamiliar suspended state remains visible, but never implies resumability.
        if (!normalized.StartsWith("suspended", StringComparison.Ordinal)) return null;
        var status = normalized switch
        {
            "suspendedresumable" => BizTalkInstanceStatus.SuspendedResumable,
            "suspendednotresumable" or "suspendednonresumable" => BizTalkInstanceStatus.SuspendedNotResumable,
            _ => BizTalkInstanceStatus.Unknown
        };
        var application = Value(element, "Application");
        var uri = Value(element, "Uri");
        var type = Value(element, "ServiceType");
        var error = Value(element, "ErrorDescription");
        var truncated = error != null && Encoding.UTF8.GetByteCount(error) > MaxErrorBytes;
        if (truncated) error = TruncateUtf8(error!, MaxErrorBytes);
        var time = Value(element, "SuspendTime");
        // Without an explicit zone the provider's local timezone is unknown.
        DateTimeOffset? since = time != null && HasZone(time) &&
            DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        return new(id, application, application == null ? BizTalkApplicationMapping.Unknown : BizTalkApplicationMapping.Known,
            uri != null ? "URI: " + uri : type != null ? "Servicetyp: " + type : null,
            Value(element, "Class") ?? type, status, raw, since, Value(element, "ErrorCode"),
            error, truncated, Value(element, "PendingOperation"));
    }

    internal static string? Value(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool HasZone(string value) => value.EndsWith('Z') ||
        (value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-');

    private static string TruncateUtf8(string value, int maxBytes)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return builder.ToString();
    }
}
