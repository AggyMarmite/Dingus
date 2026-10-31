namespace Dingus.Services;

/// <summary>Checks Dingus can save into a folder (network shares can be slow, so off the window's thread).</summary>
public static class FolderCheck
{
    /// <summary>
    /// Free space where a folder actually is (on Linux that's the drive mounted there, e.g. /media/usb – not the system
    /// drive), or -1 when it can't be told (a Windows network share).
    /// </summary>
    public static long FreeSpace(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            if (full.StartsWith(@"\\")) return -1;
            var drive = DriveInfo.GetDrives()
                .Where(d => { try { return d.IsReady; } catch { return false; } })
                .Where(d => Under(full, d.RootDirectory.FullName))
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault();
            return drive?.AvailableFreeSpace ?? new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    static bool Under(string path, string root)
    {
        var sep = Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return (path.TrimEnd(sep) + sep).StartsWith(root.TrimEnd(sep) + sep, cmp);
    }

    /// <summary>The message to show, and whether it's fine (0), a warning (1) or a problem (2).</summary>
    public static async Task<(string Message, int Level)> RunAsync(string folder, string sizeHint)
    {
        var check = Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var test = Path.Combine(folder, ".dingus-write-test");
            File.WriteAllText(test, "ok");
            File.Delete(test);
            return FreeSpace(folder);
        });
        if (await Task.WhenAny(check, Task.Delay(8000)) != check)
            return ("This folder isn't answering – if it's on a NAS, check it's switched on and connected.", 1);
        if (check.IsFaulted) return ("Dingus can't save here: " + (check.Exception?.InnerException?.Message ?? "unknown error"), 2);
        long free = check.Result;
        return (free >= 0 ? $"✓ Ready  ·  {free / (double)(1L << 30):0} GB free ({sizeHint})" : "✓ Ready", 0);
    }
}
