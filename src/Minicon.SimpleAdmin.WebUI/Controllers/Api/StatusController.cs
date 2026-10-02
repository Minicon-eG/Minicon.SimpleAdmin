using Microsoft.AspNetCore.Mvc;
using Minicon.SimpleAdmin.WebUI.Services;

namespace Minicon.SimpleAdmin.WebUI.Controllers.Api;

[ApiController]
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    private readonly StatusReaderService _statusReader;

    public StatusController(StatusReaderService statusReader)
    {
        _statusReader = statusReader;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var servers = await _statusReader.GetAllServersAsync();
        return Ok(servers.Select(s => new
        {
            s.ServerId,
            s.OverallStatus,
            s.LastCheck,
            ProblemCount = s.ActiveProblems.Count
        }));
    }

    [HttpGet("{serverId}")]
    public async Task<IActionResult> GetById(string serverId)
    {
        var server = await _statusReader.GetServerAsync(serverId);
        if (server == null)
        {
            return NotFound(new { message = $"Server '{serverId}' not found" });
        }
        return Ok(server);
    }

    [HttpGet("{serverId}/metrics")]
    public async Task<IActionResult> GetMetrics(string serverId)
    {
        var server = await _statusReader.GetServerAsync(serverId);
        if (server == null)
        {
            return NotFound(new { message = $"Server '{serverId}' not found" });
        }
        return Ok(server.Metrics);
    }

    [HttpGet("{serverId}/apppools")]
    public async Task<IActionResult> GetAppPools(string serverId)
    {
        var server = await _statusReader.GetServerAsync(serverId);
        if (server == null)
        {
            return NotFound(new { message = $"Server '{serverId}' not found" });
        }
        return Ok(server.AppPools);
    }

    [HttpGet("{serverId}/services")]
    public async Task<IActionResult> GetServices(string serverId)
    {
        var server = await _statusReader.GetServerAsync(serverId);
        if (server == null)
        {
            return NotFound(new { message = $"Server '{serverId}' not found" });
        }
        return Ok(server.WindowsServices);
    }
}
