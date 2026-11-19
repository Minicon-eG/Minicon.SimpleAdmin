using System.Net;
using System.Text.Json;
using Minicon.SimpleAdmin.WebUI.Models.BizTalk;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

/// <summary>P3a deliberately exposes reading only. No action or audit contract is enabled.</summary>
public interface IBizTalkInstanceProvider
{
    Task<BizTalkInstancePage> QuerySuspendedAsync(BizTalkTarget target, BizTalkInstanceQuery query, CancellationToken ct);
}

public sealed class BizTalkProviderException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class RestBizTalkInstanceProvider(HttpClient http, BizTalkOptions options, TimeProvider clock) : IBizTalkInstanceProvider
{
    public const string ClientName = "BizTalkInstances";
    public static HttpClientHandler CreateHandler() => new()
    {
        UseDefaultCredentials = true,
        AllowAutoRedirect = false,
        UseCookies = false
    };

    public async Task<BizTalkInstancePage> QuerySuspendedAsync(BizTalkTarget target, BizTalkInstanceQuery query, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.QueryTimeout(target.TimeoutSeconds));
        var items = new List<BizTalkInstance>();
        var scanned = 0;
        var invalid = 0;
        var truncated = false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(target.BaseUri, "OperationalData/Instances"));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new BizTalkProviderException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"Zugriff verweigert (HTTP {(int)response.StatusCode}). Rechte des WebUI-Dienstkontos prüfen."
                    : $"Management-API antwortet mit HTTP {(int)response.StatusCode}. Redirects werden nicht verfolgt.");
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var limited = new ByteLimitedStream(source, options.EffectiveMaxResponseBytes);
            try
            {
                await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(limited,
                    new JsonSerializerOptions { DefaultBufferSize = 1024, MaxDepth = 32 }, deadline.Token))
                {
                    if (scanned == options.EffectiveMaxInstances) { truncated = true; break; }
                    scanned++;
                    if (element.ValueKind != JsonValueKind.Object)
                        throw new JsonException("Instanzeintrag ist kein Objekt.");
                    if (!Guid.TryParse(BizTalkInstanceMapper.Value(element, "Id"), out var id) || id == Guid.Empty)
                        invalid++;
                    var item = BizTalkInstanceMapper.Map(element);
                    if (item != null) items.Add(item);
                }
            }
            catch (ResponseLimitException) { truncated = true; }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Zeitgrenze der Live-Abfrage überschritten.");
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException)
        {
            throw new BizTalkProviderException("Die Instanzantwort konnte nicht vollständig gelesen werden.", ex);
        }

        var filtered = items.Where(i =>
                (query.Application == null || string.Equals(i.Application, query.Application, StringComparison.Ordinal)) &&
                (!query.MissingApplication || i.ApplicationMapping == BizTalkApplicationMapping.Unknown) &&
                (string.IsNullOrEmpty(query.Error) ||
                 (i.ErrorCode?.Contains(query.Error, StringComparison.OrdinalIgnoreCase) ?? false) ||
                 (i.ErrorText?.Contains(query.Error, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderBy(i => i.SuspendedSince).ThenBy(i => i.InstanceId).ToList();
        var warnings = new List<string>();
        if (truncated) warnings.Add("Ergebnis begrenzt; Filter gelten nur für den gelesenen Ausschnitt.");
        if (invalid > 0) warnings.Add($"{invalid} Einträge ohne gültige Instanz-ID wurden ausgelassen.");
        if (items.Any(i => i.ErrorTextTruncated))
            warnings.Add("Fehlertexte sind auf 32 KiB gekürzt; die Suche berücksichtigt nur den angezeigten Text.");
        return new(target.ServerId, "REST", clock.GetUtcNow(), filtered.Skip(query.Offset).Take(query.PageSize).ToArray(),
            query.Offset, query.PageSize, (long)query.Offset + query.PageSize < filtered.Count, truncated, scanned,
            filtered.Count, warnings.Count == 0 ? null : string.Join(" ", warnings));
    }

    private sealed class ResponseLimitException : IOException;

    /// <summary>Caps transport bytes, including giant individual JSON values. One byte of lookahead detects overflow.</summary>
    private sealed class ByteLimitedStream(Stream source, long remaining) : Stream
    {
        private bool _arrayStarted;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            if (remaining == 0)
            {
                if (await source.ReadAsync(buffer[..1], cancellationToken) != 0) throw new ResponseLimitException();
                return 0;
            }
            var count = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, remaining)], cancellationToken);
            remaining -= count;
            if (!_arrayStarted)
            {
                foreach (var value in buffer.Span[..count])
                {
                    if (value is 0x20 or 0x09 or 0x0A or 0x0D) continue;
                    if (value != (byte)'[') throw new JsonException("Die Instanzantwort muss ein JSON-Array sein.");
                    _arrayStarted = true;
                    break;
                }
            }
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
