using Minicon.SimpleAdmin.WebUI.Helpers;

namespace Minicon.SimpleAdmin.WebUI.Models.BizTalk;

public enum BizTalkInstanceStatus { Unknown, SuspendedResumable, SuspendedNotResumable }
public enum BizTalkApplicationMapping { Unknown, Known }

/// <summary>Read-only live data, deliberately separate from the compact worker state.</summary>
public sealed record BizTalkInstance(
    Guid InstanceId, string? Application, BizTalkApplicationMapping ApplicationMapping,
    string? ServiceOrPort, string? Type, BizTalkInstanceStatus Status, string? RawStatus,
    DateTimeOffset? SuspendedSince, string? ErrorCode, string? ErrorText,
    bool ErrorTextTruncated, string? PendingOperation)
{
    public string? SuspendedSinceDisplay => SuspendedSince is { } value ? Display.LocalTimeWithZone(value) : null;
}

public sealed record BizTalkInstancePage(
    string ServerId, string Provider, DateTimeOffset FetchedAtUtc,
    IReadOnlyList<BizTalkInstance> Items, int Offset, int PageSize, bool HasMore,
    bool Truncated, int ScannedCount, int FilteredCountWithinLimit, string? Warning)
{
    public string FetchedAtDisplay => Display.LocalTimeWithZone(FetchedAtUtc);
}

public sealed record BizTalkInstanceQuery(
    int Offset = 0, int PageSize = 50, string? Application = null, string? Error = null,
    bool MissingApplication = false);
