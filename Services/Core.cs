using System.Net;
using System.Runtime;
using System.Windows.Threading;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dingus.Models;

namespace Dingus.Services;

public static class AppPaths
{
    public static readonly string Root = MakeRoot();

    static string MakeRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(local, "Dingus");
        // The app used to be called Nimbus TV: move its settings, playlists and history across once.
        var old = Path.Combine(local, "NimbusTV");
        try { if (!Directory.Exists(root) && Directory.Exists(old)) Directory.Move(old, root); }
        catch { }
        return root;
    }

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "log.txt");

    public static string ImageCache
    {
        get { var p = Path.Combine(Root, "images"); Directory.CreateDirectory(p); return p; }
    }

    public static string PlaylistDir(string id)
    {
        var p = Path.Combine(Root, "playlists", id);
        Directory.CreateDirectory(p);
        return p;
    }
}

public static class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            if (File.Exists(AppPaths.SettingsFile))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Opts) ?? new();
        }
        catch (Exception ex) { Log.Error(ex); Current = new(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Opts));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex) { Log.Error(ex); }
    }
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Dingus/0.2");
        return c;
    }

    public static string Friendly(Exception ex) => ex switch
    {
        TaskCanceledException => "the server took too long to respond",
        HttpRequestException h when h.StatusCode != null => $"the server returned an error ({(int)h.StatusCode})",
        HttpRequestException h => "couldn't reach the server (" + h.Message + ")",
        _ => ex.Message
    };
}

public static class Secret
{
    public static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Error(Exception ex)
    {
        if (ex == null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
        }
        catch { }
    }
}

/// <summary>
/// After big one-off jobs (loading a playlist, matching the guide) .NET keeps the freed memory
/// reserved. Compacting once afterwards hands it back to Windows.
/// </summary>
public static class Memory
{
    static DispatcherTimer _timer;

    /// <summary>Compacts a couple of seconds from now (several calls in a row collapse into one).</summary>
    public static void CompactSoon()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => { _timer.Stop(); CompactNow(); };
        }
        _timer.Stop();
        _timer.Start();
    }

    public static void CompactNow()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        TrimWorkingSet();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    /// <summary>
    /// Hands memory pages the app isn't actively using back to Windows (start-up code, finished
    /// downloads, buffers from the previous stream). Anything still in use is simply read back in
    /// as needed, so playback isn't affected – but the Task Manager figure drops to what's really in use.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try { SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1)); }
        catch { }
    }

    public static long WorkingSetMB()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.WorkingSet64 / (1024 * 1024);
    }
}
