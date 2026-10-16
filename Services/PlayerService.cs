using LibVLCSharp.Shared;

namespace Dingus.Services;

public record TrackOption(int Id, string Name, bool Current);

/// <summary>Where live TV is coming from: straight from the provider, the rewind buffer, or the provider's catch-up archive.</summary>
public enum LiveSource { Direct, Buffer, Archive }

/// <summary>Snapshot of how the current stream is doing (for the stream-health badge and stall detection).</summary>
public record StreamHealth(
    uint Width, uint Height, float Fps, double Mbps,
    int DisplayedPictures, int LostPictures, long TimeMs, long LengthMs, VLCState State,
    bool? GpuDecoding);

/// <summary>
/// One VLC engine + one player for the whole app (the Guide preview, the full-screen player and the
/// mini player share it, so switching between them is instant and never opens a second stream).
/// All blocking libVLC calls run on a single background queue so the UI never stalls when zapping.
/// </summary>
public sealed class PlayerService
{
    public LibVLC Vlc { get; }
    public MediaPlayer Player { get; }

    Task _queue = Task.CompletedTask;
    readonly object _qLock = new();

    /// <summary>
    /// True once VLC reports the graphics card is decoding the current stream; false if it's playing
    /// without that (CPU decoding); null while we don't know yet.
    /// </summary>
    public bool? GpuDecoding { get; private set; }
    DateTime _playingSince;

    public PlayerService()
    {
        // Software decoding keeps a set of frames per decoder thread – at 4K that's ~25 MB per frame,
        // so "one thread per CPU core" can cost well over a gigabyte. A few threads decode 4K fine.
        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        Vlc = new LibVLC(false,
            "--no-video-title-show",
            "--no-snapshot-preview",
            "--no-sub-autodetect-file",
            "--no-lua",
            "--http-reconnect",
            $"--avcodec-threads={threads}");

        // VLC announces "Using D3D11VA (…) for hardware decoding" when the graphics card takes over.
        Vlc.Log += (_, e) =>
        {
            var msg = e.Message;
            if (msg != null && msg.Contains("for hardware decoding", StringComparison.OrdinalIgnoreCase))
                GpuDecoding = true;
        };

        Player = new MediaPlayer(Vlc)
        {
            EnableKeyInput = false,
            EnableMouseInput = false,
            EnableHardwareDecoding = SettingsStore.Current.HardwareDecoding,
        };

        // The play clock for live pause & rewind: runs only while the picture is actually moving.
        Player.Playing += (_, _) => { lock (_tsLock) _tsClock.Start(); };
        Player.Paused += (_, _) => { lock (_tsLock) _tsClock.Stop(); };
        Player.Stopped += (_, _) => { lock (_tsLock) _tsClock.Stop(); };
        Player.EndReached += (_, _) => { lock (_tsLock) _tsClock.Stop(); };
    }

    // ------------------------------------------------------------------ live pause & rewind

    readonly object _tsLock = new();
    readonly System.Diagnostics.Stopwatch _tsClock = new();
    TimeshiftSession _ts;
    DateTime _tsBaseUtc; // stream time at the point VLC was started from
    int _gen;            // bumps on every Play/Stop, so a slow start-up for an old channel is dropped

    LiveSource _source = LiveSource.Direct;
    public LiveSource Source { get { lock (_tsLock) return _source; } }

    /// <summary>The live channel is being buffered, so it can be paused and rewound.</summary>
    public bool CanTimeshift { get { lock (_tsLock) return _ts != null && !_ts.Closed; } }

    /// <summary>The oldest moment in the rewind buffer.</summary>
    public DateTime BufferOldestUtc { get { lock (_tsLock) return _ts?.OldestUtc ?? DateTime.UtcNow; } }

    /// <summary>The moment of the broadcast on screen right now (null when watching live directly).</summary>
    public DateTime? PositionUtc
    {
        get
        {
            lock (_tsLock)
            {
                if (_source == LiveSource.Direct) return null;
                var pos = _tsBaseUtc + _tsClock.Elapsed;
                return pos > DateTime.UtcNow ? DateTime.UtcNow : pos;
            }
        }
    }

