namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// JSON path assertion for response validation
/// </summary>
public class JsonPathAssertion
{
    public string Path { get; set; } = string.Empty;
    public string Operator { get; set; } = "==";
    public object? Value { get; set; }
}
