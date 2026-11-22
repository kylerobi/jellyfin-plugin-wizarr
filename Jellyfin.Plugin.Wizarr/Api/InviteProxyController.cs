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
public partial class InviteProxyController : ControllerBase
{
    private const string Segment = "/invite";

    /// <summary>
    /// Wizarr's cookies are renamed with this prefix on the way out, and only cookies carrying it are sent back to
    /// Wizarr (with the prefix removed). Cookies other apps set on the Jellyfin address never reach Wizarr.
    /// </summary>
    internal const string CookiePrefix = "wizarr-";

    // Browser headers that are never forwarded to Wizarr (hop-by-hop, Jellyfin credentials, spoofable proxy headers,
    // and Cookie, which is filtered separately).
    private static readonly HashSet<string> SkipRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Accept-Encoding", "Content-Length", "Content-Type", "Cookie",
        "Authorization", "X-Emby-Authorization", "X-Emby-Token", "X-MediaBrowser-Token",
        "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-Forwarded-Prefix", "X-Forwarded-Port", "X-Real-IP"
    };

    // The only Wizarr response headers passed to the browser. Anything else (HSTS, Clear-Site-Data, CORS, ...) is
    // dropped so Wizarr can't change how the browser treats the whole Jellyfin address.
    // Location, Set-Cookie and the htmx URL headers are handled separately.
    private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Encoding", "Content-Language", "Content-Disposition", "Content-Range",
        "Accept-Ranges", "Cache-Control", "Expires", "Pragma", "Last-Modified", "ETag", "Vary", "Retry-After",
        "Content-Security-Policy", "X-Frame-Options", "X-Content-Type-Options", "Referrer-Policy",
        "Permissions-Policy", "Cross-Origin-Opener-Policy", "Cross-Origin-Resource-Policy",
        "HX-Trigger", "HX-Trigger-After-Settle", "HX-Trigger-After-Swap", "HX-Refresh", "HX-Reswap", "HX-Retarget",
        "HX-Reselect"
    };

    // htmx response headers that carry a URL and get the same rewriting as Location.
    private static readonly string[] HtmxUrlHeaders = { "HX-Redirect", "HX-Location", "HX-Push-Url", "HX-Replace-Url" };

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

        // With a dedicated invite address configured, refuse to serve Wizarr on any other host, so its pages can
        // never run on the origin where the Jellyfin web app keeps its logins.
        var invitePublic = WizarrConnection.PublicUri(WizarrConnection.Config.InvitePublicUrl);
        if (invitePublic is not null
            && !string.Equals(Request.Host.Host, invitePublic.Host, StringComparison.OrdinalIgnoreCase))
        {
            await WriteText(
                StatusCodes.Status404NotFound,
                "Invites are served at " + invitePublic.GetLeftPart(UriPartial.Authority) + prefix,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsSafePath(rest))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

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
        using var request = BuildUpstreamRequest(upstream, prefix, invitePublic);

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
    /// Only plain path characters are allowed: no percent-encoding (so no encoded slashes, backslashes or dots that
    /// Wizarr would decode differently from this check), no "." or ".." segments and no empty segments.
    /// </summary>
    internal static bool IsSafePath(string rest)
    {
        if (!SafePathChars().IsMatch(rest))
        {
            return false;
        }

        var segments = rest.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s is "." or "..")
            {
                return false;
            }

            // Empty segments only at the very start ("/x") or end ("/x/"), never "//".
            if (s.Length == 0 && i != 0 && i != segments.Length - 1)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The invitee-facing parts of Wizarr. Everything else (admin, login, setup, settings, api, webauthn…) is refused.
    /// </summary>
    internal static bool IsPublicWizarrPath(string rest)
    {
        var p = rest.TrimStart('/');
        var parts = p.Split('/', 3);
        var first = parts[0].ToLowerInvariant();
        return first switch
        {
            // /wizard/bundle-preview is Wizarr's admin preview of custom wizard bundles.
            "wizard" => !(parts.Length > 1 && parts[1].Equals("bundle-preview", StringComparison.OrdinalIgnoreCase)),
            "j" or "join" or "static" or "reset" or "favicon.ico" or "cinema-posters" or "image-proxy" => true,
            "invitation" => p.Equals("invitation/process", StringComparison.OrdinalIgnoreCase),
            "plex" => p.Equals("plex/callback", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private HttpRequestMessage BuildUpstreamRequest(Uri upstream, string prefix, Uri? invitePublic)
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

        var cookie = FilterRequestCookies(Request.Headers.Cookie.ToArray());
        if (cookie.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
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

        // Prefer the configured public address over the visitor's Host header, so a forged Host can't make
        // Wizarr build links that point somewhere else.
        var publicUri = invitePublic ?? WizarrConnection.PublicUri(WizarrConnection.Config.JellyfinPublicUrl);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", publicUri?.Scheme ?? Request.Scheme);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", publicUri?.Authority ?? Request.Host.Value);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", prefix);

        return request;
    }

    private async Task CopyResponse(HttpResponseMessage response, string prefix, Uri baseUri, CancellationToken ct)
    {
        Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (AllowedResponseHeaders.Contains(header.Key))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        if (response.Headers.TryGetValues("Location", out var locations))
        {
            Response.Headers.Location = RewriteLocation(locations.First(), prefix, baseUri);
        }

        foreach (var name in HtmxUrlHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = values.First();
                Response.Headers[name] = value.StartsWith('/') || value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? RewriteLocation(value, prefix, baseUri)
                    : value;  // "false", or HX-Location's JSON form
            }
        }

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            Response.Headers.SetCookie = cookies.Select(c => RewriteCookie(c, prefix)).ToArray();
        }

        // Defaults when Wizarr doesn't set them: no MIME sniffing, and don't leak /j/CODE URLs to other sites via Referer.
        if (!Response.Headers.ContainsKey("X-Content-Type-Options"))
        {
            Response.Headers["X-Content-Type-Options"] = "nosniff";
        }

        if (!Response.Headers.ContainsKey("Referrer-Policy"))
        {
            Response.Headers["Referrer-Policy"] = "same-origin";
        }

        if (HttpMethods.IsHead(Request.Method))
        {
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (IsRewritable(mediaType) && response.Content.Headers.ContentEncoding.Count == 0)
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

    /// <summary>
    /// Renames the cookie with <see cref="CookiePrefix"/>, drops any Domain and scopes its Path under the prefix.
    /// </summary>
    internal static string RewriteCookie(string cookie, string prefix)
    {
        const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        cookie = CookiePrefix + cookie.TrimStart();
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

    /// <summary>
    /// Keeps only cookies Wizarr set through this proxy (those named with <see cref="CookiePrefix"/>) and
    /// restores their original names.
    /// </summary>
    internal static string FilterRequestCookies(IEnumerable<string?> cookieHeaders)
    {
        var kept = new List<string>();
        foreach (var header in cookieHeaders)
        {
            if (string.IsNullOrEmpty(header))
            {
                continue;
            }

            foreach (var part in header.Split(';'))
            {
                var pair = part.Trim();
                if (pair.Length > CookiePrefix.Length && pair.StartsWith(CookiePrefix, StringComparison.Ordinal))
                {
                    kept.Add(pair[CookiePrefix.Length..]);
                }
            }
        }

        return string.Join("; ", kept);
    }

    private async Task WriteText(int status, string message, CancellationToken ct)
    {
        Response.StatusCode = status;
        Response.ContentType = "text/plain; charset=utf-8";
        await Response.WriteAsync(message, ct).ConfigureAwait(false);
    }

    [GeneratedRegex("^[A-Za-z0-9._~@+=,/-]*$")]
    private static partial Regex SafePathChars();
}
