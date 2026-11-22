using Jellyfin.Plugin.Wizarr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Wizarr;

/// <summary>
/// Serves Wizarr invites through the Jellyfin address and manages them from the dashboard.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "0cd6f4b8-dc11-478f-b7e9-0d10cd1aeee9";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Wizarr Invites";

    public override string Description => "Serves Wizarr's invite sign-up at /invite on your Jellyfin address (Wizarr stays private) and manages invites from the dashboard.";

    public override Guid Id => Guid.Parse(PluginGuid);

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
