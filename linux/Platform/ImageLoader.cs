using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Dingus.Services;

/// <summary>
/// Lazily loads logos and posters:  svc:ImageLoader.DecodeWidth="112" svc:ImageLoader.Url="{Binding Logo}".
/// Cached on disk, decoded off the UI thread at display size, and kept in a small in-memory cache.
/// </summary>
public class ImageLoader : AvaloniaObject
{
    static readonly SemaphoreSlim Gate = new(6);
    static readonly HashSet<string> Failed = new();
    static readonly object FailLock = new();
    static readonly Lru Cache = new(24L * 1024 * 1024);

    public static readonly AttachedProperty<int> DecodeWidthProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, int>("DecodeWidth", 160);
    public static int GetDecodeWidth(Image o) => o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(Image o, int v) => o.SetValue(DecodeWidthProperty, v);

    public static readonly AttachedProperty<string> UrlProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, string>("Url");
    public static string GetUrl(Image o) => o.GetValue(UrlProperty);
    public static void SetUrl(Image o, string v) => o.SetValue(UrlProperty, v);

    /// <summary>Optional: a Border whose background takes a dark shade of the logo's colour once it loads.</summary>
    public static readonly AttachedProperty<Border> TintTargetProperty =
        AvaloniaProperty.RegisterAttached<ImageLoader, Image, Border>("TintTarget");
    public static Border GetTintTarget(Image o) => o.GetValue(TintTargetProperty);
    public static void SetTintTarget(Image o, Border v) => o.SetValue(TintTargetProperty, v);

    static ImageLoader()
    {
        UrlProperty.Changed.AddClassHandler<Image>((img, e) => OnUrlChanged(img, e.NewValue as string));
    }

    static void Show(Image img, string url, Bitmap bmp)
    {
        img.Source = bmp;
        if (GetTintTarget(img) is Border b) b.Background = bmp == null ? null : LogoTint.For(url, bmp);
    }

    static async void OnUrlChanged(Image img, string url)
    {
        Show(img, url, null);
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        int w = GetDecodeWidth(img);
        var key = w + "|" + url;
        var hit = Cache.Get(key);
        if (hit != null) { Show(img, url, hit); return; }
        lock (FailLock) if (Failed.Contains(url)) return;
        try
        {
            var bmp = await LoadAsync(url, w);
            if (bmp == null) return;
            Cache.Add(key, bmp);
            if (GetUrl(img) == url) Show(img, url, bmp);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>Loads an image directly (cached like the rest), or null.</summary>
    public static async Task<Bitmap> GetAsync(string url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        var key = decodeWidth + "|" + url;
        var hit = Cache.Get(key);
        if (hit != null) return hit;
        lock (FailLock) if (Failed.Contains(url)) return null;
        try
        {
            var bmp = await LoadAsync(url, decodeWidth);
            if (bmp != null) Cache.Add(key, bmp);
            return bmp;
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    static async Task<Bitmap> LoadAsync(string url, int w)
    {
        var path = Path.Combine(AppPaths.ImageCache, Hash(url));
        if (!File.Exists(path))
        {
            await Gate.WaitAsync();
            try
            {
                if (!File.Exists(path))
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var bytes = await Http.Client.GetByteArrayAsync(url, cts.Token);
                    if (bytes.Length < 32) { MarkFailed(url); return null; }
                    var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    await File.WriteAllBytesAsync(tmp, bytes);
                    File.Move(tmp, path, true);
                }
            }
            catch { MarkFailed(url); return null; }
            finally { Gate.Release(); }
        }
        return await Task.Run(() => Decode(path, w, url));
    }

    static Bitmap Decode(string path, int w, string url)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Bitmap.DecodeToWidth(fs, Math.Max(16, w), BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            MarkFailed(url);
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    static void MarkFailed(string url) { lock (FailLock) Failed.Add(url); }
    static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    public static void ClearDiskCache()
    {
        try { foreach (var f in Directory.EnumerateFiles(AppPaths.ImageCache)) try { File.Delete(f); } catch { } }
        catch { }
        lock (FailLock) Failed.Clear();
    }

    public static long DiskCacheBytes()
    {
        try { return Directory.EnumerateFiles(AppPaths.ImageCache).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    sealed class Lru
    {
        readonly long _cap;
        long _bytes;
        readonly Dictionary<string, LinkedListNode<(string Key, Bitmap Bmp, long Size)>> _map = new();
        readonly LinkedList<(string Key, Bitmap Bmp, long Size)> _list = new();
        public Lru(long cap) => _cap = cap;

        public Bitmap Get(string key)
        {
            lock (_map)
            {
                if (!_map.TryGetValue(key, out var n)) return null;
                _list.Remove(n); _list.AddFirst(n);
                return n.Value.Bmp;
            }
        }

        public void Add(string key, Bitmap bmp)
        {
            long size = (long)bmp.PixelSize.Width * bmp.PixelSize.Height * 4;
            if (size > _cap / 4) return;
            lock (_map)
            {
                if (_map.ContainsKey(key)) return;
                _map[key] = _list.AddFirst((key, bmp, size));
                _bytes += size;
                while (_bytes > _cap && _list.Last != null)
                {
                    var last = _list.Last.Value;
                    _list.RemoveLast();
                    _map.Remove(last.Key);
                    _bytes -= last.Size;
                }
            }
        }
    }
}
