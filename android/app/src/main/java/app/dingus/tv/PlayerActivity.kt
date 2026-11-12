package app.dingus.tv

import android.app.Activity
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.View
import android.view.WindowManager
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.annotation.OptIn
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView

/**
 * Full-screen live TV. Up/down change channel, left/right rewind and go forward (live TV is kept for 15 minutes),
 * play/pause pauses, OK shows the info bar, MENU has the options, BACK returns.
 */
@OptIn(UnstableApi::class)
class PlayerActivity : BaseActivity() {
    companion object {
        const val CHANNEL = "channel"
        const val CATEGORY = "category"
    }

    private val handler = Handler(Looper.getMainLooper())
    private var player: ExoPlayer? = null
    private lateinit var video: PlayerView
    private lateinit var info: View
    private lateinit var message: TextView

    private var list: List<Channel> = emptyList()
    private var index = 0
    private var retries = 0
    /** With duplicates merged: which version of the channel is playing (0 = the best). */
    private var versionTry = 0
    private var version: Channel? = null
    private var zapPending: Runnable? = null
    private val hideInfo = Runnable { info.animate().alpha(0f).setDuration(250).withEndAction { info.visibility = View.GONE } }
    private val retry = Runnable { startChannel() }

    // Pause and rewind
    private var buffer: LiveBuffer? = null
    private var behindMs = 0L          // how far behind live the picture is
    private var pausedAt = 0L
    private lateinit var shift: TextView
    private val shiftTick = object : Runnable {
        override fun run() { showShift(); handler.postDelayed(this, 1_000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_player)
        video = findViewById(R.id.video)
        info = findViewById(R.id.info)
        message = findViewById(R.id.message)
        shift = findViewById(R.id.shift)

        val id = intent.getStringExtra(CHANNEL)
        val cat = intent.getStringExtra(CATEGORY) ?: Repo.ALL
        list = Repo.channelsIn(cat).ifEmpty { Repo.channelsIn(Repo.ALL) }
        index = list.indexOfFirst { it.id == id }.coerceAtLeast(0)
        if (list.isEmpty()) { finish(); return }
    }

    override fun onStart() {
        super.onStart()
        if (list.isEmpty()) return
        makePlayer()
        startChannel()
        showInfo()
    }

    override fun onStop() {
        super.onStop()
        FrameRate.reset(this)
        handler.removeCallbacksAndMessages(null)
        player?.release()
        player = null
        video.player = null
        buffer?.close()
        buffer = null
    }

    private fun makePlayer() {
        if (player != null) return
        val p = Players.create(this)
        Players.watchFrameRate(this, p)
        p.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_BUFFERING -> if (message.text.isEmpty()) message.text = ""
                    Player.STATE_READY -> {
                        retries = 0
                        message.text = ""
                        Ui.test("playing ${list[index].name} ${p.videoSize.width}x${p.videoSize.height}")
                    }
                    Player.STATE_ENDED -> { // A live stream "ending" means the connection dropped: reconnect.
                        scheduleRetry("Reconnecting…")
                    }
                    else -> {}
                }
            }

            override fun onPlayerError(error: PlaybackException) {
                Ui.test("error ${error.errorCodeName}")
                if (error.errorCode == PlaybackException.ERROR_CODE_BEHIND_LIVE_WINDOW) {
                    p.seekToDefaultPosition(); p.prepare(); return
                }
                if (retries < 3) { scheduleRetry("Reconnecting…"); return }
                // Merged duplicates: try the channel's next version before giving up.
                val next = Repo.versions(list[index]).getOrNull(++versionTry)
                if (next != null) {
                    version = next; retries = 0
                    message.text = "Trying another version…"
                    Ui.test("fallback ${next.name}")
                    handler.postDelayed({ startChannel() }, 500)
                } else message.text = "This channel isn't playing right now.\n${describe(error)}"
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
        val c = version ?: list[index]
        message.text = ""
        buffer?.close()
        buffer = null
        behindMs = 0; pausedAt = 0
        val url = Players.liveUrl(c)
        // MPEG-TS channels go through the pause/rewind buffer; HLS plays directly.
        val b = if (Store.timeshift && !url.contains(".m3u8", true)) try { LiveBuffer(url, cacheDir, 15 * 60).start() } catch (e: Exception) { null } else null
        buffer = b
        p.setMediaItem(if (b != null) Players.item(b.urlAt(0)) else Players.item(url))
        p.prepare()
        p.playWhenReady = true
        Store.noteWatched(list[index].id)
        handler.removeCallbacks(shiftTick)
        if (b != null) handler.post(shiftTick)
        showShift()
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
        showShift(); showInfo()
    }

