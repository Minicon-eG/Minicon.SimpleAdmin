namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// Normalization of scheduled task paths and path patterns, shared by the worker's ScheduledTaskChecker
/// (watch patterns, event task paths) and <see cref="ScheduledTaskIgnoreMatcher"/> so a path written as
/// "Minicon\Export" in config.json matches the "\Minicon\Export" reported by the Task Scheduler.
/// </summary>
public static class ScheduledTaskPaths
{
    /// <summary>Trims the path and adds the leading backslash ("Minicon\Export" → "\Minicon\Export").</summary>
    public static string NormalizePath(string? path)
    {
        var p = (path ?? string.Empty).Trim();
        return p.Length == 0 || p[0] == '\\' ? p : "\\" + p;
    }

    /// <summary>
    /// Like <see cref="NormalizePath"/>, but a pattern starting with a wildcard is kept as is ("*Export*").
    /// </summary>
    public static string NormalizePattern(string? pattern)
    {
        var p = (pattern ?? string.Empty).Trim();
        return p.Length == 0 || p[0] is '\\' or '*' or '?' ? p : "\\" + p;
    }
}
