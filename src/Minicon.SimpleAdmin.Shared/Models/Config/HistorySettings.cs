namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// History settings for configuring how many historical data points to keep
/// </summary>
public class HistorySettings
{
    /// <summary>
    /// Maximum number of historical entries to keep (Default: 500)
    /// </summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>
    /// Minimum number of consecutive checks required before a status change is confirmed (Default: 5)
    /// This prevents alert fatigue from temporary spikes in CPU/Memory usage.
    /// </summary>
    public int MinConsecutiveChecks { get; set; } = 5;
}
