using System.Text;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// The saved copy of a playlist (channels, films, shows) in a compact binary form: several times quicker to read
/// at launch than the JSON it replaces, so the saved playlist is on screen sooner. Only Dingus reads it; if it's
/// missing or from a different version, the playlist is simply loaded from the provider again.
/// </summary>
static class CatalogFile_
{
    const string Magic = "DingusPlaylist";
    const int Version = 1;

    public static void Write(string path, CatalogData d)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs, Encoding.UTF8);
        w.Write(Magic); w.Write(Version);
        Cats(w, d.LiveCategories); Cats(w, d.VodCategories); Cats(w, d.SeriesCategories);
        w.Write(d.Channels.Count);
        foreach (var c in d.Channels)
        {
            S(w, c.Id); w.Write(c.Num); S(w, c.Name); S(w, c.Logo); S(w, c.EpgId); S(w, c.CategoryId); S(w, c.Url);
            w.Write(c.HasArchive); w.Write(c.ArchiveDays);
        }
        w.Write(d.Movies.Count);
        foreach (var m in d.Movies)
        {
            S(w, m.Id); S(w, m.Name); S(w, m.Poster); S(w, m.CategoryId); w.Write(m.Rating); S(w, m.Year); S(w, m.Ext);
            S(w, m.Url); w.Write(m.Added);
        }
        w.Write(d.Series.Count);
        foreach (var x in d.Series)
        {
            S(w, x.Id); S(w, x.Name); S(w, x.Cover); S(w, x.CategoryId); S(w, x.Plot); S(w, x.Genre); w.Write(x.Rating);
            S(w, x.Year); w.Write(x.Added);
            w.Write(x.Episodes?.Count ?? -1);
            if (x.Episodes != null)
                foreach (var e in x.Episodes)
                {
                    S(w, e.Id); S(w, e.Title); w.Write(e.Season); w.Write(e.Number); S(w, e.Ext); S(w, e.Url);
                    S(w, e.Plot); S(w, e.Image); S(w, e.Duration);
                }
        }
        var a = d.Account;
        w.Write(a != null);
        if (a != null)
        {
            S(w, a.Status); w.Write(a.ExpiresUtc?.Ticks ?? -1); w.Write(a.ActiveConnections); w.Write(a.MaxConnections);
            w.Write(a.Trial); S(w, a.ServerTimeZone);
        }
        S(w, d.SourceEpgUrl);
    }

    public static CatalogData Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadString() != Magic || r.ReadInt32() != Version) return null;
            var d = new CatalogData { LiveCategories = Cats(r), VodCategories = Cats(r), SeriesCategories = Cats(r) };
            int n = r.ReadInt32();
            d.Channels = new List<Channel>(n);
            for (int i = 0; i < n; i++)
                d.Channels.Add(new Channel
                {
                    Id = S(r) ?? "", Num = r.ReadInt32(), Name = S(r) ?? "", Logo = S(r), EpgId = S(r) ?? "", CategoryId = S(r) ?? "", Url = S(r),
                    HasArchive = r.ReadBoolean(), ArchiveDays = r.ReadInt32(),
                });
            n = r.ReadInt32();
            d.Movies = new List<Movie>(n);
            for (int i = 0; i < n; i++)
                d.Movies.Add(new Movie
                {
                    Id = S(r) ?? "", Name = S(r) ?? "", Poster = S(r), CategoryId = S(r) ?? "", Rating = r.ReadDouble(), Year = S(r), Ext = S(r) ?? "mp4",
                    Url = S(r), Added = r.ReadInt64(),
                });
            n = r.ReadInt32();
            d.Series = new List<Series>(n);
            for (int i = 0; i < n; i++)
            {
                var x = new Series
                {
                    Id = S(r) ?? "", Name = S(r) ?? "", Cover = S(r), CategoryId = S(r) ?? "", Plot = S(r), Genre = S(r), Rating = r.ReadDouble(),
                    Year = S(r), Added = r.ReadInt64(),
                };
                int ne = r.ReadInt32();
                if (ne >= 0)
                {
                    x.Episodes = new List<Episode>(ne);
                    for (int j = 0; j < ne; j++)
                        x.Episodes.Add(new Episode
                        {
                            Id = S(r) ?? "", Title = S(r) ?? "", Season = r.ReadInt32(), Number = r.ReadInt32(), Ext = S(r) ?? "mp4", Url = S(r),
                            Plot = S(r), Image = S(r), Duration = S(r),
                        });
                }
                d.Series.Add(x);
            }
            if (r.ReadBoolean())
            {
                var a = new AccountInfo { Status = S(r) };
                long t = r.ReadInt64();
                a.ExpiresUtc = t < 0 ? null : new DateTime(t, DateTimeKind.Utc);
                a.ActiveConnections = r.ReadInt32(); a.MaxConnections = r.ReadInt32(); a.Trial = r.ReadBoolean(); a.ServerTimeZone = S(r);
                d.Account = a;
            }
            d.SourceEpgUrl = S(r);
            return d;
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    static void Cats(BinaryWriter w, List<Category> l)
    {
        w.Write(l.Count);
        foreach (var c in l) { S(w, c.Id); S(w, c.Name); }
    }

    static List<Category> Cats(BinaryReader r)
    {
        int n = r.ReadInt32();
        var l = new List<Category>(n);
        for (int i = 0; i < n; i++) l.Add(new Category { Id = S(r) ?? "", Name = S(r) ?? "" });
        return l;
    }

    // Strings that may be missing: a flag, then the text.
    static void S(BinaryWriter w, string v) { w.Write(v != null); if (v != null) w.Write(v); }
    static string S(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
}
