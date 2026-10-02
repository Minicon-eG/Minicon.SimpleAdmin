using FluentAssertions;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Checkers;
using Minicon.SimpleAdmin.Models.State;
using Moq;

namespace Minicon.SimpleAdmin.Tests.Services;

public class NotificationStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"simpleadmin_notifstate_{Guid.NewGuid():N}");
    private readonly NotificationStateStore _store = new(new Mock<ILogger<NotificationStateStore>>().Object);

    public NotificationStateStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsEmptyState()
    {
        var state = await _store.LoadAsync(Path.Combine(_dir, "nope.json"));

        state.Should().NotBeNull();
        state.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEntries()
    {
        var path = Path.Combine(_dir, "notify-state.json");
        var state = new NotifyState { GeneratedBy = "NOTIFIER-A" };
        state.Entries["server_x_stale"] = new NotifyEntry
        {
            ServerId = "x",
            Severity = "Critical",
            LastAttemptAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LastNotifiedAt = new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc),
            NotifyCount = 2
        };

        await _store.SaveAsync(path, state);
        var loaded = await _store.LoadAsync(path);

        loaded.GeneratedBy.Should().Be("NOTIFIER-A");
        loaded.Entries.Should().ContainKey("server_x_stale");
        loaded.Entries["server_x_stale"].ServerId.Should().Be("x");
        loaded.Entries["server_x_stale"].NotifyCount.Should().Be(2);
        loaded.Entries["server_x_stale"].LastNotifiedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Save_WritesAtomicallyWithoutLeavingTempFile()
    {
        var path = Path.Combine(_dir, "notify-state.json");

        await _store.SaveAsync(path, new NotifyState());

        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".tmp").Should().BeFalse();
    }
}
