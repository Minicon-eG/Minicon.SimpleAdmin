using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.IO;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// File-based exchange of BizTalk action requests between the central WebUI and the workers
/// running on the BizTalk servers.
/// <para>
/// Layout inside the actions directory:
/// <list type="bullet">
/// <item><c>requests/{serverId}/{requestId}.json</c> — written by the WebUI, read by the worker
/// whose server id matches <c>{serverId}</c>.</item>
/// <item><c>claims/{serverId}/{requestId}.json</c> — written exclusively by the worker that takes
/// the request over; the file is the proof that the request is being handled.</item>
/// <item><c>results/{serverId}/{requestId}.json</c> — written by the worker, read by the WebUI.</item>
/// </list>
/// A request that already has a claim is never executed a second time: the outcome of an
/// interrupted execution cannot be reconstructed, so the action is reported as unclear instead of
/// being repeated. The worker only ever touches requests addressed to its own server id, so
/// several workers can share one directory.
/// </para>
/// </summary>
public interface IBizTalkActionStore
{
    /// <summary>Writes a new action request (and returns the request with its assigned id).</summary>
    Task<BizTalkActionRequest> RequestAsync(BizTalkActionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Lists the requests addressed to one server, newest first, with their results.</summary>
    Task<List<BizTalkActionView>> ListAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>Returns one action view (request + result) or null when it does not exist.</summary>
    Task<BizTalkActionView?> GetAsync(string serverId, string requestId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the requests addressed to one server that have no result yet — the work queue of the
    /// worker on that server.
    /// </summary>
    Task<List<BizTalkActionRequest>> GetPendingAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>Writes the result of an executed action.</summary>
    Task SaveResultAsync(BizTalkActionResult result, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the exclusive claim on a request before it is executed. The claim file is created
    /// exclusively, so exactly one worker wins the race — also across processes and hosts sharing
    /// the directory. Returns null when the request is already claimed.
    /// </summary>
    Task<BizTalkActionClaim?> ClaimAsync(
        string serverId,
        string requestId,
        string workerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the claim of a request as uncertain: the action was carried out but the result could
    /// not be persisted. The request must not be executed again — somebody has to check the target.
    /// </summary>
    Task MarkClaimUncertainAsync(string serverId, string requestId, CancellationToken cancellationToken = default);

    /// <summary>Removes request and result files of actions that are older than <paramref name="maxAge"/>.</summary>
    Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class BizTalkActionStore : IBizTalkActionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _defaultActionsDirectory;
    private readonly ILogger<BizTalkActionStore> _logger;
    private volatile string _actionsDirectory;

    public BizTalkActionStore(string actionsDirectory, ILogger<BizTalkActionStore> logger)
    {
        _defaultActionsDirectory = actionsDirectory;
        _actionsDirectory = actionsDirectory;
        _logger = logger;

        EnsureDirectories();
    }

    /// <summary>
    /// The directory currently used for the request/result exchange. Defaults to the directory
    /// the store was created with and can be switched at runtime (the effective directory comes
    /// from the configuration, which is only known once it has been loaded).
    /// </summary>
    public string ActionsDirectory => _actionsDirectory;

    /// <summary>
    /// Switches the exchange directory and creates it if needed. Passing null or an empty value
    /// restores the default directory.
    /// </summary>
    public void SetActionsDirectory(string? actionsDirectory)
    {
        var effective = string.IsNullOrWhiteSpace(actionsDirectory) ? _defaultActionsDirectory : actionsDirectory;
        if (string.Equals(effective, _actionsDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        _actionsDirectory = effective;
        EnsureDirectories();
        _logger.LogInformation("BizTalk-Aktion: Austauschverzeichnis gesetzt auf '{Directory}'", effective);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(RequestsRoot);
        Directory.CreateDirectory(ClaimsRoot);
        Directory.CreateDirectory(ResultsRoot);
    }

    private string RequestsRoot => Path.Combine(_actionsDirectory, "requests");

    private string ClaimsRoot => Path.Combine(_actionsDirectory, "claims");

    private string ResultsRoot => Path.Combine(_actionsDirectory, "results");

    /// <inheritdoc />
    public async Task<BizTalkActionRequest> RequestAsync(BizTalkActionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ServerId))
            throw new ArgumentException("ServerId is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetName))
            throw new ArgumentException("TargetName is required.", nameof(request));

        var dir = ServerDir(RequestsRoot, request.ServerId);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, request.Id + ".json");
        await WriteJsonAtomicAsync(path, request, cancellationToken);

        _logger.LogInformation("BizTalk-Aktion angefordert: {Artifact} '{Target}' für Server '{Server}' (Id {Id})",
            request.Artifact, ForLog(request.TargetName), ForLog(request.ServerId), ForLog(request.Id));

        return request;
    }

    /// <inheritdoc />
    public async Task<List<BizTalkActionView>> ListAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var views = new List<BizTalkActionView>();

        foreach (var request in await ReadRequestsAsync(serverId, cancellationToken))
        {
            var result = await ReadResultAsync(serverId, request.Id, cancellationToken);
            var claim = await ReadClaimAsync(serverId, request.Id, cancellationToken);
            views.Add(new BizTalkActionView { Request = request, Result = result, Claim = claim });
        }

        return views.OrderByDescending(v => v.Request.RequestedAt).ToList();
    }

    /// <inheritdoc />
    public async Task<BizTalkActionView?> GetAsync(string serverId, string requestId, CancellationToken cancellationToken = default)
    {
        var requestPath = BuildFilePath(RequestsRoot, serverId, requestId);
        if (requestPath == null)
            return null;

        var request = await ReadJsonAsync<BizTalkActionRequest>(requestPath, cancellationToken);
        if (request == null)
            return null;

        var result = await ReadResultAsync(serverId, requestId, cancellationToken);
        var claim = await ReadClaimAsync(serverId, requestId, cancellationToken);
        return new BizTalkActionView { Request = request, Result = result, Claim = claim };
    }

    /// <inheritdoc />
    public async Task<List<BizTalkActionRequest>> GetPendingAsync(string serverId, CancellationToken cancellationToken = default)
    {
        var pending = new List<BizTalkActionRequest>();

        foreach (var request in await ReadRequestsAsync(serverId, cancellationToken))
        {
            // A request that already carries a claim is being handled (or was handled) by a
            // worker — it must never be handed out a second time.
            if (await ReadClaimAsync(serverId, request.Id, cancellationToken) != null)
                continue;

            var result = await ReadResultAsync(serverId, request.Id, cancellationToken);
            if (result == null)
                pending.Add(request);
        }

        return pending.OrderBy(r => r.RequestedAt).ToList();
    }

    /// <inheritdoc />
    public async Task SaveResultAsync(BizTalkActionResult result, CancellationToken cancellationToken = default)
    {
        var dir = ServerDir(ResultsRoot, result.ServerId);
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, result.RequestId + ".json");
        await WriteJsonAtomicAsync(path, result, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BizTalkActionClaim?> ClaimAsync(
        string serverId,
        string requestId,
        string workerId,
        CancellationToken cancellationToken = default)
    {
        var path = BuildFilePath(ClaimsRoot, serverId, requestId);
        if (path == null)
            return null;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var claim = new BizTalkActionClaim
        {
            RequestId = requestId,
            ServerId = serverId,
            WorkerId = workerId,
            ClaimedAt = DateTime.UtcNow
        };

        // The file is created exclusively: if it already exists, another worker owns the
        // request and this one must not execute it.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            // Already claimed by another worker.
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger.LogError(ex, "BizTalk-Aktion: Claim fuer Auftrag '{Id}' konnte nicht angelegt werden", requestId);
            return null;
        }

        try
        {
            await using (stream)
            {
                await JsonSerializer.SerializeAsync(stream, claim, JsonOptions, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A half-written claim would block the request forever — remove it.
            TryDeleteFile(path);
            _logger.LogError(ex, "BizTalk-Aktion: Claim fuer Auftrag '{Id}' konnte nicht geschrieben werden", requestId);
            return null;
        }

        _logger.LogInformation("BizTalk-Aktion: Auftrag '{Id}' von Worker '{Worker}' uebernommen", requestId, ForLog(workerId));
        return claim;
    }

    /// <inheritdoc />
    public async Task MarkClaimUncertainAsync(string serverId, string requestId, CancellationToken cancellationToken = default)
    {
        var path = BuildFilePath(ClaimsRoot, serverId, requestId);
        if (path == null)
            return;

        var claim = await ReadClaimAsync(serverId, requestId, cancellationToken);
        if (claim == null)
            return;

        claim.OutcomeUncertain = true;
        await WriteJsonAtomicAsync(path, claim, cancellationToken);

        _logger.LogWarning(
            "BizTalk-Aktion: Ergebnis fuer Auftrag '{Id}' ist unbestimmt — der Auftrag wird nicht wiederholt", requestId);
    }

    /// <inheritdoc />
    public async Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;

        foreach (var root in new[] { RequestsRoot, ClaimsRoot, ResultsRoot })
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var serverDir in Directory.EnumerateDirectories(root))
            {
                foreach (var file in Directory.EnumerateFiles(serverDir, "*.json"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) > cutoff)
                            continue;

                        File.Delete(file);
                        removed++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogDebug(ex, "BizTalk-Aktion: Datei '{File}' konnte nicht gelöscht werden", file);
                    }
                }
            }
        }

        if (removed > 0)
            _logger.LogInformation("BizTalk-Aktion: {Count} alte Auftrags-/Ergebnisdatei(en) entfernt", removed);

        return removed;
    }

