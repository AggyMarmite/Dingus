using System.Net.Sockets;
using System.Text;

namespace Dingus.Services;

/// <summary>
/// One copy of Dingus at a time, using a small local socket in the user's runtime folder:
/// the first copy listens; a second copy pokes it ("show yourself") and quits.
/// </summary>
public static class SingleInstance
{
    static Socket _listener;
    static FileStream _lock;
    public static event Action Activated;

    static string Folder
    {
        get
        {
            var run = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var dir = !string.IsNullOrEmpty(run) && Directory.Exists(run) ? run : AppPaths.Root;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static string SocketPath => Path.Combine(Folder, "dingus.sock");
    static string LockPath => Path.Combine(Folder, "dingus.lock");

    public static bool TryBecomeFirst()
    {
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another copy holds the lock: ask it to come to the front.
            try
            {
                using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                s.Connect(new UnixDomainSocketEndPoint(SocketPath));
                s.Send(Encoding.UTF8.GetBytes("show"));
            }
            catch { }
            return false;
        }
        catch (Exception ex) { Log.Error(ex); return true; }

        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(4);
            var t = new Thread(Listen) { IsBackground = true, Name = "Single instance" };
            t.Start();
        }
        catch (Exception ex) { Log.Error(ex); }
        return true;
    }

    static void Listen()
    {
        var buf = new byte[16];
        while (_listener != null)
        {
            try
            {
                using var c = _listener.Accept();
                c.Receive(buf);
                Activated?.Invoke();
            }
            catch { if (_listener == null) return; Thread.Sleep(200); }
        }
    }

    public static void Release()
    {
        try { var l = _listener; _listener = null; l?.Dispose(); } catch { }
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
        try { _lock?.Dispose(); _lock = null; } catch { }
    }
}