    /// <summary>
    /// Plays the provider's catch-up archive from <paramref name="startUtc"/> (minute precision) – used to go
    /// further back than the rewind buffer. The buffer is dropped: one connection to the provider at a time.
    /// </summary>
    public void PlayArchive(string url, DateTime startUtc)
    {
        int gen = Interlocked.Increment(ref _gen);
        var s = SettingsStore.Current;
        var cache = Math.Clamp(s.NetworkCachingMs, 300, 10000);
        var opts = new[] { $":network-caching={cache}", ":adaptive-maxbuffer=12000" }.Concat(SoundOptions()).ToArray();
        Enqueue(() =>
        {
            if (gen != _gen) return;
            long audio = Player.AudioDelay, spu = Player.SpuDelay;
            EndTimeshift();
            lock (_tsLock) { _source = LiveSource.Archive; _tsBaseUtc = startUtc; _tsClock.Reset(); }
            GpuDecoding = null;
            _playingSince = DateTime.MaxValue;
            using var media = new Media(Vlc, new Uri(url), opts);
            Player.Play(media);
            Player.Volume = s.Volume;
            ApplyPictureNow();
            if (audio != 0) Player.SetAudioDelay(audio);
            if (spu != 0) Player.SetSpuDelay(spu);
        });
    }

    /// <summary>Jumps to a moment inside the rewind buffer.</summary>
    public void SeekBuffer(DateTime utc)
    {
        TimeshiftSession ts;
        lock (_tsLock) ts = _ts;
        if (ts != null) PlayFromBuffer(ts, utc);
    }

    /// <summary>How far behind live the picture is (zero when watching live).</summary>
    public TimeSpan BehindLive
    {
        get
        {
            lock (_tsLock)
            {
                var latest = _source == LiveSource.Buffer && _ts != null ? _ts.LatestUtc
                           : _source == LiveSource.Archive ? DateTime.UtcNow : (DateTime?)null;
                if (latest == null) return TimeSpan.Zero;
                var behind = latest.Value - (_tsBaseUtc + _tsClock.Elapsed);
                return behind < TimeSpan.Zero ? TimeSpan.Zero : behind;
            }
        }
    }

    /// <summary>How much of the channel is in the buffer.</summary>
    public TimeSpan Buffered { get { lock (_tsLock) return _ts == null ? TimeSpan.Zero : _ts.LatestUtc - _ts.OldestUtc; } }

    /// <summary>Moves back (negative) or forward through the live buffer.</summary>
    public void JumpLive(double seconds)
    {
        TimeshiftSession ts;
        DateTime target;
        lock (_tsLock)
        {
            ts = _ts;
            if (ts == null) return;
            var pos = _tsBaseUtc + _tsClock.Elapsed;
            target = pos.AddSeconds(seconds);
            var oldest = ts.OldestUtc;
            if (target < oldest) target = oldest;
            if (target >= ts.LatestUtc.AddSeconds(-3)) { GoLive(); return; }
        }
        PlayFromBuffer(ts, target);
    }

    /// <summary>Catches up to the live picture.</summary>
    public void GoLive()
    {
        TimeshiftSession ts;
        lock (_tsLock) ts = _ts;
        if (ts == null) return;
        PlayFromBuffer(ts, ts.LatestUtc.AddSeconds(-1));
    }

    void PlayFromBuffer(TimeshiftSession ts, DateTime at)
    {
        int gen = Interlocked.Increment(ref _gen);
        var (offset, utc) = ts.Locate(at);
        var opts = LiveOptions(local: true);
        Enqueue(() =>
        {
            if (gen != _gen) return;
            lock (_tsLock)
            {
                if (_ts != ts) return;
                _source = LiveSource.Buffer;
                _tsBaseUtc = utc;
                _tsClock.Reset();
            }
            long audio = Player.AudioDelay, spu = Player.SpuDelay; // keep your sync settings across jumps
            using var media = new Media(Vlc, new Uri(LocalStreamServer.UrlFor(ts, offset)), opts);
            Player.Play(media);
            Player.Volume = SettingsStore.Current.Volume;
            ApplyPictureNow();
            if (audio != 0) Player.SetAudioDelay(audio);
            if (spu != 0) Player.SetSpuDelay(spu);
        });
    }

