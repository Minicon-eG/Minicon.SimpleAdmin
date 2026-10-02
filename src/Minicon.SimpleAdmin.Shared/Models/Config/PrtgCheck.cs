namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// PRTG check definition
/// </summary>
public class PrtgCheck
{
    public string Metric { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public int? SensorId { get; set; }
    public string? Target { get; set; }
    public Dictionary<string, object>? Filters { get; set; }
    public int? IntervalSeconds { get; set; }
    public Threshold? Threshold { get; set; }
}
