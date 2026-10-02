namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Settings for the central HTML generator that aggregates status from multiple servers
/// </summary>
public class CentralGeneratorSettings
{
    /// <summary>
    /// Gets or sets whether this instance acts as the central HTML generator.
    /// Only one instance should have this enabled - it reads JSON from all servers
    /// and generates the aggregated overview page.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the path to read status JSON files from.
    /// This is typically the same as CentralOutputPath where all servers write their status.
    /// Supports UNC paths: \\fileserver\status
    /// </summary>
    /// <example>\\\\fileserver\\simpleadmin\\status</example>
    public string? CentralStatusPath { get; set; }

    /// <summary>
    /// Gets or sets the output path for generated HTML files.
    /// If not set, files are written to the local wwwroot directory.
    /// </summary>
    /// <example>\\\\fileserver\\simpleadmin\\html</example>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Gets or sets the file pattern for status files.
    /// Default: *.status.json
    /// </summary>
    public string FilePattern { get; set; } = "*.status.json";

    /// <summary>
    /// Gets or sets the maximum age in minutes for status files.
    /// Files older than this are marked as "Unknown" status.
    /// Default: 5 minutes
    /// </summary>
    public int StaleThresholdMinutes { get; set; } = 5;
}
