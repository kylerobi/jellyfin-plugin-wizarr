using System.Net;
using Jellyfin.Plugin.Wizarr.Api;
using Jellyfin.Plugin.Wizarr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Branding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Wizarr;

/// <summary>
/// Adds (or removes) an invite link in Jellyfin's login disclaimer (Dashboard → General → Branding).
/// The plugin only ever touches the text between its own markers, so anything the admin wrote there stays.
/// </summary>
internal static class LoginLink
{
    internal const string StartMarker = "<!-- wizarr-invites:start (managed by the Wizarr Invites plugin) -->";
    internal const string EndMarker = "<!-- wizarr-invites:end -->";
    internal const string DefaultText = "Have an invite code? Join here";
    private const string BrandingKey = "branding";

    /// <summary>
    /// Brings the login disclaimer in line with the plugin settings. Saves only if something changed.
    /// </summary>
    internal static void Apply(IConfigurationManager configManager, PluginConfiguration config, ILogger logger)
    {
        try
        {
            var branding = configManager.GetConfiguration<BrandingOptions>(BrandingKey);
            var block = config.ShowLoginLink
                ? BuildBlock(config, configManager.GetNetworkConfiguration().BaseUrl)
                : null;
            var updated = UpdateDisclaimer(branding.LoginDisclaimer, block);

            if (!string.Equals(updated, branding.LoginDisclaimer ?? string.Empty, StringComparison.Ordinal))
            {
                branding.LoginDisclaimer = updated;
                configManager.SaveConfiguration(BrandingKey, branding);
                logger.LogInformation(block is null ? "Removed the invite link from the login screen" : "Updated the invite link on the login screen");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not update the invite link on the login screen");
        }
    }

    /// <summary>
    /// The link points at the separate invite address when one is set; otherwise at /invite on whatever address
    /// the login page was opened on (so it works on the LAN as well as from outside).
    /// </summary>
    internal static string BuildBlock(PluginConfiguration config, string? baseUrl)
    {
        var invite = WizarrConnection.PublicUri(config.InvitePublicUrl);
        var href = invite is not null
            ? invite.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/invite"
            : (baseUrl ?? string.Empty).TrimEnd('/') + "/invite";
        var text = string.IsNullOrWhiteSpace(config.LoginLinkText) ? DefaultText : config.LoginLinkText.Trim();

        return StartMarker + "\n"
               + "<a href=\"" + WebUtility.HtmlEncode(href) + "\" style=\"color:#00a4dc\">" + WebUtility.HtmlEncode(text) + "</a>\n"
               + EndMarker;
    }

    /// <summary>
    /// Removes any previous plugin block from <paramref name="current"/> and appends <paramref name="block"/> (if not null).
    /// </summary>
    internal static string UpdateDisclaimer(string? current, string? block)
    {
        var text = current ?? string.Empty;

        var start = text.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = text.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end >= 0)
            {
                text = text[..start].TrimEnd() + "\n\n" + text[(end + EndMarker.Length)..].TrimStart();
            }
        }

        text = text.Trim();
        if (block is not null)
        {
            text = text.Length == 0 ? block : text + "\n\n" + block;
        }

        return text;
    }
}
