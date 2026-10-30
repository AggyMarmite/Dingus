namespace Dingus.Services;

/// <summary>Checks Dingus can save into a folder (network shares can be slow, so off the window's thread).</summary>
public static class FolderCheck
{
    /// <summary>The message to show, and whether it's fine (0), a warning (1) or a problem (2).</summary>
    public static async Task<(string Message, int Level)> RunAsync(string folder, string sizeHint)
    {
        var check = Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var test = Path.Combine(folder, ".dingus-write-test");
            File.WriteAllText(test, "ok");
            File.Delete(test);
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            try { return new DriveInfo(root!).AvailableFreeSpace; } catch { return -1L; }
        });
        if (await Task.WhenAny(check, Task.Delay(8000)) != check)
            return ("This folder isn't answering – if it's on a NAS, check it's switched on and connected.", 1);
        if (check.IsFaulted) return ("Dingus can't save here: " + (check.Exception?.InnerException?.Message ?? "unknown error"), 2);
        long free = check.Result;
        return (free >= 0 ? $"✓ Ready  ·  {free / (double)(1L << 30):0} GB free ({sizeHint})" : "✓ Ready", 0);
    }
}
