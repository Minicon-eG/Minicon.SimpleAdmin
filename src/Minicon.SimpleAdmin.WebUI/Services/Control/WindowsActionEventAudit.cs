using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;
using Minicon.SimpleAdmin.WebUI.Services.Backup;

namespace Minicon.SimpleAdmin.WebUI.Services.Control;

public interface IActionEventAudit { void TryWrite(BizTalkAuditEntry entry); }
public interface IWindowsEventSink
{
    bool IsWindows { get; }
    bool IsApplicationSource(string source);
    void Write(string source, int eventId, string message);
}

internal sealed class WindowsEventSink : IWindowsEventSink
{
    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsApplicationSource(string source) => OperatingSystem.IsWindows() && Check(source);
    [SupportedOSPlatform("windows")]
    private static bool Check(string source) => EventLog.SourceExists(source) &&
        string.Equals(EventLog.LogNameFromSourceName(source, "."), "Application", StringComparison.OrdinalIgnoreCase);
    public void Write(string source, int eventId, string message)
    {
        if (OperatingSystem.IsWindows()) EventLog.WriteEntry(source, message,
            eventId is 3100 or 3200 ? EventLogEntryType.Information :
            eventId is 3101 or 3201 ? EventLogEntryType.Warning : EventLogEntryType.Error, eventId);
    }
}

internal sealed class WindowsActionEventAudit(IWindowsEventSink sink, ILogger<WindowsActionEventAudit> logger, int capacity = 128)
    : BackgroundService, IActionEventAudit, ISettingsBackupEventAudit
{
    private readonly Channel<object> queue = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int platform;
    private static readonly JsonSerializerOptions EventJson = new() { Converters = { new JsonStringEnumConverter() } };
    private int warned;
    private volatile bool degraded;

    public void TryWrite(BizTalkAuditEntry entry) => Enqueue(entry);
    public void TryWrite(SettingsBackupAuditEntry entry) => Enqueue(entry);

    private void Enqueue(object entry)
    {
        try
        {
            if (degraded || Volatile.Read(ref platform) == 2) return;
            if (!queue.Writer.TryWrite(entry))
            {
                degraded = true;
                if (Volatile.Read(ref platform) == 1) Warn();
            }
        }
        catch { degraded = true; if (Volatile.Read(ref platform) == 1) Warn(); }
    }

    private void Warn()
    {
        if (Interlocked.Exchange(ref warned, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { logger.LogWarning("Windows Application event audit unavailable or buffer full; file audit remains authoritative. Check sources/rights and restart after repair."); }
            catch { }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Run(() => PumpAsync(stoppingToken), CancellationToken.None).WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { degraded = true; Warn(); }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var isWindows = sink.IsWindows;
        Volatile.Write(ref platform, isWindows ? 1 : 2);
        if (!isWindows) return;
        if (degraded) { Warn(); return; }
        var sources = new List<string>();
        foreach (var name in new[] { "Minicon.SimpleAdmin.WebUI", "SimpleAdmin" })
        {
            try { if (sink.IsApplicationSource(name)) sources.Add(name); } catch { }
        }
        if (sources.Count == 0) { degraded = true; Warn(); return; }
        await foreach (var item in queue.Reader.ReadAllAsync(ct))
        {
            var (eventId, message) = Serialize(item);
            var written = false;
            foreach (var source in sources.ToArray())
            {
                try { sink.Write(source, eventId, message); written = true; break; }
                catch { sources.Remove(source); }
            }
            if (!written) { degraded = true; Warn(); return; }
        }
    }

    private static (int EventId, string Message) Serialize(object item) => item switch
    {
        BizTalkAuditEntry entry => (EventId(entry.Outcome),
            JsonSerializer.Serialize(entry with { Application = entry.Application ?? "unbekannt" }, EventJson)),
        SettingsBackupAuditEntry entry => (entry.Outcome switch
        {
            "Succeeded" => 3200,
            "Rejected" => 3201,
            _ => 3202
        }, JsonSerializer.Serialize(entry, EventJson)),
        _ => throw new InvalidOperationException("Unknown event audit entry")
    };

    internal static int EventId(BizTalkActionOutcome outcome) => outcome switch
        { BizTalkActionOutcome.Accepted => 3100, BizTalkActionOutcome.Failed => 3102, _ => 3101 };
}