    string[] LiveOptions(bool local)
    {
        var s = SettingsStore.Current;
        // From the local buffer there's nothing to wait for, so a short cache is enough (and uses less memory).
        var cache = local ? 800 : Math.Clamp(s.NetworkCachingMs, 300, 10000);
        return new[] { $":network-caching={cache}", $":live-caching={cache}", ":adaptive-maxbuffer=12000" }.Concat(SoundOptions()).ToArray();
    }

    // ------------------------------------------------------------------ picture & sound

    /// <summary>The screen's shape for "Fill screen" cropping, e.g. "16:9" (set by the main window).</summary>
    public string FillRatio { get; set; } = "16:9";

    static IEnumerable<string> SoundOptions()
    {
        var s = SettingsStore.Current;
        if (s.NightMode)
        {
            // Dynamic range compression: quiet dialogue up, explosions down.
            yield return ":audio-filter=compressor";
            yield return ":compressor-rms-peak=0.2";
            yield return ":compressor-attack=25";
            yield return ":compressor-release=100";
            yield return ":compressor-threshold=-20";
            yield return ":compressor-ratio=8";
            yield return ":compressor-knee=4.5";
            yield return ":compressor-makeup-gain=10";
        }
        if (!string.IsNullOrEmpty(s.AudioLanguage)) yield return $":audio-language={s.AudioLanguage}";
        if (!string.IsNullOrEmpty(s.SubtitleLanguage)) yield return $":sub-language={s.SubtitleLanguage}";
    }

    void ApplyPictureNow()
    {
        var s = SettingsStore.Current;
        try
        {
            Player.AspectRatio = string.IsNullOrEmpty(s.AspectRatio) ? null : s.AspectRatio;
            Player.CropGeometry = s.Crop == "fill" ? FillRatio : string.IsNullOrEmpty(s.Crop) ? null : s.Crop;
        }
        catch (Exception ex) { Log.Error(ex); }
    }

    /// <summary>Re-applies the aspect ratio and crop settings to what's playing.</summary>
    public void ApplyPicture() => Enqueue(ApplyPictureNow);

    public long AudioDelayMs => Player.AudioDelay / 1000;
    public long SubtitleDelayMs => Player.SpuDelay / 1000;
    public void SetAudioDelay(long ms) => Enqueue(() => Player.SetAudioDelay(ms * 1000));
    public void SetSubtitleDelay(long ms) => Enqueue(() => Player.SetSpuDelay(ms * 1000));

    void EndTimeshift()
    {
        TimeshiftSession old;
        lock (_tsLock) { old = _ts; _ts = null; _tsClock.Reset(); _source = LiveSource.Direct; }
        old?.Dispose();
    }

