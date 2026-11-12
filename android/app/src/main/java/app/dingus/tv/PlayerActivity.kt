package app.dingus.tv

import android.app.Activity
import android.net.Uri
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
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.DefaultLoadControl
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView

/** Full-screen live TV. Up/down change channel, OK shows the info bar, MENU favourites, BACK returns. */
@OptIn(UnstableApi::class)
class PlayerActivity : Activity() {
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
    private var zapPending: Runnable? = null
    private val hideInfo = Runnable { info.animate().alpha(0f).setDuration(250).withEndAction { info.visibility = View.GONE } }
    private val retry = Runnable { startChannel() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_player)
        video = findViewById(R.id.video)
        info = findViewById(R.id.info)
        message = findViewById(R.id.message)

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
        handler.removeCallbacksAndMessages(null)
        player?.release()
        player = null
        video.player = null
    }

    private fun makePlayer() {
        if (player != null) return
        val http = DefaultHttpDataSource.Factory()
            .setUserAgent(Net.USER_AGENT)
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(12_000)
            .setReadTimeoutMs(15_000)
        // Modest buffers: quick to start, and kind to the Fire Stick's memory.
        val load = DefaultLoadControl.Builder()
            .setBufferDurationsMs(8_000, 30_000, 1_200, 2_500)
            .setPrioritizeTimeOverSizeThresholds(true)
            .build()
        val p = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(http))
            .setLoadControl(load)
            .build()
        p.setVideoScalingMode(C.VIDEO_SCALING_MODE_SCALE_TO_FIT)
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
                if (retries < 3) scheduleRetry("Reconnecting…")
                else message.text = "This channel isn't playing right now.\n${describe(error)}"
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
        val c = list[index]
        message.text = ""
        val item = MediaItem.Builder().setUri(Uri.parse(c.url))
        if (c.url.contains(".m3u8", ignoreCase = true)) item.setMimeType(MimeTypes.APPLICATION_M3U8)
        p.setMediaItem(item.build())
        p.prepare()
        p.playWhenReady = true
        Store.noteWatched(c.id)
    }

    /** Changing channel: the info bar updates straight away; the stream switches once you stop pressing. */
    private fun zap(step: Int) {
        if (list.size < 2) return
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
        Logos.load(findViewById<ImageView>(R.id.logo), c.logo, Ui.dp(this, 56))
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

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_PAGE_UP -> { zap(-1); return true }
            KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_PAGE_DOWN -> { zap(1); return true }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_INFO -> {
                if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run() }
                else showInfo()
                return true
            }
            KeyEvent.KEYCODE_MENU -> {
                Store.toggleFavourite(list[index].id)
                showInfo()
                return true
            }
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> {
                player?.let { it.playWhenReady = !it.playWhenReady }
                return true
            }
            KeyEvent.KEYCODE_BACK -> {
                if (info.visibility == View.VISIBLE && info.alpha > 0.5f) { handler.removeCallbacks(hideInfo); hideInfo.run(); return true }
            }
        }
        return super.onKeyDown(keyCode, event)
    }
}
