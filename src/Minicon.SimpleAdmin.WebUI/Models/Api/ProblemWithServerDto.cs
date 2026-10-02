using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.WebUI.Models.Api;

/// <summary>
/// Problem payload that includes the owning server ID.
/// </summary>
public class ProblemWithServerDto
{
    public string ServerId { get; set; } = string.Empty;

    public ActiveProblem Problem { get; set; } = new();
}
