using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Models.Api;
using Minicon.SimpleAdmin.WebUI.Services;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class ProblemsController : ControllerBase
{
    private readonly StatusReaderService _statusReader;

    public ProblemsController(StatusReaderService statusReader)
    {
        _statusReader = statusReader;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var problems = await _statusReader.GetAllProblemsAsync();
        return Ok(MapToDto(problems));
    }

    [HttpGet("server/{serverId}")]
    public async Task<IActionResult> GetByServer(string serverId)
    {
        var problems = await _statusReader.GetProblemsForServerAsync(serverId);
        return Ok(MapToDto(problems));
    }

    [HttpGet("{problemId}")]
    public async Task<IActionResult> GetById(string problemId, [FromQuery] string? serverId = null)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            return BadRequest(new { message = "Query parameter 'serverId' is required." });
        }

        var server = await _statusReader.GetServerAsync(serverId);
        if (server == null)
        {
            return NotFound(new { message = $"Server '{serverId}' not found" });
        }

        var problem = server.ActiveProblems.FirstOrDefault(p => p.Id == problemId);
        if (problem == null)
        {
            return NotFound(new { message = $"Problem '{problemId}' not found on server '{serverId}'" });
        }

        return Ok(MapToDto(new List<(string ServerId, ActiveProblem Problem)> { (server.ServerId, problem) }).First());
    }

    private static List<ProblemWithServerDto> MapToDto(List<(string ServerId, ActiveProblem Problem)> problems)
    {
        return problems.Select(p => new ProblemWithServerDto
        {
            ServerId = p.ServerId,
            Problem = p.Problem
        }).ToList();
    }
}
