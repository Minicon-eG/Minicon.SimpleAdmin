using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Checkers;

/// <summary>
/// Loads and persists this notifier instance's <see cref="NotifyState"/> (notify-state.json).
/// Writes atomically (temp file + move) so a peer reading the file over HTTP never sees a
/// half-written document.
/// </summary>
public sealed class NotificationStateStore(ILogger<NotificationStateStore> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<NotifyState> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                var state = JsonSerializer.Deserialize<NotifyState>(json, JsonOptions);
                if (state != null)
                    return state;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notifier: failed to read notify-state from '{Path}', starting fresh", path);
        }

        return new NotifyState();
    }

    public async Task SaveAsync(string path, NotifyState state, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, cancellationToken);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Notifier: failed to write notify-state to '{Path}'", path);
        }
    }
}
