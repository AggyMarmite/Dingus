using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Dingus.Services;

/// <summary>
/// Attached property for lazily loading logos/posters:
///   svc:ImageLoader.DecodeWidth="112" svc:ImageLoader.Url="{Binding Logo}"
/// Images are cached on disk, decoded off the UI thread at display size (not full resolution),
/// frozen, and kept in a small in-memory LRU – so thousands of channels cost very little RAM.
/// </summary>
public static class ImageLoader
{
    static readonly HashSet<string> Failed = new();
    static readonly object FailLock = new();
    static readonly Lru Cache = new(24L * 1024 * 1024); // decoded images kept in RAM: 24 MB max

    public static readonly DependencyProperty DecodeWidthProperty =
        DependencyProperty.RegisterAttached("DecodeWidth", typeof(int), typeof(ImageLoader), new PropertyMetadata(160));
    public static int GetDecodeWidth(DependencyObject o) => (int)o.GetValue(DecodeWidthProperty);
    public static void SetDecodeWidth(DependencyObject o, int v) => o.SetValue(DecodeWidthProperty, v);

    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.RegisterAttached("Url", typeof(string), typeof(ImageLoader), new PropertyMetadata(null, OnUrlChanged));
    public static string GetUrl(DependencyObject o) => (string)o.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject o, string v) => o.SetValue(UrlProperty, v);

    /// <summary>
    /// Optional: a Border whose background takes a dark shade of the logo's main colour once it loads
    /// (channel cards on Home). Logos without a clear colour leave it unset.
    /// </summary>
    public static readonly DependencyProperty TintTargetProperty =
        DependencyProperty.RegisterAttached("TintTarget", typeof(Border), typeof(ImageLoader), new PropertyMetadata(null));
    public static Border GetTintTarget(DependencyObject o) => (Border)o.GetValue(TintTargetProperty);
    public static void SetTintTarget(DependencyObject o, Border v) => o.SetValue(TintTargetProperty, v);

    static void Show(Image img, string url, BitmapSource bmp)
    {
        img.Source = bmp;
        if (GetTintTarget(img) is Border b) b.Background = bmp == null ? null : LogoTint.For(url, bmp);
        HidePlaceholder(img, bmp != null);
    }

    /// <summary>
    /// The channel's initials sit underneath its logo as a stand-in. Once the logo has loaded they're hidden,
    /// so they don't show through logos with a see-through background.
    /// </summary>
    static void HidePlaceholder(Image img, bool loaded)
    {
        if (img.Parent is not Panel p) return;
        int at = p.Children.IndexOf(img);
        for (int i = 0; i < at; i++)
            if (p.Children[i] is TextBlock t) t.Visibility = loaded ? Visibility.Hidden : Visibility.Visible;
    }

    static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img) return;
        var url = e.NewValue as string;
        Show(img, url, null);
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;

        int w = GetDecodeWidth(img);
        var key = w + "|" + url;
        var hit = Cache.Get(key);
        if (hit != null) { Show(img, url, hit); return; }
        lock (FailLock) if (Failed.Contains(url)) return;

        try
        {
            // Downloading, saving and decoding all happen off the window's thread (a page of posters is dozens at once).
            // Downloading, saving and decoding all happen off the window's thread (a page of posters is dozens at once).
            var bmp = await Task.Run(() => LoadAsync(url, w));
            if (bmp == null) return;
            Cache.Add(key, bmp);
            if (GetUrl(img) == url) Show(img, url, bmp);
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>Loads an image directly (cached like the rest), or null if it can't be loaded.</summary>
    public static async Task<BitmapSource> GetAsync(string url, int decodeWidth)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        var key = decodeWidth + "|" + url;
        var hit = Cache.Get(key);
        if (hit != null) return hit;
        lock (FailLock) if (Failed.Contains(url)) return null;
        try
        {
            var bmp = await Task.Run(() => LoadAsync(url, decodeWidth));
            if (bmp != null) Cache.Add(key, bmp);
            return bmp;
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }

    static async Task<BitmapSource> LoadAsync(string url, int w)
    {
        var path = await ImageFiles.EnsureAsync(url);
        if (path == null) { MarkFailed(url); return null; }
        return await Task.Run(() => Decode(path, w, url));
    }

    static BitmapSource Decode(string path, int w, string url)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = w;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            MarkFailed(url); // unsupported format (svg/webp) or corrupt
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    static void MarkFailed(string url)
    {
        lock (FailLock) Failed.Add(url);
    }


    public static void ClearDiskCache()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.ImageCache))
                try { File.Delete(f); } catch { }
        }
        catch { }
        lock (FailLock) Failed.Clear();
    }

    public static long DiskCacheBytes()
    {
        try { return Directory.EnumerateFiles(AppPaths.ImageCache).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    /// <summary>Least-recently-used cache of decoded images, capped by total decoded size.</summary>
    sealed class Lru
    {
        readonly long _capBytes;
        long _bytes;
        readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Bmp, long Size)>> _map = new();
        readonly LinkedList<(string Key, BitmapSource Bmp, long Size)> _list = new();
        public Lru(long capBytes) => _capBytes = capBytes;

        public BitmapSource Get(string key)
        {
            lock (_map)
            {
                if (!_map.TryGetValue(key, out var node)) return null;
                _list.Remove(node);
                _list.AddFirst(node);
                return node.Value.Bmp;
            }
        }

        public void Add(string key, BitmapSource bmp)
        {
            long size = (long)bmp.PixelWidth * bmp.PixelHeight * 4;
            if (size > _capBytes / 4) return; // never let one huge image push everything out
            lock (_map)
            {
                if (_map.ContainsKey(key)) return;
                _map[key] = _list.AddFirst((key, bmp, size));
                _bytes += size;
                while (_bytes > _capBytes && _list.Last != null)
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
