namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Pořadí stahování na jednom místě — worker i odhad času musí brát položky
/// úplně stejně, jinak by odhad ukazoval jiné pořadí, než v jakém se reálně stahuje.
/// </summary>
public static class QueueOrder
{
    /// <summary>
    /// Pořadí ve frontě: ⚡ přednost → filmy před seriály → ruční pořadí (▲▼) a čas zařazení. Díly jednoho seriálu
    /// drží pohromadě (seriál stojí tam, kde jeho nejdřív zařazený díl) a jdou S01E01 → …
    /// </summary>
    public static IOrderedEnumerable<QueueItem> ByPriority(IEnumerable<QueueItem> items)
    {
        var list = items as IReadOnlyCollection<QueueItem> ?? items.ToList();
        var rank = list.Where(i => i.MediaType == MediaKind.Episode)
            .GroupBy(i => (i.ForceNow, Key: SeriesKey(i)!))
            .ToDictionary(g => g.Key, g => g.Min(i => (i.SortIndex, i.AddedUtc)));
        (long SortIndex, DateTime AddedUtc) R(QueueItem i) =>
            i.MediaType == MediaKind.Episode ? rank[(i.ForceNow, SeriesKey(i)!)] : (i.SortIndex, i.AddedUtc);
        return list.OrderByDescending(i => i.ForceNow)
            .ThenBy(i => i.MediaType == MediaKind.Episode ? 1 : 0)
            .ThenBy(i => R(i).SortIndex)
            .ThenBy(i => R(i).AddedUtc)
            .ThenBy(i => SeriesKey(i) ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(i => i.Season ?? 0)
            .ThenBy(i => i.Episode ?? 0)
            .ThenBy(i => i.AddedUtc);
    }

    /// <summary>
    /// Která z čekajících položek jde na řadu. Když je na řadě epizoda, vezme se
    /// z téhož seriálu nejnižší nestažená (E01 → E02 → …), ať to vypadá jako reálné
    /// sledování — i když v ručním pořadí stojí výš pozdější díl.
    /// </summary>
    public static QueueItem? PickNext(IReadOnlyCollection<QueueItem> queued)
    {
        var item = ByPriority(queued).FirstOrDefault();
        if (item == null || item.MediaType != MediaKind.Episode || item.ForceNow)
        {
            return item;
        }

        var series = SeriesKey(item);
        return queued
            .Where(i => i.MediaType == MediaKind.Episode && !i.ForceNow && SeriesKey(i) == series)
            .OrderBy(i => i.Season ?? 0)
            .ThenBy(i => i.Episode ?? 0)
            .FirstOrDefault() ?? item;
    }

    /// <summary>Klíč seriálu (normalizovaný název), u filmu null.</summary>
    public static string? SeriesKey(QueueItem item) =>
        item.MediaType == MediaKind.Episode
            ? MediaOrganizer.CleanTitle(item.SeriesTitle ?? item.Title).ToLowerInvariant()
            : null;

    /// <summary>Klíč seriálu z názvu, jak ho posílá GUI.</summary>
    public static string SeriesKey(string seriesTitle) =>
        MediaOrganizer.CleanTitle(seriesTitle).ToLowerInvariant();

    /// <summary>
    /// Ke které hlídané položce položka fronty patří: seriál podle názvu, film podle
    /// názvu (rok se nebere — u ručně přidaných položek nemusí sedět).
    /// </summary>
    public static string GroupKey(QueueItem item) =>
        item.MediaType == MediaKind.Episode
            ? "ep|" + SeriesKey(item)
            : "mv|" + MediaOrganizer.CleanTitle(item.Title).ToLowerInvariant();

    /// <summary>Tentýž klíč pro hlídanou položku (viz <see cref="GroupKey(QueueItem)"/>).</summary>
    public static string GroupKey(WatchItem item) =>
        (item.Type == "series" ? "ep|" : "mv|") + MediaOrganizer.CleanTitle(item.Title).ToLowerInvariant();
}
