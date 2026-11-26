using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Counts the findings of the file / log monitoring in a uniform way. The WebUI badge and the
/// static HTML page use the same counter so that a finding that only exists in one of them
/// can never be shown as "OK" in one of the two representations.
/// </summary>
public static class FileMonitoringFindingCounter
{
    /// <summary>The number of findings of a file-monitoring state (all finding kinds).</summary>
    public static int CountFindings(FileMonitoringState? state)
    {
        if (state == null)
        {
            return 0;
        }

        return state.StuckFiles.Count
               + state.LogMatches.Count
               + state.PendingRuns.Count
               + state.AbandonedRuns.Count
               + state.MissingRuns.Count
               + CountErrors(state);
    }

    /// <summary>
    /// The number of errors of a file-monitoring state: an unreachable path (<see cref=
    /// FileMonitoringState.AnyError"/>) and the evaluation errors of the rules (e.g. a regular
    /// expression that hit its time limit). An error is a finding of its own — it must never be
    /// shown as "OK".
    /// </summary>
    public static int CountErrors(FileMonitoringState? state)
    {
        if (state == null)
        {
            return 0;
        }

        return (state.AnyError ? 1 : 0) + state.EvaluationErrors.Count;
    }

    /// <summary>The number of critical findings of a file-monitoring state.</summary>
    public static int CountCritical(FileMonitoringState? state)
    {
        if (state == null)
        {
            return 0;
        }

        return state.StuckFiles.Count(f => f.IsCritical)
               + state.LogMatches.Count(m => m.IsCritical)
               + state.PendingRuns.Count(p => p.IsCritical)
               + state.AbandonedRuns.Count(a => a.IsCritical)
               + state.MissingRuns.Count(m => m.IsCritical)
               + (state.AnyError ? 1 : 0)
               + state.EvaluationErrors.Count;
    }

    /// <summary>The German detail text of the badge (all finding kinds).</summary>
    public static string Describe(FileMonitoringState? state)
    {
        if (state == null)
        {
            return string.Empty;
        }

        var errors = CountErrors(state);
        var parts = new List<string>
        {
            $"{state.StuckFiles.Count} hängende Dateien",
            $"{state.LogMatches.Count} Log-Treffer",
            $"{state.PendingRuns.Count} offene Läufe",
            $"{state.AbandonedRuns.Count} abgebrochene Läufe",
            $"{state.MissingRuns.Count} fehlende erwartete Läufe"
        };

        if (errors > 0)
        {
            parts.Add($"{errors} Auswertungsfehler");
        }

        return string.Join(", ", parts);
    }
}
