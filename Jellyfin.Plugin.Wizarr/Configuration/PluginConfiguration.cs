using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Wizarr.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the private address Jellyfin uses to reach Wizarr, e.g. http://wizarr:5690 on a shared Docker network.
    /// Wizarr is never contacted by anything else; invitees reach it only through Jellyfin at /invite.
    /// </summary>
    public string WizarrInternalUrl { get; set; } = "http://wizarr:5690";

    /// <summary>
    /// Gets or sets the public Jellyfin address used when building share links. Blank = the address the dashboard is open on.
    /// </summary>
    public string JellyfinPublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Wizarr API key. Leave blank to read it from the WIZARR_API_KEY environment variable instead.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Wizarr server id preselected when creating invites.
    /// </summary>
    public int DefaultServerId { get; set; }
}
