using System.Net;
using System.Runtime;
#if LINUX
using Avalonia.Threading;
#else
using System.Windows.Threading;
#endif
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

    /// <summary>Swaps in different settings (backup restore) and saves them.</summary>
    public static void Replace(AppSettings s)
    {
        Current = s ?? new();
        Save();
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
        #if LINUX
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) Dingus/0.8");
#else
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Dingus/0.2");
#endif
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

#if LINUX
/// <summary>
/// Passwords are encrypted with a random key kept in a file only you can read (~/.local/share/Dingus/.key),
/// the Linux equivalent of the Windows version's per-user protection.
/// </summary>
public static class Secret
{
    static byte[] _key;

    static byte[] Key()
    {
        if (_key != null) return _key;
        var path = Path.Combine(AppPaths.Root, ".key");
        try
        {
            if (File.Exists(path)) { var k = File.ReadAllBytes(path); if (k.Length == 32) return _key = k; }
            Directory.CreateDirectory(AppPaths.Root);
            var key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(path, key);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return _key = key;
        }
        catch (Exception ex) { Log.Error(ex); return _key = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + Environment.MachineName)); }
    }

    public static string Protect(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        using var aes = new AesGcm(Key(), 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(s);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        aes.Encrypt(nonce, plain, cipher, tag);
        return "v1:" + Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public static string Unprotect(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.StartsWith("v1:")) return "";
        try
        {
            var all = Convert.FromBase64String(s[3..]);
            using var aes = new AesGcm(Key(), 16);
            var plain = new byte[all.Length - 28];
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }
}
#else
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
#endif

public static class Log
{
    static readonly object Gate = new();

    public static void Error(Exception ex)
    {
        if (ex == null) return;
        Write(ex.ToString());
    }

    /// <summary>A note (not an error) for the log.</summary>
    public static void Info(string message) => Write(message);

    static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
            }
        }
        catch { }
    }
}

/// <summary>
/// After big one-off jobs (loading a playlist, matching the guide) .NET keeps the freed memory
/// reserved. Compacting once afterwards hands it back to Windows – but only once you've left the window alone
/// for a few seconds, because a full compaction pauses the app (over a second with a very big playlist) and the
/// memory handed back has to be read in again on the next click.
/// </summary>
public static class Memory
{
    static DispatcherTimer _timer;
    static bool _wanted;
    static DateTime _requested, _lastInput = DateTime.MinValue;

    /// <summary>How long you need to have left the window alone before tidying up.</summary>
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(4);

    /// <summary>Called on every key press, click and mouse wheel (and by the speed test's pretend clicks).</summary>
    public static void NoteInput() => _lastInput = DateTime.UtcNow;

    /// <summary>Compacts soon – a couple of seconds from now, once the window is quiet (several calls collapse into one).</summary>
    public static void CompactSoon()
    {
        if (_timer == null)
        {
#if LINUX
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
#else
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1) };
#endif
            _timer.Tick += (_, _) =>
            {
                var now = DateTime.UtcNow;
                if (!_wanted) { _timer.Stop(); return; }
                if (now - _requested < TimeSpan.FromSeconds(2) || now - _lastInput < Quiet) return; // not yet
                _wanted = false;
                _timer.Stop();
                CompactNow();
            };
        }
        _wanted = true;
        _requested = DateTime.UtcNow;
        _timer.Start();
    }

    public static void CompactNow() => DataService.Time("memory: compacting", () =>
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        TrimWorkingSet();
    });

#if LINUX
    [System.Runtime.InteropServices.DllImport("libc")]
    static extern int malloc_trim(UIntPtr pad);

    /// <summary>Hands freed memory back to the system.</summary>
    public static void TrimWorkingSet()
    {
        try { malloc_trim(UIntPtr.Zero); }
        catch { }
    }
#else
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
#endif

    public static long WorkingSetMB()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return p.WorkingSet64 / (1024 * 1024);
    }
}
