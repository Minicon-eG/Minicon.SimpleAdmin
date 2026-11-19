using Minicon.SimpleAdmin.Models.Config;

namespace Minicon.SimpleAdmin.WebUI.Services.BizTalk;

public sealed record BizTalkTarget(string ServerId, string ServerName, Uri BaseUri, int? TimeoutSeconds);

/// <summary>Pure config validation. No DNS lookup and no target from worker status.</summary>
public static class BizTalkTargets
{
    public static Server? ResolveServer(Config config, string? reference) =>
        config.GetEffectiveFeatures().BizTalk.Enabled && !string.IsNullOrWhiteSpace(reference)
            ? config.Servers.FirstOrDefault(s => s.Active && s.Checks?.BizTalk?.Enabled == true &&
                (string.Equals(s.GetEffectiveId(), reference.Trim(), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.Name, reference.Trim(), StringComparison.OrdinalIgnoreCase)))
            : null;

    public static BizTalkTarget? Resolve(Config config, string? reference, BizTalkOptions options)
    {
        var server = ResolveServer(config, reference);
        if (server == null || Uri.CheckHostName(server.Name) == UriHostNameType.Unknown ||
            !Uri.TryCreate(server.Checks!.BizTalk!.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;

        if (uri.IsLoopback && !IsLocal(server.Name))
            uri = new UriBuilder(uri) { Host = server.Name }.Uri;

        var sameHost = string.Equals(uri.Host, server.Name, StringComparison.OrdinalIgnoreCase)
                       || (uri.IsLoopback && IsLocal(server.Name));
        var approved = options.AllowedApiHosts.Any(pair =>
            string.Equals(pair.Key, server.GetEffectiveId(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pair.Value, uri.Host, StringComparison.OrdinalIgnoreCase));
        if (!sameHost && !approved) return null;
        return new(server.GetEffectiveId(), server.Name,
            new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"), server.Checks.BizTalk.TimeoutSeconds);
    }

    private static bool IsLocal(string name) =>
        name.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name is "127.0.0.1" or "::1";
}
