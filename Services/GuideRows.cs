using Dingus.Models;
using Dingus.Views;

namespace Dingus.Services;

/// <summary>Live TV's channel rows – shared by the Windows and Linux apps. Safe to run off the window's thread.</summary>
public static class GuideRows
{
    /// <summary>Rows to show (your hidden channels left out), every row, rows by channel id, and channels per category.</summary>
    public sealed record Set(List<GuideRow> All, List<GuideRow> Everything, Dictionary<string, GuideRow> ById, Dictionary<string, int> Counts);

    /// <summary>Works out the rows: merges versions of the same channel, guide links, favourites, your names. No UI.</summary>
    public static Set Compute()
    {
        var ds = DataService.Instance;
        var data = ds.Catalog;
        var hidden = ds.HiddenSet("live");
        var favs = ds.FavouriteSet();
        var epg = ds.Epg;
        var hiddenCh = new HashSet<string>(ds.Active?.HiddenChannels ?? new List<string>());
        var names = ds.Active?.ChannelNames ?? new Dictionary<string, string>();

        var all = new List<GuideRow>(data.Channels.Count);
        var byId = new Dictionary<string, GuideRow>(data.Channels.Count);
        bool merge = SettingsStore.Current.MergeDuplicates;
        var groups = new Dictionary<string, GuideRow>();
        int i = 1;
        foreach (var c in data.Channels)
        {
            if (hidden.Contains(c.CategoryId)) continue;
            var key = merge ? ChannelMerger.Key(c) : "";
            if (key.Length == 0 || !groups.TryGetValue(key, out var row))
            {
                row = new GuideRow(c, i++);
                all.Add(row);
                if (key.Length > 0) groups[key] = row;
            }
            row.AddVariant(c);
            byId.TryAdd(c.Id, row);
        }
        foreach (var r in all)
        {
            r.IsFavourite = r.Variants.Any(v => favs.Contains(v.Id));
            r.Logo = r.Variants.Select(v => v.Logo).FirstOrDefault(l => l != null) ?? r.Variants.Select(v => epg.Icon(v.Id)).FirstOrDefault(l => l != null);
            r.EpgId = PickEpgId(r, epg);
            r.CustomName = r.Variants.Select(v => names.TryGetValue(v.Id, out var n) ? n : null).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            r.IsHidden = hiddenCh.Count > 0 && r.Variants.Any(v => hiddenCh.Contains(v.Id));
        }
        var everything = all;
        if (hiddenCh.Count > 0) all = all.Where(r => !r.IsHidden).ToList();

        var counts = new Dictionary<string, int>();
        foreach (var r in all)
            foreach (var cid in r.CategoryIds) counts[cid] = counts.TryGetValue(cid, out var n0) ? n0 + 1 : 1;
        return new Set(all, everything, byId, counts);
    }

    /// <summary>The variant whose id the guide has programmes for (else the first).</summary>
    public static string PickEpgId(GuideRow r, EpgData epg) =>
        r.Variants.Select(v => v.Id).FirstOrDefault(id => epg.Get(id) != null) ?? r.Channel.Id;
}
