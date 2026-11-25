package app.dingus.tv

import android.app.Activity
import android.os.Handler
import android.os.Looper
import android.util.TypedValue
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.annotation.OptIn
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Live TV playback, shared by the guide's preview window and full screen: it's the same player and the same
 * stream, so going between them never loads the channel twice. In full screen: up/down change channel,
 * left/right rewind and go forward (15 minutes are kept), play/pause pauses, OK shows the info bar, MENU has the
 * options.
 */
@OptIn(UnstableApi::class)
class LiveTv(private val a: Activity, private val video: PlayerView, private val overlay: View) {
    private val handler = Handler(Looper.getMainLooper())
    private var player: ExoPlayer? = null
    private val info: View = overlay.findViewById(R.id.info)
    private val message: TextView = overlay.findViewById(R.id.message)
    private val statsLine: TextView = overlay.findViewById(R.id.stats)
    private val statsBadge: TextView = overlay.findViewById(R.id.statsBadge)
    private var dropped = 0
    private var lastBytes = -1L
    private var lastFrames = -1
    private var lastSample = 0L
    private var mbps = 0.0
    private var measuredFps = 0.0
    private val shift: TextView = overlay.findViewById(R.id.shift)

    /** The channels up/down move through, and which one is playing. */
    var list: List<Channel> = emptyList(); private set
    var index = 0; private set
    val current: Channel? get() = if (player != null) list.getOrNull(index) else null
    /** The stream actually playing for [current] (a version or another source), when it isn't the channel itself. */
    val playing: Channel? get() = if (player != null) version ?: current else null
    var fullScreen = false
        private set(v) {
            field = v
            // Full screen: the spinner while a channel tunes or catches up (the preview says "Loading…" instead).
            video.setShowBuffering(if (v) androidx.media3.ui.PlayerView.SHOW_BUFFERING_WHEN_PLAYING else androidx.media3.ui.PlayerView.SHOW_BUFFERING_NEVER)
            applyPicture()
        }

    /** Full screen: the channel's own picture size (≡ → Picture size); the guide's window always fits. */
    private fun applyPicture() = PictureSize.apply(video, if (fullScreen) PictureSize.get(list.getOrNull(index)?.id) else "fit")
    /** Told when the channel changes in full screen (so the guide can follow). */
    var onChannel: ((Channel) -> Unit)? = null

    private var retries = 0
    private var triedBackup = false // (a backup server has been tried for this stream)
    private var guess = 0 // the stream didn't say what it is: 1 = try it as HLS, 2 = as DASH (Players.item)
    private var versionTry = 0
    private var slotWaited = false // already waited for a free connection for this channel
    private var slotGen = 0         // bumps whenever a channel starts or the player goes, so an old wait stops
    private var version: Channel? = null
    private var zapPending: Runnable? = null
    private val hideInfo = Runnable {
        info.animate().alpha(0f).setStartDelay(0).setInterpolator(android.view.animation.AccelerateDecelerateInterpolator())
            .setDuration(if (Ui.lite) 0 else 250).withEndAction { info.visibility = View.GONE; info.translationY = 0f }
    }

    /** Whether the info bar is on screen (and not already on its way out). */
    val infoShowing: Boolean get() = info.visibility == View.VISIBLE && info.alpha > 0.3f
    private val retry = Runnable { startChannel() }

    /**
     * A stream that stops moving (the picture frozen, no error from the player – it happens with some providers after
     * a few minutes, or when a connection's quietly dropped): after 12 seconds with nothing new, start it again, at live.
     */
    private var lastPos = -1L
    private var lastBuffered = -1L
    private var stuckFor = 0
    private val watchdog = object : Runnable {
        override fun run() {
            val p = player
            if (p != null) {
                val active = p.playWhenReady && (p.playbackState == Player.STATE_READY || p.playbackState == Player.STATE_BUFFERING)
                val pos = p.currentPosition; val buf = p.bufferedPosition
                if (active && pos == lastPos && buf == lastBuffered) stuckFor++ else stuckFor = 0
                lastPos = pos; lastBuffered = buf
                if (stuckFor >= 4) {
                    stuckFor = 0
                    Ui.test("stall-restart ${current?.name}")
                    retries = 0
                    if (!switchVersion("stopped")) startChannel() // a merged channel: its next version instead
                }
            }
            handler.postDelayed(this, 3_000)
        }
    }

    private var buffer: LiveBuffer? = null
    private var behindMs = 0L
    private var pausedAt = 0L
    private val shiftTick = object : Runnable {
        override fun run() { showShift(); handler.postDelayed(this, 1_000) }
    }

    // ------------------------------------------------------------------ playing

