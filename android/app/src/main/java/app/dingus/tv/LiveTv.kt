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
    var fullScreen = false; private set
    /** Told when the channel changes in full screen (so the guide can follow). */
    var onChannel: ((Channel) -> Unit)? = null

    private var retries = 0
    private var versionTry = 0
    private var version: Channel? = null
    private var zapPending: Runnable? = null
    private val hideInfo = Runnable { info.animate().alpha(0f).setDuration(if (Ui.lite) 0 else 250).withEndAction { info.visibility = View.GONE } }
    private val retry = Runnable { startChannel() }

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
        version = null; versionTry = 0; retries = 0
        makePlayer()
        startChannel()
    }

    /** Back to the screen while watching: start the same channel again. */
    fun resume() {
        if (player != null || list.isEmpty()) return
        makePlayer()
        startChannel()
    }

    /** Stops and lets go of the stream (leaving the screen). */
    fun release() {
        if (player != null) Account.soon()
        handler.removeCallbacksAndMessages(null)
        player?.release()
        player = null
        video.player = null
        buffer?.close()
        buffer = null
        message.text = ""
        shift.visibility = View.GONE
    }

    private fun makePlayer() {
        if (player != null) return
        val p = Players.create(a)
        dropped = 0; lastBytes = -1; lastFrames = -1; mbps = 0.0; measuredFps = 0.0
        p.addAnalyticsListener(object : androidx.media3.exoplayer.analytics.AnalyticsListener {
            override fun onDroppedVideoFrames(eventTime: androidx.media3.exoplayer.analytics.AnalyticsListener.EventTime, droppedFrames: Int, elapsedMs: Long) { dropped += droppedFrames }
        })
        p.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_READY -> {
                        retries = 0
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
            }

            override fun onPlayerError(error: PlaybackException) {
                Ui.test("error ${error.errorCodeName}")
                if (error.errorCode == PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW) { p.seekToDefaultPosition(); p.prepare(); return }
                if (retries < 3) { scheduleRetry("Reconnecting…"); return }
                // Merged duplicates: try the channel's next version before giving up.
                val next = list.getOrNull(index)?.let { Repo.versions(it).getOrNull(++versionTry) }
                if (next != null) {
                    version = next; retries = 0
                    message.text = "Trying another version…"
                    Ui.test("fallback ${next.name}")
                    handler.postDelayed({ startChannel() }, 500)
                } else message.text = (if (fullScreen) "This channel isn't playing right now.\n" else "Not playing\n") + describe(error)
            }
        })
        video.player = p
        player = p
    }

    private fun describe(e: PlaybackException) = when (e.errorCode) {
        PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS -> "The provider refused the stream (too many connections or not in your package?)"
        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
        PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT -> "Couldn't reach the provider."
        PlaybackException.ERROR_CODE_PARSING_CONTAINER_UNSUPPORTED,
        PlaybackException.ERROR_CODE_DECODER_INIT_FAILED -> "This stream's format isn't supported on this device."
        else -> e.errorCodeName
    }

    private fun scheduleRetry(text: String) {
        retries++
        message.text = text
        handler.removeCallbacks(retry)
        handler.postDelayed(retry, 1_500L * retries)
    }

    private fun startChannel() {
        val p = player ?: return
        val c = version ?: list.getOrNull(index) ?: return
        Ui.test("load ${c.name}")
        message.text = if (fullScreen) "" else "Loading…"
        buffer?.close()
        buffer = null
        behindMs = 0; pausedAt = 0
        val url = Players.liveUrl(c)
        // MPEG-TS channels go through the pause/rewind buffer (so full screen can rewind straight away); HLS plays directly.
        val b = if (Store.timeshift && !url.contains(".m3u8", true)) try { LiveBuffer(url, a.cacheDir, 15 * 60).start() } catch (e: Exception) { null } else null
        buffer = b
        p.setMediaItem(if (b != null) Players.item(b.urlAt(0)) else Players.item(url))
        p.prepare()
        p.playWhenReady = true
        if (fullScreen) Store.noteWatched(list[index].id)
        handler.removeCallbacks(shiftTick)
        if (b != null) handler.post(shiftTick)
        showShift()
    }

    // ------------------------------------------------------------------ full screen

    fun enterFullScreen() {
        fullScreen = true
        a.window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        message.setTextSize(TypedValue.COMPLEX_UNIT_SP, 20f)
        list.getOrNull(index)?.let { Store.noteWatched(it.id); Ui.test("fullscreen ${it.name}") }
        player?.videoFormat?.frameRate?.let { FrameRate.match(a, it) }
        showShift()
        showInfo()
        handler.removeCallbacks(statsTick); handler.post(statsTick)
    }

    fun leaveFullScreen() {
        fullScreen = false
        handler.removeCallbacks(statsTick)
        statsBadge.visibility = View.GONE
        a.window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        FrameRate.reset(a)
        message.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14f)
        pendingJump = -1; handler.removeCallbacks(doJump)
        handler.removeCallbacks(hideInfo)
        info.animate().cancel()
        info.visibility = View.GONE
        // Paused or behind live: the preview carries on from live.
        if (pausedAt > 0 || behindMs > 0) { pausedAt = 0; if (buffer != null) seekBehind(0) else player?.playWhenReady = true }
        shift.visibility = View.GONE
    }

    /** BACK in full screen: hides the info bar first (true = handled here). */
    fun onBack(): Boolean {
        if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run(); return true }
        return false
    }

    fun onKey(keyCode: Int, event: KeyEvent): Boolean {
        val handled = keyCode in HANDLED
        if (event.action != KeyEvent.ACTION_DOWN) return handled
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP -> zap(-1)
            KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN -> zap(1)
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_INFO ->
                if (event.repeatCount == 0) {
                    // OK: the full info; OK again hides it.
                    if (info.visibility == View.VISIBLE && info.alpha > 0.5f && infoFull) { handler.removeCallbacks(hideInfo); hideInfo.run() } else showInfo(full = true)
                }
            KeyEvent.KEYCODE_MENU -> if (event.repeatCount == 0) options()
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY -> if (event.repeatCount == 0) togglePause()
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> if (buffer != null) {
                val step = if (keyCode == KeyEvent.KEYCODE_MEDIA_REWIND || event.repeatCount > 6) 30_000L else 10_000L
                pendingJump = (if (pendingJump < 0) currentBehind() else pendingJump) + step
                scheduleJump()
            }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> if (buffer != null) {
                val step = if (keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD || event.repeatCount > 6) 30_000L else 10_000L
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
        version = null; versionTry = 0
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
     * The info bar. Opening a stream or changing channel shows the channel, what's on, its description and the
     * stream stats; OK adds the times and progress, what's next and the keys.
     */
    fun showInfo(full: Boolean = false) {
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
        times.text = now?.let { Ui.span(it) } ?: ""
        now?.let { prog.progress = Ui.progress(it) }
        (times.parent as View).visibility = if (full && now != null) View.VISIBLE else View.GONE
        desc.text = now?.desc ?: ""
        desc.visibility = if (desc.text.isNotEmpty()) View.VISIBLE else View.GONE
        nextV.text = next?.let { "Next  ${Ui.time(it.start)}  ${it.title}" } ?: ""
        nextV.visibility = if (full && nextV.text.isNotEmpty()) View.VISIBLE else View.GONE
        overlay.findViewById<View>(R.id.keys).visibility = if (full) View.VISIBLE else View.GONE
        updateStats()
        info.animate().cancel()
        info.alpha = 1f
        info.visibility = View.VISIBLE
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
        if (buffer == null) { p.playWhenReady = !p.playWhenReady; return }
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

    private fun options() {
        val c = list.getOrNull(index) ?: return
        val p = player ?: return
        val items = ArrayList<Pair<String, () -> Unit>>()
        items.add((if (Store.isFavourite(c.id)) "Remove from Favourites" else "Add to Favourites") to { Store.toggleFavourite(c.id); showInfo(infoFull) })
        items.add("Audio & subtitles" to { app.dingus.tv.Tracks.choose(a, p) })
        if (currentBehind() >= 3_000) items.add("Back to live" to { seekBehind(0) })
        items.add((if (Store.statsPinned) "Hide stream stats" else "Show stream stats") to {
            Store.statsPinned = !Store.statsPinned; updateStats(); Ui.test("stats-pinned ${Store.statsPinned}")
        })
        items.add("Stream information" to { streamInfo() })
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
        private val HANDLED = setOf(
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT,
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER, KeyEvent.KEYCODE_INFO,
            KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_UP, KeyEvent.KEYCODE_PAGE_DOWN,
            KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY,
            KeyEvent.KEYCODE_MEDIA_REWIND, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD,
        )
    }
}
