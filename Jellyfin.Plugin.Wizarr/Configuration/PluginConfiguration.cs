using System.Text.Json.Serialization;
using System.Xml.Serialization;
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
    /// Gets or sets an optional separate public address for invites, e.g. https://invite.example.com, that points at the
    /// same Jellyfin server. When set, /invite is only served on this host, so Wizarr's pages never share a browser
    /// origin (and therefore stored Jellyfin logins) with the Jellyfin web app.
    /// </summary>
    public string InvitePublicUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Wizarr API key. Leave blank to read it from the WIZARR_API_KEY environment variable instead.
    /// Saved to the plugin's XML file but never sent to the browser.
    /// </summary>
    [JsonIgnore]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether an API key is saved. Lets the dashboard show "(saved)" without seeing the key.
    /// </summary>
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// Gets or sets a new API key sent by the dashboard. Write-only: applied by <see cref="Plugin.UpdateConfiguration"/> and never stored.
    /// </summary>
    [XmlIgnore]
    public string? NewApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the dashboard asked to remove the saved API key. Never stored.
    /// </summary>
    [XmlIgnore]
    public bool ClearApiKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin adds an invite link to Jellyfin's login screen
    /// (in the login disclaimer, between its own markers).
    /// </summary>
    public bool ShowLoginLink { get; set; }

    /// <summary>
    /// Gets or sets the text of the login-screen link. Blank = "Have an invite code? Join here".
    /// </summary>
    public string LoginLinkText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Wizarr server id preselected when creating invites.
    /// </summary>
    public int DefaultServerId { get; set; }
}