    /** Plays [c] (from [channels], for up/down). Does nothing if it's already playing. */
    fun play(channels: List<Channel>, c: Channel) {
        // (Work out what's playing before switching to the new list – after, the old position points somewhere else.)
        val playingSame = player != null && current?.id == c.id
        val i = channels.indexOfFirst { it.id == c.id }
        list = if (i >= 0) channels else listOf(c) + channels
        index = if (i >= 0) i else 0
        if (playingSame) return
        version = null; versionTry = 0; retries = 0; triedBackup = false; guess = 0; slotWaited = false; playedOnce = false; rebuffers.clear()
        makePlayer()
        startChannel()
        startWatchdog()
    }

    // While anything's playing (the guide's window too), the Fire TV's screensaver mustn't start: it comes on after
    // 5 minutes without a button press and stops the stream behind it.
    private fun startWatchdog() {
        a.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON); stuckFor = 0; lastPos = -1; handler.removeCallbacks(watchdog); handler.postDelayed(watchdog, 3_000) }

    /** Back to the screen while watching: start the same channel again. */
    fun resume() {
        if (player != null || list.isEmpty()) return
        makePlayer()
        startChannel()
        startWatchdog()
        if (fullScreen) { handler.removeCallbacks(statsTick); handler.post(statsTick) }
    }

    /** Stops and lets go of the stream (leaving the screen). */
    fun release() {
        Streams.remove(stream)
        mini.hide()
        if (player != null) Account.soon()
        handler.removeCallbacksAndMessages(null)
        slotGen++
        a.window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        player?.release()
        player = null
        video.player = null
        video.videoSurfaceView?.visibility = View.VISIBLE // (an HDR preview hides it; the next channel needs it back)
        sdrPreview = false
        previewTexture?.visibility = View.GONE
        buffer?.close()
        buffer = null
        message.text = ""
        shift.visibility = View.GONE
        // (Its timers were just cancelled: the info bar would otherwise stay up when you come back.)
        info.animate().cancel(); info.visibility = View.GONE
        statsBadge.visibility = View.GONE
    }

    // ------------------------------------------------------------------ HDR channels in the guide's preview

    /** The small preview of an HDR channel is drawn here instead (see [updateSurface]). */
    private var previewTexture: android.view.TextureView? = null
    private var sdrPreview = false

    private fun isHdr(p: ExoPlayer): Boolean {
        val t = p.videoFormat?.colorInfo?.colorTransfer
        return t == androidx.media3.common.C.COLOR_TRANSFER_ST2084 || t == androidx.media3.common.C.COLOR_TRANSFER_HLG
    }

    /**
     * An HDR channel shown on the TV's video layer switches the whole screen into HDR, which turns the guide
     * around a small preview grey. So the preview of an HDR channel is drawn like the rest of the screen (no
     * switch), and full screen goes back to the video layer for proper HDR.
     */
    private fun updateSurface() {
        val p = player ?: return
        // In full screen an HDR channel uses the TV's video layer (HDR) only if dynamic range matching is on.
        val want = isHdr(p) && (!fullScreen || !Store.matchDynamicRange)
        if (want == sdrPreview) return
        sdrPreview = want
        if (want) {
            val tv = previewTexture ?: android.view.TextureView(a).also {
                video.addView(it, android.widget.FrameLayout.LayoutParams(android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.MATCH_PARENT))
                previewTexture = it
            }
            tv.visibility = View.VISIBLE
            video.player = null
            // Hide the video's surface too: left on screen, the TV can stay in HDR mode and the guide looks washed out.
            video.videoSurfaceView?.visibility = View.GONE // (the texture sits inside the player view, so only its surface goes)
            p.setVideoTextureView(tv)
            Ui.test("preview-sdr")
        } else {
            previewTexture?.let { p.clearVideoTextureView(it); it.visibility = View.GONE }
            video.videoSurfaceView?.visibility = View.VISIBLE
            video.player = p
        }
    }

    /** ▼ in full screen: the mini guide over the picture; OK on a channel there watches it. */
    private val mini by lazy {
        MiniGuide(a, overlay as android.widget.FrameLayout) { c ->
            val i = list.indexOfFirst { it.id == c.id }
            if (i >= 0 && i != index) {
                version = null; versionTry = 0; slotWaited = false; retries = 0; rebuffers.clear(); guess = 0; triedBackup = false
                pendingJump = -1; handler.removeCallbacks(doJump)
                index = i
                onChannel?.invoke(list[index])
                startChannel()
            }
            showInfo()
        }
    }

    /** The VPN or network is about to change: close this stream cleanly, then start it again afterwards (Streams). */
    private val stream = Streams.Playing(
        close = {
            slotGen++; handler.removeCallbacks(retry)
            player?.stop()
            buffer?.close(); buffer = null
            if (player != null) message.text = if (fullScreen) "Reconnecting…" else "Loading…"
        },
        reopen = { if (player != null) { retries = 0; startChannel() } },
    )

    private fun makePlayer() {
        if (player != null) return
        Streams.add(stream)
        val p = Players.create(a)
        dropped = 0; lastBytes = -1; lastFrames = -1; mbps = 0.0; measuredFps = 0.0
        p.addAnalyticsListener(object : androidx.media3.exoplayer.analytics.AnalyticsListener {
            override fun onDroppedVideoFrames(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime, droppedFrames: Int, elapsedMs: Long) { dropped += droppedFrames }
        })
        p.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_BUFFERING -> onBuffering()
                    Player.STATE_READY -> {
                        handler.removeCallbacks(bufferCheck); playedOnce = true
                        retries = 0; triedBackup = false
                        message.text = ""
                        val name = list.getOrNull(index)?.name
                        Ui.test(if (fullScreen) "playing $name ${p.videoSize.width}x${p.videoSize.height}" else "preview $name")
                        Account.soon()
                    }
                    Player.STATE_ENDED -> scheduleRetry("Reconnecting…") // a live stream "ending" means the connection dropped
                    else -> {}
                }
            }

            override fun onTracksChanged(tracks: Tracks) {
                if (fullScreen) p.videoFormat?.frameRate?.let { FrameRate.match(a, it) }
                updateSurface()
            }

            override fun onVideoSizeChanged(size: androidx.media3.common.VideoSize) {
                updateSurface()
                // Note what this version really plays at, so the best one is tried first next time.
                playing?.let { c -> Merge.record(c, size.height, p.videoFormat?.frameRate?.takeIf { it > 1 }?.let { Math.round(it) } ?: 0) }
            }

            override fun onPlayerError(error: PlaybackException) {
                Ui.test("error ${error.errorCodeName}")
                if (error.errorCode == PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW) { p.seekToDefaultPosition(); p.prepare(); return }
                // An address that doesn't say what it is (no ".m3u8" but really HLS, say): try the other kinds before retrying.
                if (Players.unrecognised(error) && guess < 2) { guess++; Ui.test("format-guess $guess"); startChannel(); return }
                if (retries < 3) { scheduleRetry("Reconnecting…"); return }
                // The server can't be reached: try the playlist's backup server addresses, if it has any.
                if (!triedBackup && Failover.hasBackups() && Players.serverDown(error)) {
                    triedBackup = true
                    message.text = "Trying a backup server…"
                    Thread {
                        val ok = Failover.recover()
                        handler.post { if (ok) { retries = 0; startChannel() } else giveUp(error) }
                    }.start()
                    return
                }
                giveUp(error)
            }
        })
        video.player = p
        player = p
    }

    // ------------------------------------------------------------------ merged channels: buffering → next version

    /**
     * A merged channel that keeps buffering moves to its next version by itself (and says so): buffering for
     * 5 seconds while playing, not starting within 15, or buffering 3 times in a minute. Each version is tried once.
     */
    private var playedOnce = false
    private val rebuffers = ArrayList<Long>()
    private val bufferCheck = Runnable {
        if (player?.playbackState == Player.STATE_BUFFERING && player?.playWhenReady == true && behindMs == 0L && System.currentTimeMillis() >= quietUntil)
            switchVersion(if (playedOnce) "kept buffering" else "wouldn't start")
    }

    /** Until when buffering is expected (just rewound, paused or caught up): not a reason to change version. */
    private var quietUntil = 0L

    private fun onBuffering() {
        val p = player ?: return
        if (playedOnce && p.playWhenReady && behindMs == 0L && System.currentTimeMillis() >= quietUntil)
            list.getOrNull(index)?.let { SlowVpn.onBuffering(a, "live:${it.id}") }
        if (!p.playWhenReady || !hasOtherVersion()) return
        if (behindMs > 0 || buffer != null && pausedAt > 0 || System.currentTimeMillis() < quietUntil) return
        handler.removeCallbacks(bufferCheck)
        handler.postDelayed(bufferCheck, if (playedOnce) 5_000 else 15_000)
        if (!playedOnce) return
        val now = System.currentTimeMillis()
        rebuffers.add(now); rebuffers.removeAll { now - it > 60_000 }
        if (rebuffers.size >= 3) { rebuffers.clear(); switchVersion("kept buffering") }
    }

    /** What to try for a channel, in order: its versions (merged channels), then the same channel elsewhere with the same guide. */
    private fun tries(c: Channel): List<Channel> =
        ((if (Store.mergeChannels) Repo.versions(c) else listOf(c)) + Merge.order(Repo.sameAs(c))).distinctBy { it.id }

    private fun hasOtherVersion(): Boolean =
        list.getOrNull(index)?.let { tries(it).size > versionTry + 1 } == true

    /** Moves to the merged channel's next version; false when there's no other one left to try. */
    private fun switchVersion(why: String): Boolean {
        val c = list.getOrNull(index) ?: return false
        val vs = tries(c)
        if (vs.size <= versionTry + 1) return false
        val next = vs[++versionTry]
        version = next; retries = 0
        handler.removeCallbacks(bufferCheck); rebuffers.clear(); playedOnce = false
        Ui.test("version-switch $why ${next.name}")
        message.text = if (next.categoryId != c.categoryId && Repo.sourceKey(next.name) == Repo.sourceKey(c.name) && Merge.key(next) != Merge.key(c))
            "This channel $why – switched to ${next.name}  (${Repo.categoryName(next.categoryId)})"
        else "That version $why – switched to ${versionLabel(next)}"
        val shown = message.text
        handler.postDelayed({ if (message.text == shown) message.text = "" }, 4_000)
        startChannel()
        return true
    }

    private fun versionLabel(v: Channel) = v.name + "  (" + (if (Merge.isBackup(v)) "backup" else when (Merge.rank(v)) { 4 -> "4K"; 3 -> "FHD"; 1 -> "SD"; else -> "HD" }) + ")"

    /** Merged duplicates: try the channel's next version before giving up. */
    private fun giveUp(error: PlaybackException) {
        val next = list.getOrNull(index)?.let { tries(it).getOrNull(++versionTry) }
        if (next != null) {
            version = next; retries = 0
            message.text = "Trying another version…"
            Ui.test("fallback ${next.name}")
            handler.postDelayed({ startChannel() }, 500)
        } else if (!slotWaited) { slotWaited = true; waitForFreeConnection(error) }
        else showFailed(error)
    }

    private fun showFailed(error: PlaybackException) {
        android.util.Log.w("Dingus", "Channel not playing: ${error.errorCodeName}", error)
        message.text = if (fullScreen) "This channel isn't playing right now.\n${describe(error)}\nPress ▲ or ▼ for another channel." else "Not playing\n${describe(error)}"
    }

    /**
     * The channel won't start. Providers count a connection as in use until their side notices it's gone, so one that
     * was cut off (switching VPN, the Wi-Fi dropping) can keep a 1-connection plan "full" for several minutes. If the
     * provider says every connection is in use, wait for one to be freed (checking every 15 s, for up to 8 minutes)
     * and then start the channel, instead of giving up.
     */
    private fun waitForFreeConnection(error: PlaybackException) {
        val pl = Store.playlist ?: return showFailed(error)
        player?.stop() // so our own attempt isn't one of the ones counted
        buffer?.close(); buffer = null
        message.text = "Checking your connections…"
        val gen = ++slotGen
        Background.scope.launch {
            val until = System.currentTimeMillis() + 8 * 60_000
            var first = true
            while (gen == slotGen && player != null) {
                val info = try { Account.refresh(pl) } catch (e: Exception) { null }
                if (gen != slotGen || player == null) return@launch
                if (info == null || info.maxConnections <= 0) { showFailed(error); return@launch }
                if (info.activeConnections < info.maxConnections) {
                    Ui.test("connection free ${info.activeConnections}/${info.maxConnections}")
                    retries = 0; startChannel(); return@launch
                }
                if (first) Ui.test("waiting for a free connection ${info.activeConnections}/${info.maxConnections}")
                first = false
                message.text = "Waiting for a free connection  ·  ${info.activeConnections} of ${info.maxConnections} in use\n" +
                    (if (info.maxConnections == 1) "Your plan allows one connection. If another TV or phone is watching with this login, stop it there; otherwise it frees up by itself in a few minutes."
                     else "If other TVs or phones are watching with this login, stop one there; otherwise it frees up by itself in a few minutes.")
                if (System.currentTimeMillis() > until) { showFailed(error); return@launch }
                delay(15_000)
            }
        }
    }

    private fun describe(e: PlaybackException) = Players.plain(e)

    private fun scheduleRetry(text: String) {
        retries++
        message.text = text
        handler.removeCallbacks(retry)
        handler.postDelayed(retry, 1_500L * retries)
    }

    private fun startChannel() {
        val p = player ?: return
        // The VPN dropped and the kill switch is on: nothing plays until it's back (Streams reopens this then).
        if (Vpn.holding) { p.stop(); message.text = "Waiting for the VPN to reconnect…"; Ui.test("vpn-holding"); return }
        slotGen++
        playedOnce = false; handler.removeCallbacks(bufferCheck)
        applyPicture()
        dropped = 0 // the player is reused between channels, so the count starts again for each channel
        val c = version ?: list.getOrNull(index)?.let { if (Store.mergeChannels) Repo.versions(it).first() else it } ?: return
        Ui.test("load ${c.name}")
        message.text = if (fullScreen) "" else "Loading…"
        buffer?.close()
        buffer = null
        behindMs = 0; pausedAt = 0
        val url = Players.liveUrl(c)
        // MPEG-TS channels go through the pause/rewind buffer (so full screen can rewind straight away); HLS plays directly.
        val b = if (Store.timeshift && guess == 0 && Players.typeOf(url) == null) try { LiveBuffer(url, a.cacheDir, 15 * 60).start() } catch (e: Exception) { null } else null
        buffer = b
        p.setMediaItem(if (b != null) Players.item(b.urlAt(0)) else Players.item(url, guess))
        p.prepare()
        p.playWhenReady = true
        if (fullScreen) Store.noteWatched(list[index].id)
        handler.removeCallbacks(shiftTick)
        if (b != null) handler.post(shiftTick)
        showShift()
    }

    // ------------------------------------------------------------------ full screen

    /** Set by the screen when the picture is growing into full screen (so the info bar waits for it). */
    var animatedEnter = false

    fun enterFullScreen() {
        fullScreen = true
        updateSurface()
        a.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        message.setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
        list.getOrNull(index)?.let { Store.noteWatched(it.id); Ui.test("fullscreen ${it.name}") }
        player?.videoFormat?.frameRate?.let { FrameRate.match(a, it) }
        showShift()
        showInfo(delay = if (animatedEnter) 240 else 0)
        handler.removeCallbacks(statsTick); handler.post(statsTick)
    }

    fun leaveFullScreen() {
        mini.hide()
        fullScreen = false
        updateSurface()
        handler.removeCallbacks(statsTick)
        statsBadge.visibility = View.GONE
        if (player == null) a.window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON) // (still on while the window plays)
        FrameRate.reset(a)
        message.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        pendingJump = -1; handler.removeCallbacks(doJump)
        handler.removeCallbacks(hideInfo)
        info.animate().cancel()
        if (!Ui.lite && infoShowing) {
            // The info bar fades first (120 ms), then the picture shrinks.
            info.animate().alpha(0f).setStartDelay(0).setInterpolator(android.view.animation.AccelerateDecelerateInterpolator())
                .setDuration(120).withEndAction { info.visibility = View.GONE; info.translationY = 0f }
        } else { info.visibility = View.GONE; info.translationY = 0f }
        // Paused or behind live: the preview carries on from live.
        if (pausedAt > 0 || behindMs > 0) { pausedAt = 0; if (buffer != null) seekBehind(0) else player?.playWhenReady = true }
        shift.visibility = View.GONE
    }

    /** BACK in full screen: hides the info bar first (true = handled here). */
    fun onBack(): Boolean {
        if (mini.showing) { mini.hide(); return true }
        if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run(); return true }
        return false
    }

    fun onKey(keyCode: Int, event: KeyEvent): Boolean {
        val handled = keyCode in HANDLED
        if (mini.showing && keyCode != KeyEvent.KEYCODE_BACK && mini.onKey(keyCode, event)) return true
        if (event.action != KeyEvent.ACTION_DOWN) return handled
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP -> zap(-1)
            // ▼: the mini guide (the channel down button still changes channel straight away).
            KeyEvent.KEYCODE_DPAD_DOWN -> if (event.repeatCount == 0) {
                handler.removeCallbacks(hideInfo); if (info.visibility == View.VISIBLE) hideInfo.run()
                mini.show(list, index)
            }
            KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN -> zap(1)
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_INFO ->
                if (event.repeatCount == 0) {
                    // OK: the full info; OK again hides it.
                    if (info.visibility == View.VISIBLE && info.alpha > 0.5f && infoFull) { handler.removeCallbacks(hideInfo); hideInfo.run() } else showInfo(full = true)
                }
            KeyEvent.KEYCODE_MENU -> if (event.repeatCount == 0) options()
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY -> if (event.repeatCount == 0) togglePause()
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> if (buffer != null) {
                // The rewind button: 10 seconds a press. Holding ◀: faster after a moment.
                val step = if (keyCode != KeyEvent.KEYCODE_MEDIA_REWIND && event.repeatCount > 6) 30_000L else 10_000L
                pendingJump = (if (pendingJump < 0) currentBehind() else pendingJump) + step
                scheduleJump()
            }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> if (buffer != null) {
                // The fast forward button: 10 seconds a press, up to live.
                val step = if (keyCode != KeyEvent.KEYCODE_MEDIA_FAST_FORWARD && event.repeatCount > 6) 30_000L else 10_000L
                pendingJump = maxOf(0, (if (pendingJump < 0) currentBehind() else pendingJump) - step)
                scheduleJump()
            }
            else -> return false
        }
        return true
    }

    /** Changing channel: the info bar updates straight away; the stream switches once you stop pressing. */
    private fun zap(step: Int) {
        if (list.size < 2) return
        version = null; versionTry = 0; slotWaited = false; rebuffers.clear(); guess = 0; triedBackup = false
        pendingJump = -1; handler.removeCallbacks(doJump)
        index = (index + step + list.size) % list.size
        retries = 0
        showInfo()
        onChannel?.invoke(list[index])
        zapPending?.let { handler.removeCallbacks(it) }
        val r = Runnable { startChannel() }
        zapPending = r
        handler.postDelayed(r, 350)
    }

    /** True while the full info (OK pressed) is showing, rather than the short one. */
    private var infoFull = false

    /**
     * The info bar. Opening a stream or changing channel shows the channel, what's on with its times and progress,
     * its description and the stream stats; OK adds what's next and the keys.
     */
    fun showInfo(full: Boolean = false, delay: Long = 0) {
        val c = list.getOrNull(index) ?: return
        infoFull = full
        overlay.findViewById<TextView>(R.id.name).text = c.name
        overlay.findViewById<TextView>(R.id.number).text = if (Store.showNumbers && c.number > 0) c.number.toString() else ""
        overlay.findViewById<View>(R.id.fav).visibility = if (Store.isFavourite(c.id)) View.VISIBLE else View.GONE
        overlay.findViewById<TextView>(R.id.initials).text = Ui.initials(c.name)
        Logos.load(overlay.findViewById<ImageView>(R.id.logo), c.logo, Ui.dp(a, 56), overlay.findViewById(R.id.initials))
        val (now, next) = Repo.nowNext(c)
        val nowV = overlay.findViewById<TextView>(R.id.now)
        val times = overlay.findViewById<TextView>(R.id.times)
        val prog = overlay.findViewById<ProgressBar>(R.id.progress)
        val desc = overlay.findViewById<TextView>(R.id.desc)
        val nextV = overlay.findViewById<TextView>(R.id.next)
        nowV.text = now?.title ?: "No guide information"
        times.text = now?.let { Ui.span(it) + if (Repo.canStartOver(c, it)) "   ·   ≡ Start over" else "" } ?: ""
        now?.let { prog.progress = Ui.progress(it) }
        (times.parent as View).visibility = if (now != null) View.VISIBLE else View.GONE // (the times and progress bar always show)
        desc.text = now?.desc ?: ""
        desc.visibility = if (desc.text.isNotEmpty()) View.VISIBLE else View.GONE
        nextV.text = next?.let { "Next  ${Ui.time(it.start)}  ${it.title}" } ?: ""
        nextV.visibility = if (full && nextV.text.isNotEmpty()) View.VISIBLE else View.GONE
        overlay.findViewById<View>(R.id.keys).visibility = if (full) View.VISIBLE else View.GONE
        updateStats()
        info.animate().cancel()
        if (!Ui.lite && (info.visibility != View.VISIBLE || info.alpha < 0.5f)) {
            // Fades in with a small rise (when it's already showing it just stays, with the new details).
            info.alpha = 0f
            info.translationY = Ui.dp(a, 12).toFloat()
            info.visibility = View.VISIBLE
            info.animate().alpha(1f).translationY(0f).setStartDelay(delay)
                .setInterpolator(android.view.animation.DecelerateInterpolator()).setDuration(220).start()
        } else {
            info.alpha = 1f
            info.translationY = 0f
            info.visibility = View.VISIBLE
        }
        handler.removeCallbacks(hideInfo)
        handler.postDelayed(hideInfo, if (full) 8_000 else 4_000)
        Ui.test(if (full) "info full" else "info short")
    }

    // ------------------------------------------------------------------ pause and rewind

    private fun currentBehind(): Long = behindMs + if (pausedAt > 0) System.currentTimeMillis() - pausedAt else 0

    /** Jump to [ms] behind live (0 = live). */
    private fun seekBehind(ms: Long) {
        val b = buffer ?: return
        val p = player ?: return
        if (b.byteRate <= 0) { message.text = "Rewind is ready in a few seconds"; handler.postDelayed({ message.text = "" }, 1_500); return }
        val max = (b.secondsAvailable * 1000).toLong()
        val target = ms.coerceIn(0, maxOf(0, max - 1_000))
        val lead = if (target < 3_000) 2_000L else target // at "live", start a moment back so it doesn't stall
        quietUntil = System.currentTimeMillis() + 10_000; rebuffers.clear(); handler.removeCallbacks(bufferCheck)
        p.setMediaItem(Players.item(b.urlAt(b.offsetBehind(lead / 1000.0))))
        p.prepare()
        p.playWhenReady = true
        behindMs = if (target < 3_000) 0 else target
        pausedAt = 0
        Ui.test("timeshift behind ${behindMs / 1000}s")
        showShift()
        if (fullScreen) showInfo()
    }

    private fun togglePause() {
        val p = player ?: return
        quietUntil = System.currentTimeMillis() + 10_000; rebuffers.clear(); handler.removeCallbacks(bufferCheck)
        if (buffer == null) { p.playWhenReady = !p.playWhenReady; showShift(); return } // (no rewind: still say it's paused)
        if (pausedAt == 0L) { p.playWhenReady = false; pausedAt = System.currentTimeMillis(); Ui.test("timeshift paused") }
        else {
            val b = buffer!!
            val behind = currentBehind()
            if (behind > b.secondsAvailable * 1000) { pausedAt = 0; seekBehind(behind); return } // paused longer than is kept
            behindMs = behind; pausedAt = 0
            p.playWhenReady = true
        }
        showShift()
    }

    private fun showShift() {
        if (!fullScreen) { shift.visibility = View.GONE; return }
        val behind = currentBehind()
        when {
            buffer == null && player?.playWhenReady == false -> { shift.text = "❚❚  Paused  ·  press ▶❚❚ to carry on"; shift.visibility = View.VISIBLE }
            pausedAt > 0 -> { shift.text = "❚❚  Paused  ·  −${clock(behind)}"; shift.visibility = View.VISIBLE }
            behind >= 3_000 -> { shift.text = "−${clock(behind)}  behind live"; shift.visibility = View.VISIBLE }
            else -> shift.visibility = View.GONE
        }
    }

    private fun clock(ms: Long): String { val s = ms / 1000; return "%d:%02d".format(s / 60, s % 60) }

    private var pendingJump = -1L
    private val doJump = Runnable { val t = pendingJump; pendingJump = -1; if (t >= 0) seekBehind(t) }
    /** Several presses of left/right add up, and the jump happens once you stop pressing. */
    private fun scheduleJump() {
        val b = buffer
        val max = if (b != null) (b.secondsAvailable * 1000).toLong() else 0L
        pendingJump = pendingJump.coerceIn(0, maxOf(0, max))
        shift.text = if (pendingJump < 3_000) "Live" else "−${clock(pendingJump)}"
        shift.visibility = View.VISIBLE
        handler.removeCallbacks(doJump)
        handler.postDelayed(doJump, 600)
    }

    // ------------------------------------------------------------------ options

    /** A different version was chosen for the channel that's playing: switch to it. */
    fun replayVersion() { version = null; versionTry = 0; slotWaited = false; retries = 0; startChannel() }

    /** ≡ → Sources…: plays the same channel from another category (in place of this one). */
    fun playSource(s: Channel) { version = s; versionTry = 0; slotWaited = false; retries = 0; startChannel() }

    private fun options() {
        val c = list.getOrNull(index) ?: return
        val p = player ?: return
        val items = ArrayList<Pair<String, () -> Unit>>()
        val on = Repo.nowNext(c).first
        if (on != null && Repo.canStartOver(c, on)) items.add("↺  Start over  ·  from ${Ui.time(on.start)}" to { startOver(a, c, on) })
        items.add((if (Store.isFavourite(c.id)) "Remove from Favourites" else "Add to Favourites") to { Store.toggleFavourite(c.id); showInfo(infoFull) })
        items.add("Audio & subtitles" to { app.dingus.tv.Tracks.choose(a, p) })
        items.add("Picture size  ·  ${PictureSize.label(PictureSize.get(c.id))}" to { PictureSize.choose(a, c.id, c.name) { applyPicture() } })
        if (currentBehind() >= 3_000) items.add("Back to live" to { seekBehind(0) })
        items.add((if (Store.statsPinned) "Hide stream stats" else "Show stream stats") to {
            Store.statsPinned = !Store.statsPinned; updateStats(); Ui.test("stats-pinned ${Store.statsPinned}")
        })
        items.add("Stream information" to { streamInfo() })
        if (Store.mergeChannels && Repo.versions(c).size > 1) items.add(1, "Choose version…" to { Repo.chooseVersion(a, c) { replayVersion() } })
        Repo.sources(c).size.takeIf { it > 1 }?.let { n -> items.add(minOf(2, items.size), "Sources…  ·  ${n - 1} more" to { Repo.chooseSource(a, c, version ?: c) { playSource(it) } }) }
        Dialogs.builder(a).setTitle(c.name).setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }.show()
    }

    // ------------------------------------------------------------------ stream stats

    /** "4K", "FHD", "HD", or the height ("576p") for anything lower – like the Windows app. */
    private fun resLabel(w: Int, h: Int): String = when {
        h >= 2000 || w >= 3600 -> "4K"
        h >= 1400 -> "1440p"
        h >= 1000 || w >= 1900 -> "FHD"
        h >= 700 || w >= 1260 -> "HD"
        h > 0 -> "${h}p"
        else -> ""
    }

    /** Every 2 seconds in full screen: bitrate and frame rate measured, the stats line and badge redrawn. */
    private val statsTick = object : Runnable {
        override fun run() {
            if (!fullScreen) return
            val p = player
            val now = android.os.SystemClock.elapsedRealtime()
            val bytes = StreamMeter.bytes
            val frames = p?.videoDecoderCounters?.let { it.ensureUpdated(); it.renderedOutputBufferCount } ?: -1
            if (lastBytes >= 0 && now > lastSample) {
                val secs = (now - lastSample) / 1000.0
                val m = (bytes - lastBytes) * 8 / 1_000_000.0 / secs
                mbps = if (mbps <= 0) m else mbps * 0.6 + m * 0.4 // smoothed
                if (frames >= 0 && lastFrames >= 0 && frames >= lastFrames) measuredFps = (frames - lastFrames) / secs
            }
            lastBytes = bytes; lastFrames = frames; lastSample = now
            updateStats()
            handler.postDelayed(this, 2_000)
        }
    }

    private fun statsText(): String {
        val p = player ?: return ""
        val parts = ArrayList<String>()
        val v = p.videoFormat
        val w = p.videoSize.width.takeIf { it > 0 } ?: v?.width ?: 0
        val h = p.videoSize.height.takeIf { it > 0 } ?: v?.height ?: 0
        resLabel(w, h).takeIf { it.isNotEmpty() }?.let { parts.add(it) }
        val fps = v?.frameRate?.takeIf { it > 1 }?.toDouble() ?: measuredFps
        if (fps > 1) parts.add("${Math.round(fps)} fps")
        if (mbps > 0.05) parts.add("%.1f Mbps".format(mbps))
        parts.add(if (dropped == 0) "no dropped frames" else "$dropped dropped")
        if (Vpn.status == Vpn.Status.ON) parts.add("VPN connected")
        return parts.joinToString("  ·  ")
    }

    private fun updateStats() {
        val t = statsText()
        val healthy = (player?.playbackState == Player.STATE_READY) && dropped <= 5
        val dot = if (player?.playbackState == Player.STATE_BUFFERING) 0xFFFBBF24.toInt() else if (healthy) 0xFF4ADE80.toInt() else 0xFFF87171.toInt()
        val sb = android.text.SpannableStringBuilder("●  ").apply {
            setSpan(android.text.style.ForegroundColorSpan(dot), 0, 1, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            append(t)
        }
        statsLine.text = if (t.isEmpty()) "" else sb
        statsLine.visibility = if (t.isEmpty()) View.GONE else View.VISIBLE
        statsBadge.text = sb
        statsBadge.visibility = if (fullScreen && Store.statsPinned && t.isNotEmpty()) View.VISIBLE else View.GONE
        if (t.isNotEmpty()) Ui.test("stats $t")
    }

    private fun streamInfo() {
        val p = player ?: return
        val v = p.videoFormat
        val au = p.audioFormat
        val lines = ArrayList<String>()
        if (v != null) {
            lines.add("Picture: ${v.width} × ${v.height}" + (if (v.frameRate > 0) "  ·  ${"%.2f".format(v.frameRate)} fps" else ""))
            v.sampleMimeType?.let { lines.add("Video: ${it.substringAfter('/').uppercase()}") }
        }
        if (au != null) lines.add("Sound: ${au.sampleMimeType?.substringAfter('/')?.uppercase() ?: "?"}" + (if (au.channelCount > 0) "  ·  ${au.channelCount} channels" else ""))
        buffer?.byteRate?.takeIf { it > 0 }?.let { lines.add("Bitrate: ${"%.1f".format(it * 8 / 1_000_000)} Mbit/s") }
        buffer?.let { lines.add("Rewind available: ${clock((it.secondsAvailable * 1000).toLong())}") }
        list.getOrNull(index)?.let { lines.add("Address: ${Players.liveUrl(version ?: it).replace(Regex("/live/[^/]+/[^/]+/"), "/live/…/")}") }
        if (Vpn.status == Vpn.Status.ON) lines.add("VPN: ${Vpn.connectedTo?.label ?: "on"}")
        Dialogs.builder(a).setTitle("Stream information").setMessage(lines.joinToString("\n")).setPositiveButton("OK", null).show()
    }

    companion object {
        /** Plays what's on now from its beginning (the channel's catch-up); BACK comes back to live TV. */
        fun startOver(a: Activity, c: Channel, p: Programme) {
            val url = Repo.catchUpUrl(c, p) ?: return
            Ui.test("start-over ${c.name} ${p.title}")
            a.startActivity(VodPlayerActivity.intent(a, VodPlayerActivity.CATCHUP, null,
                listOf(VodPlayerActivity.Entry("so:${c.id}:${p.start}", "${c.name}  ·  ${p.title}", url)), 0, true, "START OVER  ·  BACK FOR LIVE TV"))
        }

        private val HANDLED = setOf(
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_INFO,
            KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_UP, KeyEvent.KEYCODE_PAGE_DOWN,
            KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY,
            KeyEvent.KEYCODE_MEDIA_REWIND, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD,
        )
    }
}
