using Jellyfin.Plugin.Fastshare.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>Vstupní bod pluginu. Drží konfiguraci a registruje konfigurační stránku.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("10e6271d-79f2-400a-b8a6-89e51a9a4273");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Fastshare";

    public override string Description =>
        "Hledání na Fastshare.cloud a stahování do knihovny Jellyfinu (souběžně, s časovými okny a hlídačem seriálů).";

    public override Guid Id => PluginId;

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "fastshare",
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html",
                DisplayName = "Fastshare",
                EnableInMainMenu = true,
                MenuIcon = "cloud_download",
            },
        ];
    }
}