    private fun togglePause() {
        val p = player ?: return
        if (buffer == null) { p.playWhenReady = !p.playWhenReady; return }
        if (pausedAt == 0L) { p.playWhenReady = false; pausedAt = System.currentTimeMillis(); Ui.test("timeshift paused") }
        else {
            val b = buffer!!
            val behind = currentBehind()
            // Paused for longer than is kept: carry on from the oldest part still saved.
            if (behind > b.secondsAvailable * 1000) { pausedAt = 0; seekBehind(behind); return }
            behindMs = behind; pausedAt = 0
            p.playWhenReady = true
        }
        showShift()
    }

    private fun showShift() {
        val behind = currentBehind()
        when {
            pausedAt > 0 -> { shift.text = "❚❚  Paused  ·  −${clock(behind)}"; shift.visibility = View.VISIBLE }
            behind >= 3_000 -> { shift.text = "−${clock(behind)}  behind live"; shift.visibility = View.VISIBLE }
            else -> shift.visibility = View.GONE
        }
    }

    private fun clock(ms: Long): String { val s = ms / 1000; return "%d:%02d".format(s / 60, s % 60) }

    // ------------------------------------------------------------------ options

    private fun options() {
        val c = list[index]
        val p = player ?: return
        val fav = Store.isFavourite(c.id)
        val items = ArrayList<Pair<String, () -> Unit>>()
        items.add((if (fav) "Remove from Favourites" else "Add to Favourites") to { Store.toggleFavourite(c.id); showInfo() })
        items.add("Audio & subtitles" to { Tracks.choose(this, p) })
        if (currentBehind() >= 3_000) items.add("Back to live" to { seekBehind(0) })
        items.add("Stream information" to { streamInfo() })
        Dialogs.builder(this).setTitle(c.name)
            .setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }
            .show()
    }

    private fun streamInfo() {
        val p = player ?: return
        val v = p.videoFormat
        val a = p.audioFormat
        val lines = ArrayList<String>()
        if (v != null) {
            lines.add("Picture: ${v.width} × ${v.height}" + (if (v.frameRate > 0) "  ·  ${"%.2f".format(v.frameRate)} fps" else ""))
            v.sampleMimeType?.let { lines.add("Video: ${it.substringAfter('/').uppercase()}") }
        }
        if (a != null) lines.add("Sound: ${a.sampleMimeType?.substringAfter('/')?.uppercase() ?: "?"}" + (if (a.channelCount > 0) "  ·  ${a.channelCount} channels" else ""))
        buffer?.byteRate?.takeIf { it > 0 }?.let { lines.add("Bitrate: ${"%.1f".format(it * 8 / 1_000_000)} Mbit/s") }
        buffer?.let { lines.add("Rewind available: ${clock((it.secondsAvailable * 1000).toLong())}") }
        lines.add("Address: ${Players.liveUrl(version ?: list[index]).replace(Regex("/live/[^/]+/[^/]+/"), "/live/…/")}")
        Dialogs.builder(this).setTitle("Stream information").setMessage(lines.joinToString("\n")).setPositiveButton("OK", null).show()
    }

    /** Changing channel: the info bar updates straight away; the stream switches once you stop pressing. */
    private fun zap(step: Int) {
        if (list.size < 2) return
        version = null; versionTry = 0
        pendingJump = -1; handler.removeCallbacks(doJump)
        index = (index + step + list.size) % list.size
        retries = 0
        showInfo()
        zapPending?.let { handler.removeCallbacks(it) }
        val r = Runnable { startChannel() }
        zapPending = r
        handler.postDelayed(r, 350)
    }

    private fun showInfo() {
        val c = list[index]
        findViewById<TextView>(R.id.name).text = c.name
        findViewById<TextView>(R.id.number).text = if (c.number > 0) c.number.toString() else ""
        findViewById<View>(R.id.fav).visibility = if (Store.isFavourite(c.id)) View.VISIBLE else View.GONE
        findViewById<TextView>(R.id.initials).text = Ui.initials(c.name)
        Logos.load(findViewById<ImageView>(R.id.logo), c.logo, Ui.dp(this, 56), findViewById(R.id.initials))
        val (now, next) = Repo.nowNext(c)
        val nowV = findViewById<TextView>(R.id.now)
        val times = findViewById<TextView>(R.id.times)
        val prog = findViewById<ProgressBar>(R.id.progress)
        val desc = findViewById<TextView>(R.id.desc)
        if (now != null) {
            nowV.text = now.title
            times.text = Ui.span(now)
            prog.progress = Ui.progress(now)
            prog.visibility = View.VISIBLE
            desc.text = now.desc ?: ""
        } else {
            nowV.text = "No guide information"
            times.text = ""
            prog.visibility = View.INVISIBLE
            desc.text = ""
        }
        desc.visibility = if (desc.text.isEmpty()) View.GONE else View.VISIBLE
        findViewById<TextView>(R.id.next).text = next?.let { "Next  ${Ui.time(it.start)}  ${it.title}" } ?: ""
        info.animate().cancel()
        info.alpha = 1f
        info.visibility = View.VISIBLE
        handler.removeCallbacks(hideInfo)
        handler.postDelayed(hideInfo, 5_000)
    }

    /** BACK hides the info bar first, then goes back to the channel list. */
    override fun onBackPressed() {
        if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run(); return }
        Ui.test("player-closed")
        super.onBackPressed()
    }

    /** Several presses of left/right add up, and the jump happens once you stop pressing. */
    private var pendingJump = -1L
    private val doJump = Runnable { val t = pendingJump; pendingJump = -1; if (t >= 0) seekBehind(t) }
    private fun scheduleJump() {
        val b = buffer
        val max = if (b != null) (b.secondsAvailable * 1000).toLong() else 0L
        pendingJump = pendingJump.coerceIn(0, maxOf(0, max))
        shift.text = if (pendingJump < 3_000) "Live" else "−${clock(pendingJump)}"
        shift.visibility = View.VISIBLE
        handler.removeCallbacks(doJump)
        handler.postDelayed(doJump, 600)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP -> { zap(-1); return true }
            KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN -> { zap(1); return true }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_INFO -> {
                if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run() }
                else showInfo()
                return true
            }
            KeyEvent.KEYCODE_MENU -> { if (event.repeatCount == 0) options(); return true }
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY -> {
                if (event.repeatCount == 0) togglePause(); return true
            }
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> {
                if (buffer == null) return true
                val step = if (keyCode == KeyEvent.KEYCODE_MEDIA_REWIND || event.repeatCount > 6) 30_000L else 10_000L
                pendingJump = (if (pendingJump < 0) currentBehind() else pendingJump) + step
                scheduleJump(); return true
            }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> {
                if (buffer == null) return true
                val step = if (keyCode == KeyEvent.KEYCODE_MEDIA_FAST_FORWARD || event.repeatCount > 6) 30_000L else 10_000L
                pendingJump = maxOf(0, (if (pendingJump < 0) currentBehind() else pendingJump) - step)
                scheduleJump(); return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }
}
