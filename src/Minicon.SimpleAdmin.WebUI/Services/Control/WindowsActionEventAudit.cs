using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;

namespace Minicon.SimpleAdmin.WebUI.Services.Control;

public interface IActionEventAudit { void TryWrite(BizTalkAuditEntry entry); }
public interface IWindowsEventSink
{
    bool IsWindows { get; }
    bool IsApplicationSource(string source);
    void Write(string source, int eventId, string message);
}
// EventLog is supplied by our explicit FrameworkReference Microsoft.AspNetCore.App (reference pack).
// It does not require an additional/transitive NuGet PackageReference.
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
            eventId == 3100 ? EventLogEntryType.Information : eventId == 3101 ? EventLogEntryType.Warning : EventLogEntryType.Error, eventId);
    }
}

/// <summary>One bounded, nonblocking queue. All Windows calls (including source discovery) run off the request path.</summary>
internal sealed class WindowsActionEventAudit(IWindowsEventSink sink, ILogger<WindowsActionEventAudit> logger, int capacity = 128)
    : BackgroundService, IActionEventAudit
{
    private readonly Channel<BizTalkAuditEntry> queue = Channel.CreateBounded<BizTalkAuditEntry>(new BoundedChannelOptions(capacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    // Wait + TryWrite deliberately detects overflow (false) without waiting. DropWrite would report
    // success on a dropped item and hide the outage warning. No WriteAsync is used.
    private int platform; // 0: initialization pending, 1: Windows, 2: non-Windows
    private static readonly JsonSerializerOptions EventJson = new() { Converters = { new JsonStringEnumConverter() } };
    private int warned;
    private volatile bool degraded;
    public void TryWrite(BizTalkAuditEntry entry)
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
        // A host may attach a blocking provider to ILogger too. Schedule at most one warning off HTTP.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { logger.LogWarning("BizTalk Application event audit unavailable or buffer full; file audit remains authoritative. Check sources/rights and restart after repair."); }
            catch { /* Never recurse through a failed logger. */ }
        });
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // WaitAsync allows shutdown even if a Windows API hangs; at most one bounded worker remains blocked.
        try { await Task.Run(() => PumpAsync(stoppingToken), CancellationToken.None).WaitAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch { degraded = true; Warn(); }
    }
    private async Task PumpAsync(CancellationToken ct)
    {
        // Platform seam is evaluated once, here on the background worker, never from TryWrite/HTTP.
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
        await foreach (var entry in queue.Reader.ReadAllAsync(ct))
        {
            var written = false;
            foreach (var source in sources.ToArray())
            {
                try { sink.Write(source, EventId(entry.Outcome), JsonSerializer.Serialize(entry with { Application = entry.Application ?? "unbekannt" }, EventJson)); written = true; break; }
                catch { sources.Remove(source); }
            }
            if (!written) { degraded = true; Warn(); return; }
        }
    }
    internal static int EventId(BizTalkActionOutcome outcome) => outcome switch
        { BizTalkActionOutcome.Accepted => 3100, BizTalkActionOutcome.Failed => 3102, _ => 3101 };
}
