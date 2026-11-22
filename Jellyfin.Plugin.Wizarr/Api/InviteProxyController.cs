using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wizarr.Api;

/// <summary>
/// Serves Wizarr's invite sign-up pages at https://your-jellyfin/invite so Wizarr itself never
/// has to be reachable from the internet. Only the public sign-up routes are passed through;
/// Wizarr's admin panel, login, setup and API stay unreachable from here.
/// </summary>
[Route("invite")]
[AllowAnonymous]
public class InviteProxyController : ControllerBase
{
    private const string Segment = "/invite";

    // Browser headers that are never forwarded to Wizarr (hop-by-hop, Jellyfin credentials, spoofable proxy headers).
    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Accept-Encoding", "Content-Length", "Content-Type",
        "Authorization", "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token",
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-Forwarded-Prefix", "X-Forwarded-Port", "X-Real-IP"
    };

    // Wizarr response headers that are dropped or rewritten before reaching the browser.
    private static readonly HashSet<string> SkipResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Trailer", "Proxy-Authenticate",
        "Content-Length", "Server", "Set-Cookie", "Location"
    };

    private readonly ILogger<InviteProxyController> _logger;

    public InviteProxyController(ILogger<InviteProxyController> logger)
    {
        _logger = logger;
    }

    [AcceptVerbs("GET", "HEAD", "POST", Route = "{**path}")]
    public async Task Proxy(CancellationToken cancellationToken)
    {
        var encodedPath = Request.Path.ToUriComponent();      // e.g. /invite/j/ABC123 (relative to Jellyfin's base URL)
        var pathBase = Request.PathBase.ToUriComponent().TrimEnd('/');
        var prefix = pathBase + Segment;                      // e.g. /invite or /jellyfin/invite
        var rest = encodedPath.Length > Segment.Length ? encodedPath[Segment.Length..] : string.Empty;

        // Cookies are scoped to the lowercase path, so normalise /Invite → /invite.
        if (!encodedPath.StartsWith(Segment, StringComparison.Ordinal))
        {
            Response.Redirect(prefix + rest + Request.QueryString.Value);
            return;
        }

        // Bare /invite shows Wizarr's "enter your invite code" page.
        if (rest.Length == 0 || rest == "/")
        {
            rest = "/join";
        }

        if (!IsPublicWizarrPath(rest))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var baseUri = WizarrConnection.BaseUri();
        if (baseUri is null)
        {
            await WriteText(StatusCodes.Status503ServiceUnavailable, "Invites aren't set up on this server yet.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var upstream = new Uri(baseUri, rest.TrimStart('/') + Request.QueryString.Value);
        using var request = BuildUpstreamRequest(upstream, prefix);

        HttpResponseMessage response;
        try
        {
            response = await WizarrConnection.Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Could not reach Wizarr at {Url}", baseUri);
            await WriteText(StatusCodes.Status502BadGateway, "The invite service is unavailable right now. Please try again later.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            await CopyResponse(response, prefix, baseUri, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The invitee-facing parts of Wizarr. Everything else (admin, login, setup, settings, api, webauthn…) is refused.
    /// </summary>
    private static bool IsPublicWizarrPath(string rest)
    {
        var p = rest.TrimStart('/');
        var first = p.Split('/', 2)[0].ToLowerInvariant();
        return first switch
        {
            "j" or "join" or "wizard" or "static" or "reset" or "favicon.ico" or "cinema-posters" or "image-proxy" => true,
            "invitation" => p.Equals("invitation/process", StringComparison.OrdinalIgnoreCase),
            "plex" => p.Equals("plex/callback", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private HttpRequestMessage BuildUpstreamRequest(Uri upstream, string prefix)
    {
        var request = new HttpRequestMessage(new HttpMethod(Request.Method), upstream);

        foreach (var header in Request.Headers)
        {
            if (header.Key.StartsWith(':') || SkipRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        var hasBody = Request.ContentLength > 0
                      || (Request.ContentLength is null && HttpMethods.IsPost(Request.Method));
        if (hasBody)
        {
            request.Content = new StreamContent(Request.Body);
            if (!string.IsNullOrEmpty(Request.ContentType))
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", Request.ContentType);
            }

            if (Request.ContentLength is long length)
            {
                request.Content.Headers.ContentLength = length;
            }
        }

        // Wizarr (with TRUST_PROXY_HEADERS=true) uses these to build links under /invite on the Jellyfin address
        // and to rate-limit by the real visitor IP instead of Jellyfin's.
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrEmpty(clientIp))
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
        }

        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", Request.Scheme);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", Request.Host.Value);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix);

        return request;
    }

    private async Task CopyResponse(HttpResponseMessage response, string prefix, Uri baseUri, CancellationToken ct)
    {
        Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!SkipResponseHeaders.Contains(header.Key))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        if (response.Headers.TryGetValues("Location", out var locations))
        {
            Response.Headers.Location = RewriteLocation(locations.First(), prefix, baseUri);
        }

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            Response.Headers.SetCookie = cookies.Select(c => RewriteCookie(c, prefix)).ToArray();
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (IsRewritable(mediaType))
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(RewriteBody(text, prefix));
            Response.ContentType = mediaType + "; charset=utf-8";
            Response.ContentLength = bytes.Length;
            await Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        else
        {
            if (response.Content.Headers.ContentLength is long length)
            {
                Response.ContentLength = length;
            }

            await response.Content.CopyToAsync(Response.Body, ct).ConfigureAwait(false);
        }
    }

    private static bool IsRewritable(string mediaType) => mediaType is
        "text/html" or "text/css" or "application/javascript" or "text/javascript";

    /// <summary>
    /// Safety net for links Wizarr hard-codes from the site root (normally Wizarr already adds /invite itself
    /// thanks to X-Forwarded-Prefix). Anything already under the prefix is left alone.
    /// </summary>
    internal static string RewriteBody(string text, string prefix)
    {
        var p = Regex.Escape(prefix.TrimStart('/'));
        var notPrefixed = $"(?!/|{p}(?:[/\"'?#`)]|$))";
        const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // href="/x", src="/x", action="/x", hx-get="/x" ...
        text = Regex.Replace(
            text,
            $"(\\b(?:href|src|action|formaction|poster|hx-get|hx-post|hx-put|hx-patch|hx-delete|hx-push-url|hx-replace-url)\\s*=\\s*[\"'])/{notPrefixed}",
            "$1" + prefix + "/",
            Opts);

        // fetch('/x'), location.href = '/x'
        text = Regex.Replace(
            text,
            $"(\\b(?:fetch\\(\\s*|location(?:\\.href)?\\s*=\\s*)[\"'`])/{notPrefixed}",
            "$1" + prefix + "/",
            Opts);

        // "/static/..." in scripts and url(/static/...) in CSS
        text = Regex.Replace(text, "([\"'`(])/static/", "$1" + prefix + "/static/", Opts);

        return text;
    }

    internal static string RewriteLocation(string location, string prefix, Uri baseUri)
    {
        if (Uri.TryCreate(location, UriKind.Absolute, out var abs)
            && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            // Never send a browser to the private Wizarr address.
            if (!string.Equals(abs.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return location;
            }

            location = abs.PathAndQuery + abs.Fragment;
        }

        if (location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
            && !(location == prefix
                 || location.StartsWith(prefix + "/", StringComparison.Ordinal)
                 || location.StartsWith(prefix + "?", StringComparison.Ordinal)))
        {
            location = prefix + location;
        }

        return location;
    }

    internal static string RewriteCookie(string cookie, string prefix)
    {
        const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        cookie = Regex.Replace(cookie, @";\s*Domain=[^;]*", string.Empty, Opts);

        if (!Regex.IsMatch(cookie, @";\s*Path=", Opts))
        {
            return cookie + "; Path=" + prefix;
        }

        return Regex.Replace(cookie, @"(;\s*Path=)([^;]*)", m =>
        {
            var path = m.Groups[2].Value.Trim();
            if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return m.Value;
            }

            return m.Groups[1].Value + (path is "" or "/" ? prefix : prefix + path);
        }, Opts);
    }

    private async Task WriteText(int status, string message, CancellationToken ct)
    {
        Response.StatusCode = status;
        Response.ContentType = "text/plain; charset=utf-8";
        await Response.WriteAsync(message, ct).ConfigureAwait(false);
    }
}
