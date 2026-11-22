using System.Net;
using Jellyfin.Plugin.Wizarr.Configuration;

namespace Jellyfin.Plugin.Wizarr.Api;

/// <summary>
/// The single HTTP client Jellyfin uses to talk to Wizarr on the private network.
/// </summary>
internal static class WizarrConnection
{
    /// <summary>
    /// Redirects and cookies are passed back to the browser untouched rather than handled here,
    /// and responses are fetched uncompressed so links in them can be rewritten.
    /// </summary>
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    internal static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    internal static Uri? BaseUri()
    {
        var url = Config.WizarrInternalUrl?.Trim();
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    internal static string? ApiKey()
    {
        var cfg = Config;
        return string.IsNullOrWhiteSpace(cfg.ApiKey)
            ? Environment.GetEnvironmentVariable("WIZARR_API_KEY")
            : cfg.ApiKey;
    }
}
