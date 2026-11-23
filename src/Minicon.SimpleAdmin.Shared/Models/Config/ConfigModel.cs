using System.Text.Json;
using System.Text.Json.Serialization;

namespace Minicon.SimpleAdmin.Models.Config;

public abstract class ConfigModel
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = new();
}