    /// <summary>Deletes a file, ignoring the case that it is already gone.</summary>
    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "BizTalk-Aktion: Datei '{File}' konnte nicht entfernt werden", Path.GetFileName(path));
        }
    }

    // --- internals ---

    /// <summary>
    /// Directory of one server inside a root folder. The name carries a hash of the exact server
    /// id, so two different ids can never share a folder ("bt.prod" and "bt_prod" would both
    /// reduce to "bt_prod" otherwise) and the mapping stays stable across processes.
    /// </summary>
    private static string ServerDir(string root, string serverId)
        => Path.Combine(root, ServerDirName(serverId));

    /// <summary>
    /// The folder name of one server: the readable part plus a short hash of the exact id.
    /// Internal so the tests can assert the layout.
    /// </summary>
    internal static string ServerDirName(string serverId)
    {
        var readable = Sanitize(serverId);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(serverId ?? string.Empty)))[..8].ToLowerInvariant();
        return $"{readable}-{hash}";
    }

    /// <summary>
    /// Builds the path of a request/result file. The id comes from the URL and must therefore
    /// never influence the path beyond the file name: it is restricted to a safe character set
    /// and the resulting path is verified to stay inside the exchange directory.
    /// </summary>
    private static string? BuildFilePath(string root, string serverId, string id)
    {
        if (!IsValidId(id))
            return null;

        var dir = Path.GetFullPath(ServerDir(root, serverId));
        var full = Path.GetFullPath(Path.Combine(dir, id + ".json"));

        var rootFull = Path.GetFullPath(root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            return null;

        return full;
    }

    /// <summary>Reduces a server id to a safe directory name (path separators, dots and spaces).</summary>
    private static string Sanitize(string value)
    {
        var chars = value.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    /// <summary>
    /// Checks that an id may be used as a file name: only letters, digits, hyphen and underscore.
    /// Ids come from the URL and must never influence the path beyond the file name.
    /// </summary>
    private static bool IsValidId(string? value)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 64
            && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Prepares a value for a log entry: line breaks would allow forging log lines, so they are
    /// removed and the value is shortened.
    /// </summary>
    private static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var cleaned = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return cleaned.Length <= 120 ? cleaned : cleaned[..120];
    }

    private async Task<List<BizTalkActionRequest>> ReadRequestsAsync(string serverId, CancellationToken cancellationToken)
    {
        var requests = new List<BizTalkActionRequest>();
        var dir = ServerDir(RequestsRoot, serverId);

        if (!Directory.Exists(dir))
            return requests;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var request = await ReadJsonAsync<BizTalkActionRequest>(file, cancellationToken);
            if (request == null)
                continue;

            // The folder name is derived from the server id, but the file itself is authoritative:
            // a request that belongs to a different server must never be executed here.
            if (!string.Equals(request.ServerId, serverId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "BizTalk-Aktion: Auftrag im Ordner von Server '{Server}' gehoert zu Server '{Owner}' — wird uebersprungen",
                    ForLog(serverId), ForLog(request.ServerId));
                continue;
            }

            requests.Add(request);
        }

        return requests;
    }

    private async Task<BizTalkActionResult?> ReadResultAsync(string serverId, string requestId, CancellationToken cancellationToken)
    {
        var path = BuildFilePath(ResultsRoot, serverId, requestId);
        if (path == null)
            return null;

        return await ReadJsonAsync<BizTalkActionResult>(path, cancellationToken);
    }

    private async Task<BizTalkActionClaim?> ReadClaimAsync(string serverId, string requestId, CancellationToken cancellationToken)
    {
        var path = BuildFilePath(ClaimsRoot, serverId, requestId);
        if (path == null)
            return null;

        return await ReadJsonAsync<BizTalkActionClaim>(path, cancellationToken);
    }

    private async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Only the file name goes into the log — the full path would carry user input.
            _logger.LogWarning(ex, "BizTalk-Aktion: Datei '{File}' konnte nicht gelesen werden",
                ForLog(Path.GetFileName(path)));
            return null;
        }
    }

    /// <summary>Writes JSON via a temp file + rename, so readers never see a half-written file.</summary>
    private async Task WriteJsonAtomicAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }
}
