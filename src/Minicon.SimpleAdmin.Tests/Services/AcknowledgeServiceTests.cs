using FluentAssertions;
using Minicon.SimpleAdmin.Models.State;
using Minicon.SimpleAdmin.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class AcknowledgeServiceTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<AcknowledgeService>> _logger = new();

    [Fact]
    public async Task CreateAcknowledgeAsync_WritesToMetadataDirectory()
    {
        var statusDirectory = Path.Combine(_tempDirectory, "status");
        var service = new AcknowledgeService(statusDirectory, _logger.Object);

        await service.CreateAcknowledgeAsync("server-a", "cpu_usage_critical", "tester", "Investigating", 30);

        File.Exists(Path.Combine(statusDirectory, "metadata", "acknowledges.json")).Should().BeTrue();
        File.Exists(Path.Combine(statusDirectory, "acknowledges.json")).Should().BeFalse();
    }

    [Fact]
    public async Task LoadAcknowledgesAsync_ReloadsWhenFileChangesOnDisk()
    {
        var statusDirectory = Path.Combine(_tempDirectory, "status");
        var firstService = new AcknowledgeService(statusDirectory, _logger.Object);
        var secondService = new AcknowledgeService(statusDirectory, _logger.Object);

        (await firstService.LoadAcknowledgesAsync()).Acknowledges.Should().BeEmpty();

        await secondService.CreateAcknowledgeAsync("server-a", "cpu_usage_critical", "tester", "Investigating", 30);

        var reloaded = await firstService.LoadAcknowledgesAsync();

        reloaded.Acknowledges.Should().ContainSingle();
        reloaded.Acknowledges[0].ServerId.Should().Be("server-a");
        reloaded.Acknowledges[0].ProblemId.Should().Be("cpu_usage_critical");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
