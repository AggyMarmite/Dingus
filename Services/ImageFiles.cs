using System.Security.Cryptography;
using System.Text;

namespace Dingus.Services;

/// <summary>
/// The disk side of logos and posters, shared by the Windows and Linux image loaders: downloads each picture once
/// into the image cache and hands back its file. When several cards want the same picture at the same moment
/// (the same crest on five games, a logo on many rows) they all wait for one download – before, each fetched its
/// own copy, and replacing the file while another card was reading it could leave that card blank.
/// </summary>
public static class ImageFiles
{
    static readonly SemaphoreSlim Gate = new(6);
    static readonly Dictionary<string, Task<string>> InFlight = new();

    public static string PathFor(string url) =>
        Path.Combine(AppPaths.ImageCache, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));

    /// <summary>The picture's file in the cache (downloading it first if needed), or null if it couldn't be fetched.</summary>
    public static Task<string> EnsureAsync(string url)
    {
        var path = PathFor(url);
        if (File.Exists(path)) return Task.FromResult(path);
        lock (InFlight)
        {
            if (InFlight.TryGetValue(path, out var running)) return running;
            var t = DownloadAsync(url, path);
            InFlight[path] = t;
            _ = t.ContinueWith(_ => { lock (InFlight) InFlight.Remove(path); }, TaskScheduler.Default);
            return t;
        }
    }

    static async Task<string> DownloadAsync(string url, string path)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path)) return path;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var bytes = await Http.Client.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false);
            if (bytes.Length < 32) return null;
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            try { File.Move(tmp, path, true); }
            catch (IOException) when (File.Exists(path)) { try { File.Delete(tmp); } catch { } } // someone else's copy is there
            return path;
        }
        catch { return null; }
        finally { Gate.Release(); }
    }
}
