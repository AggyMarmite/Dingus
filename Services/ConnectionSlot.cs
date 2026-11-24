using System.Net.NetworkInformation;
using Dingus.Models;

namespace Dingus.Services;

/// <summary>
/// Waiting for the provider to free a connection. Providers count a stream as "in use" until their side notices it has
/// gone, so a connection that was cut off rather than closed (switching VPN server, the Wi-Fi dropping) can keep a
/// 1-connection plan "full" for several minutes. When a channel won't start and the provider says every connection is
/// in use, the player waits here and starts the channel as soon as one is free, instead of giving up. Shared by both apps.
/// </summary>
public static class ConnectionSlot
{
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(8);

    /// <summary>Asks the provider how many connections are in use right now. Null when it can't say (no limit, not an Xtream login, unreachable).</summary>
    public static async Task<(int Active, int Max)?> CheckAsync()
    {
        var ds = DataService.Instance;
        if (ds.Xtream == null) return null;
        AccountInfo acc;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            acc = await ds.Xtream.LoginAsync(cts.Token);
        }
        catch (Exception ex) { Log.Error(ex); return null; }
        ds.SetAccount(acc);
        return acc.MaxConnections > 0 ? (acc.ActiveConnections, acc.MaxConnections) : null;
    }

    /// <summary>
    /// Checks every <see cref="PollEvery"/> until a connection is free (true), or until <paramref name="stillWanted"/>
    /// says you've moved on, the provider can't be asked, or <see cref="GiveUpAfter"/> passes (false).
    /// <paramref name="waiting"/> is told the latest count each time it's still full.
    /// </summary>
    public static async Task<bool> WaitAsync(Func<bool> stillWanted, Action<int, int> waiting)
    {
        var until = DateTime.UtcNow + GiveUpAfter;
        while (stillWanted())
        {
            var s = await CheckAsync();
            if (!stillWanted() || s is not { } now) return false;
            if (now.Active < now.Max) return true;
            waiting(now.Active, now.Max);
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(PollEvery);
        }
        return false;
    }
}

/// <summary>
/// Tells the player when this computer's network changes (a VPN connecting, disconnecting or changing, Wi-Fi switching),
/// so a stream that was cut off by it is reopened within a few seconds instead of waiting for the usual stall check.
/// Only real changes count: the addresses of the connections that are up must differ from before. Shared by both apps.
/// </summary>
public static class NetworkWatch
{
    /// <summary>Raised on a background thread, once the change has settled for a moment.</summary>
    public static event Action Changed;

    static readonly object _lock = new();
    static string _last;
    static Timer _settle;
    static bool _started;

    public static void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _started = true;
            _last = Snapshot();
        }
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => Poke();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => Poke();
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    static void Poke()
    {
        lock (_lock)
        {
            _settle?.Dispose();
            _settle = new Timer(_ => Check(), null, 1500, Timeout.Infinite);
        }
    }

    static void Check()
    {
        var now = Snapshot();
        lock (_lock)
        {
            if (now == null || now == _last) return;
            _last = now;
        }
        Log.Info("Network changed");
        try { Changed?.Invoke(); } catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>The connections that are up and their IPv4 addresses (IPv6 privacy addresses change by themselves, so they're left out).</summary>
    static string Snapshot()
    {
        try
        {
            var parts = new List<string>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var v4 = ni.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()).OrderBy(a => a);
                parts.Add(ni.Id + "=" + string.Join(",", v4));
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts);
        }
        catch (Exception ex) { Log.Error(ex); return null; }
    }
}