    void Enqueue(Action a)
    {
        lock (_qLock)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try { a(); } catch (Exception ex) { Log.Error(ex); }
            }, TaskScheduler.Default);
        }
    }

    /// <param name="startMs">Start position for films/episodes (resume); 0 = from the beginning.</param>
    public void Play(string url, bool live, long startMs = 0)
    {
        var s = SettingsStore.Current;
        var cache = Math.Clamp(s.NetworkCachingMs, 300, 10000);
        var opts = new List<string> { $":network-caching={cache}" };
        if (live) opts.Add($":live-caching={cache}");
        // HLS/DASH streams read up to 30 s ahead by default – at 4K that's ~100 MB. 12 s is plenty.
        opts.Add(":adaptive-maxbuffer=12000");
        if (startMs > 0) opts.Add($":start-time={startMs / 1000.0:0.0}".Replace(',', '.'));
        opts.AddRange(SoundOptions());

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException("Invalid stream address.");
        int gen = Interlocked.Increment(ref _gen);
        bool buffer = live && s.Timeshift && TimeshiftSession.Eligible(url);

        Enqueue(() =>
        {
            if (gen != _gen) return; // you've already moved on to something else
            EndTimeshift();          // one connection to the provider at a time
            if (buffer)
            {
                var ts = TimeshiftSession.TryStart(url, s.TimeshiftMinutes, TimeSpan.FromSeconds(10));
                if (gen != _gen) { ts?.Dispose(); return; }
                if (ts != null)
                {
                    lock (_tsLock) { _ts = ts; _tsBaseUtc = ts.OldestUtc; _tsClock.Reset(); _source = LiveSource.Buffer; }
                    uri = new Uri(LocalStreamServer.UrlFor(ts, 0));
                    opts = LiveOptions(local: true).ToList();
                }
            }
            GpuDecoding = null;
            _playingSince = DateTime.MaxValue;
            Player.EnableHardwareDecoding = s.HardwareDecoding;
            using var media = new Media(Vlc, uri, opts.ToArray());
            Player.Play(media);
            Player.Volume = s.Volume;
            ApplyPictureNow();
        });
    }

    public void Stop()
    {
        Interlocked.Increment(ref _gen);
        Enqueue(() => { Player.Stop(); EndTimeshift(); });
    }
    public void TogglePause() => Enqueue(() => Player.Pause());

    public void SeekBy(long ms) => Enqueue(() =>
    {
        if (!Player.IsSeekable) return;
        var len = Player.Length;
        var t = Math.Max(0, Player.Time + ms);
        if (len > 0) t = Math.Min(t, len - 1000);
        Player.Time = t;
    });

    public void SeekTo(long ms) => Enqueue(() => { if (Player.IsSeekable) Player.Time = Math.Max(0, ms); });

    public void SetVolume(int v) => Enqueue(() => Player.Volume = Math.Clamp(v, 0, 100));
    public void SetMute(bool m) => Enqueue(() => Player.Mute = m);

    /// <summary>Reads stream statistics off the UI thread.</summary>
    public Task<StreamHealth> GetHealthAsync() => Task.Run(() =>
    {
        uint w = 0, h = 0;
        try { Player.Size(0, ref w, ref h); } catch { }
        int shown = 0, lost = 0;
        double mbps = 0;
        try
        {
            using var m = Player.Media;
            if (m != null)
            {
                var st = m.Statistics;
                shown = st.DisplayedPictures;
                lost = st.LostPictures;
                // libVLC reports bitrate in kilobytes per millisecond-ish units: ×8 gives Mbit/s.
                mbps = Math.Max(st.InputBitrate, st.DemuxBitrate) * 8.0;
            }
        }
        catch { }
        var state = Player.State;
        if (state == VLCState.Playing && _playingSince == DateTime.MaxValue) _playingSince = DateTime.UtcNow;
        // No "hardware decoding" announcement a few seconds into playback = the CPU is decoding.
        if (GpuDecoding == null && shown > 0 && DateTime.UtcNow - _playingSince > TimeSpan.FromSeconds(3)) GpuDecoding = false;
        return new StreamHealth(w, h, Player.Fps, mbps, shown, lost, Player.Time, Player.Length, state, GpuDecoding);
    });

    public List<TrackOption> AudioTracks()
    {
        var cur = Player.AudioTrack;
        return Player.AudioTrackDescription
            .Where(t => t.Id >= 0)
            .Select(t => new TrackOption(t.Id, t.Name ?? ("Track " + t.Id), t.Id == cur))
            .ToList();
    }

    public List<TrackOption> SubtitleTracks()
    {
        var cur = Player.Spu;
        return Player.SpuDescription
            .Select(t => new TrackOption(t.Id, t.Id < 0 ? "Off" : (t.Name ?? ("Subtitle " + t.Id)), t.Id == cur))
            .ToList();
    }

    public void SetAudioTrack(int id) => Enqueue(() => Player.SetAudioTrack(id));
    public void SetSubtitle(int id) => Enqueue(() => Player.SetSpu(id));
}
