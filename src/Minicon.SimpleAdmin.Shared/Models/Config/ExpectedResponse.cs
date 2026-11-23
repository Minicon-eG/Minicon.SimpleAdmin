namespace Minicon.SimpleAdmin.Models.Config;

/// <summary>
/// Expected response criteria
/// </summary>
public class ExpectedResponse : ConfigModel
{
    public List<int>? StatusCodes { get; set; }
    public List<string>? BodyContains { get; set; }
    public Dictionary<string, string>? Headers { get; set; }
    public List<JsonPathAssertion>? JsonPath { get; set; }
}
