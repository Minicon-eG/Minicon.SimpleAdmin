using System.Text.Json.Serialization;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.WebUI.Models.ScheduledTasks;

/// <summary>Control action on a scheduled task.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduledTaskAction>))]
public enum ScheduledTaskAction
{
    /// <summary>Enable the task.</summary>
    Enable,

    /// <summary>Disable the task.</summary>
    Disable,

    /// <summary>Start the task now.</summary>
    Run,

    /// <summary>Stop the running instance.</summary>
    End
}

/// <summary>Result of one action on one server.</summary>
public sealed record ScheduledTaskActionResult(
    string Server,
    string TaskPath,
    ScheduledTaskAction Action,
    bool Success,
    string Message);

/// <summary>
/// Result of "activate exclusively": disables the task on every other server of the group first,
/// then enables it on the target. <see cref="Success"/> only when every step succeeded.
/// </summary>
public sealed record ScheduledTaskExclusiveResult(
    string TaskPath,
    string TargetServer,
    bool Success,
    IReadOnlyList<ScheduledTaskActionResult> Steps,
    string Message);

/// <summary>Live state of a task on one server (queried remotely, independent of the worker cycle).</summary>
public sealed record ScheduledTaskLiveState(
    string Server,
    bool Reachable,
    ScheduledTaskInfo? Task,
    string? Error);

/// <summary>One audit entry of a control action (persisted in scheduled-task-actions.json).</summary>
public sealed class ScheduledTaskAuditEntry
{
    /// <summary>When the action was executed (UTC).</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Authenticated Windows user who triggered the action.</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>Target server.</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Full task path.</summary>
    public string TaskPath { get; set; } = string.Empty;

    /// <summary>Action, or "ActivateExclusively" for the group operation.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Whether the action succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Result / error message (German).</summary>
    public string Message { get; set; } = string.Empty;
}
