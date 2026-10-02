using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using System.IO.Abstractions.TestingHelpers;
using Minicon.SimpleAdmin.Services;

namespace Minicon.SimpleAdmin.Tests.Services;

/// <summary>
/// Unit tests for ServiceConfigReader using MockFileSystem
/// </summary>
public class ServiceConfigReaderTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly Mock<ILogger<ServiceConfigReader>> _logger;
    private const string ConfigPath = @"C:\config\config.json";

    public ServiceConfigReaderTests()
    {
        _fileSystem = new MockFileSystem();
        _logger = new Mock<ILogger<ServiceConfigReader>>();
    }

    private ServiceConfigReader CreateReader() =>
        new ServiceConfigReader(ConfigPath, _logger.Object, _fileSystem);

    private static string MinimalValidConfig() => """
        {
          "servers": [
            {
              "name": "srv01",
              "serviceTypes": [
                { "type": "transfer" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task LoadConfigAsync_ReturnsConfig_WhenFileValid()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData(MinimalValidConfig()));
        var reader = CreateReader();

        var config = await reader.LoadConfigAsync();

        config.Should().NotBeNull();
        config.Servers.Should().HaveCount(1);
        config.Servers[0].Name.Should().Be("srv01");
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsFileNotFoundException_WhenFileMissing()
    {
        var reader = CreateReader();

        var act = async () => await reader.LoadConfigAsync();

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsInvalidOperationException_WhenJsonInvalid()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData("not valid json {{{"));
        var reader = CreateReader();

        var act = async () => await reader.LoadConfigAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsInvalidOperationException_WhenNoServers()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData("""{ "servers": [] }"""));
        var reader = CreateReader();

        var act = async () => await reader.LoadConfigAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one server*");
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsInvalidOperationException_WhenServerNameEmpty()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData("""
            {
              "servers": [
                { "name": "", "serviceTypes": [{ "type": "transfer" }] }
              ]
            }
            """));
        var reader = CreateReader();

        var act = async () => await reader.LoadConfigAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Server name*");
    }

    [Fact]
    public async Task LoadConfigAsync_ThrowsInvalidOperationException_WhenMaxEntriesExceedsLimit()
    {
        _fileSystem.AddFile(ConfigPath, new MockFileData("""
            {
              "servers": [
                { "name": "srv01", "serviceTypes": [{ "type": "transfer" }] }
              ],
              "history": { "maxEntries": 99999 }
            }
            """));
        var reader = CreateReader();

        var act = async () => await reader.LoadConfigAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*10000*");
    }
}
