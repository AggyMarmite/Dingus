using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Backup &amp; restore: settings, playlists, favourites, channel edits, history and reminders in one .zip.
/// Saved passwords are encrypted for this Windows account only, so they can't move to another PC as they are:
/// they're left out unless you choose to include them, in which case they're written in plain text.
/// </summary>
public static class Backup
{
    static readonly string[] Extra = { "reminders.json" };

    public static void Create(string zipPath, bool includePasswords)
    {
        SettingsStore.Save();
        HistoryStore.SaveNow();

        var node = JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile))!.AsObject();
        if (node["Playlists"] is JsonArray lists)
            foreach (var item in lists.OfType<JsonObject>())
            {
                var id = item["Id"]?.GetValue<string>();
                var p = SettingsStore.Current.Playlists.FirstOrDefault(x => x.Id == id);
                item["ProtectedPassword"] = "";
                if (includePasswords && p != null) item["BackupPassword"] = p.Password;
            }
        node["GpuPreferenceSaved"] = false;

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var e = zip.CreateEntry("settings.json");
        using (var w = new StreamWriter(e.Open()))
            w.Write(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (var f in Extra)
        {
            var path = Path.Combine(AppPaths.Root, f);
            if (File.Exists(path)) zip.CreateEntryFromFile(path, f);
        }
        // Watch history is kept per playlist.
        foreach (var p in SettingsStore.Current.Playlists)
        {
            var h = Path.Combine(AppPaths.PlaylistDir(p.Id), "history.json");
            if (File.Exists(h)) zip.CreateEntryFromFile(h, $"history/{p.Id}.json");
        }
        var info = zip.CreateEntry("dingus-backup.txt");
        using (var w = new StreamWriter(info.Open()))
            w.Write($"Dingus backup\nCreated {DateTime.Now:yyyy-MM-dd HH:mm}\nPasswords included: {(includePasswords ? "yes (plain text)" : "no")}\n");
    }

    /// <summary>Checks a backup and says what's in it, or throws if it isn't one.</summary>
    public static string Describe(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var e = zip.GetEntry("settings.json") ?? throw new InvalidDataException("That file isn't a Dingus backup.");
        using var r = new StreamReader(e.Open());
        var s = JsonSerializer.Deserialize<AppSettings>(r.ReadToEnd()) ?? throw new InvalidDataException("The backup is damaged.");
        return $"{s.Playlists.Count} playlist{(s.Playlists.Count == 1 ? "" : "s")}: {string.Join(", ", s.Playlists.Select(p => p.Name))}";
    }

    /// <summary>Replaces the current settings with the backup's. The app should restart afterwards.</summary>
    public static void Restore(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var e = zip.GetEntry("settings.json") ?? throw new InvalidDataException("That file isn't a Dingus backup.");
        string json;
        using (var r = new StreamReader(e.Open())) json = r.ReadToEnd();
        var restored = JsonSerializer.Deserialize<AppSettings>(json) ?? throw new InvalidDataException("The backup is damaged.");
        var node = JsonNode.Parse(json)!.AsObject();

        // Passwords: from the backup if it has them, otherwise keep the one saved on this PC for the same playlist.
        var lists = node["Playlists"] as JsonArray;
        foreach (var p in restored.Playlists)
        {
            var plain = lists?.OfType<JsonObject>().FirstOrDefault(x => x["Id"]?.GetValue<string>() == p.Id)?["BackupPassword"]?.GetValue<string>();
            var current = SettingsStore.Current.Playlists.FirstOrDefault(x => x.Id == p.Id);
            if (!string.IsNullOrEmpty(plain)) p.Password = plain;
            else if (current != null) p.ProtectedPassword = current.ProtectedPassword;
            else p.ProtectedPassword = "";
            p.LastRefreshUtc = default; // fetch fresh lists on next start
            p.LastEpgRefreshUtc = default;
        }
        // Keep this PC's own window and graphics settings.
        var cur = SettingsStore.Current;
        restored.GpuPreferenceSaved = cur.GpuPreferenceSaved;
        restored.GpuPreferenceOriginal = cur.GpuPreferenceOriginal;
        restored.WindowLeft = cur.WindowLeft; restored.WindowTop = cur.WindowTop;
        restored.WindowWidth = cur.WindowWidth; restored.WindowHeight = cur.WindowHeight;
        restored.WindowMaximized = cur.WindowMaximized;

        HistoryStore.Detach(); // so closing doesn't write the old history back over the restored one
        SettingsStore.Replace(restored);
        foreach (var p in restored.Playlists)
        {
            var entry = zip.GetEntry($"history/{p.Id}.json");
            if (entry != null) entry.ExtractToFile(Path.Combine(AppPaths.PlaylistDir(p.Id), "history.json"), overwrite: true);
        }
        foreach (var f in Extra)
        {
            var entry = zip.GetEntry(f);
            if (entry != null) entry.ExtractToFile(Path.Combine(AppPaths.Root, f), overwrite: true);
        }
        ThemeService.ApplyStartWithWindows(restored.StartWithWindows);
    }
}
