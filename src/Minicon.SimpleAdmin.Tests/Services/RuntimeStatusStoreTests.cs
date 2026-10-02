using FluentAssertions;
using Minicon.SimpleAdmin.Services;
using Minicon.SimpleAdmin.Models.Status;
using Microsoft.Extensions.Logging;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class RuntimeStatusStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "simpleadmin-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<ILogger<RuntimeStatusStore>> _logger = new();

    [Fact]
    public async Task LoadAllStatusesAsync_IgnoresJsonFilesWithoutServerAndService()
    {
        var store = new RuntimeStatusStore(_tempDirectory, _logger.Object);
        var validStatus = new RuntimeStatus
        {
            Server = "server-a",
            Service = "api",
            LastUpdate = DateTime.UtcNow,
            Status = ServiceStatus.Healthy
        };

        await store.SaveStatusAsync(validStatus, 10);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "acknowledges.json"), "{}");

        var statuses = await store.LoadAllStatusesAsync();

        statuses.Should().ContainSingle();
        statuses[0].Server.Should().Be("server-a");
        statuses[0].Service.Should().Be("api");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
