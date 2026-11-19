using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.WebUI.Models.BizTalk;

[JsonConverter(typeof(JsonStringEnumConverter<BizTalkInstanceAction>))]
public enum BizTalkInstanceAction { Resume, Terminate }
[JsonConverter(typeof(JsonStringEnumConverter<BizTalkActionOutcome>))]
public enum BizTalkActionOutcome { Accepted, Rejected, Failed, Unknown }
public sealed record BizTalkActionResult(Guid OperationId, string ServerId, Guid InstanceId,
    BizTalkInstanceAction Action, BizTalkActionOutcome Outcome, string Message, string ExecutingAccount,
    bool AuditPersisted, string? ObservedStatus, int HttpStatus);
public sealed record BizTalkAuditEntry(Guid OperationId, string Phase, DateTimeOffset Timestamp,
    string User, string ExecutingAccount, string Server, Guid InstanceId, string? Application,
    BizTalkApplicationMapping ApplicationMapping, string Provider, BizTalkInstanceAction Action,
    BizTalkActionOutcome Outcome, string Message, string? RequestedServer = null, bool? ServerKnown = null);
