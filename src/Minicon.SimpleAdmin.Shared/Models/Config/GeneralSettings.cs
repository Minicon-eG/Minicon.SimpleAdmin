namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// General application settings
/// </summary>
public class GeneralSettings
{
    /// <summary>
    /// Check interval in seconds (default: 60)
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Path for output files (default: "./output")
    /// </summary>
    public string OutputPath { get; set; } = "./output";

    /// <summary>
    /// Number of days to retain history (default: 30)
    /// </summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>
    /// Application title shown in the WebUI navigation and browser tab
    /// (empty = "SimpleAdmin")
    /// </summary>
    public string? Title { get; set; }
}
