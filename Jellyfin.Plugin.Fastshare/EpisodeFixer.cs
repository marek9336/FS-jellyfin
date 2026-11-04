using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Fastshare;

/// <summary>
/// Přečíslování dílu v Jellyfinu. Smazat jeho .nfo nestačí: obnova s „nahradit vše" číslo dílu vymaže a z cesty ho
/// nedoplní, běžný sken nechá staré (ověřeno v testovacím JF 12.2, 4. 11.). Proto: řada a díl rovnou z názvu souboru,
/// smazat údaje o původním dílu (název, popis, ID v TMDb) a běžnou obnovou dotáhnout správný díl z TMDb.
/// </summary>
public sealed class EpisodeFixer
{
    private readonly ILibraryManager _library;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<EpisodeFixer> _logger;

    public EpisodeFixer(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, ILogger<EpisodeFixer> logger)
    {
        _library = library;
        _providers = providers;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Díl na `path` má být SxxEyy. Když ho Jellyfin zná (soubor se nepřesouval, nebo už je naskenovaný) a čísluje jinak,
    /// přepíše čísla a zařadí obnovu metadat. Nový soubor (po přesunu) Jellyfin při skenu očísluje sám z názvu — .nfo už nemá.
    /// </summary>
    public async Task<bool> RenumberAsync(string path, int season, int episode, CancellationToken ct)
    {
        if (_library.FindByPath(path, false) is not Episode ep)
        {
            return false;
        }

        if (ep.ParentIndexNumber == season && ep.IndexNumber == episode)
        {
            return false;
        }

        _logger.LogInformation("Fastshare: {Path} přečíslováno S{OldS:D2}E{OldE:D2} → S{S:D2}E{E:D2}", path, ep.ParentIndexNumber ?? 0, ep.IndexNumber ?? 0, season, episode);
        ep.ParentIndexNumber = season;
        ep.IndexNumber = episode;
        ep.IndexNumberEnd = null;
        ep.Name = Path.GetFileNameWithoutExtension(path); // název, popis a ID patřily dílu, kterým soubor nebyl
        ep.Overview = null;
        ep.PremiereDate = null;
        ep.ProductionYear = null;
        foreach (var key in ep.ProviderIds.Keys.ToList())
        {
            ep.ProviderIds.Remove(key);
        }

        await ep.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        _providers.QueueRefresh(
            ep.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = true,
            },
            RefreshPriority.High);
        return true;
    }
}
