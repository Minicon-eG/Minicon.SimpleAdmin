using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.WebUI.Models.Scripts;

[JsonConverter(typeof(JsonStringEnumConverter<ScriptRunStatus>))]
public enum ScriptRunStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    /// <summary>The WebUI process ended while the run was active (e.g. IIS recycle).</summary>
    Interrupted
}

/// <summary>
/// One execution of a <see cref="ScriptDefinition"/> (audit record + output).
/// </summary>
public sealed class ScriptRun
{
    public string Id { get; set; } = string.Empty;
    public string ScriptId { get; set; } = string.Empty;
    public string ScriptName { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>Parameter values as passed (lists joined with ", "). Switches that are off are omitted.</summary>
    public Dictionary<string, string> Arguments { get; set; } = new();

    /// <summary>Equivalent PowerShell call, e.g. <c>.\Copy.ps1 -SourceDate "2025-07-20" -DryRun</c>.</summary>
    public string? CommandLine { get; set; }

    /// <summary>Raw form values, used for "erneut ausführen".</summary>
    public Dictionary<string, List<string>> InputValues { get; set; } = new();

    public string? StartedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    /// <summary>Last status change; decides which copy wins when two WebUI processes merge history.json.</summary>
    public DateTime? UpdatedAt { get; set; }
    public ScriptRunStatus Status { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public string? CancelledBy { get; set; }
    public int? ProcessId { get; set; }

    [JsonIgnore]
    public bool IsRunning => Status == ScriptRunStatus.Running;

    [JsonIgnore]
    public TimeSpan Duration => (FinishedAt ?? DateTime.UtcNow) - StartedAt;

    /// <summary>Independent copy (callers must not see later status changes of the live object).</summary>
    public ScriptRun Clone()
    {
        var copy = (ScriptRun)MemberwiseClone();
        copy.Arguments = new Dictionary<string, string>(Arguments);
        copy.InputValues = InputValues.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        return copy;
    }
}

/// <summary>
/// One line of script output.
/// </summary>
public sealed record ScriptOutputLine(DateTime Timestamp, bool IsError, string Text);
