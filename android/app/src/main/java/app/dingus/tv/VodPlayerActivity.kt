package app.dingus.tv

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.WindowManager
import android.widget.TextView
import androidx.annotation.OptIn
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import java.util.Locale

/** Films and episodes: resumes where you left off, and goes straight on to the next episode. */
@OptIn(UnstableApi::class)
class VodPlayerActivity : BaseActivity() {
    class Entry(val id: String, val title: String, val url: String)

    companion object {
        /** A past TV programme (catch-up): plays like a film, but nothing is remembered. */
        const val CATCHUP = "catchup"

        fun intent(ctx: Context, kind: String, showId: String?, entries: List<Entry>, index: Int, fromStart: Boolean): Intent =
            Intent(ctx, VodPlayerActivity::class.java)
                .putExtra("kind", kind).putExtra("show", showId)
                .putExtra("ids", entries.map { it.id }.toTypedArray())
                .putExtra("titles", entries.map { it.title }.toTypedArray())
                .putExtra("urls", entries.map { it.url }.toTypedArray())
                .putExtra("index", index).putExtra("fromStart", fromStart)
    }

    private val handler = Handler(Looper.getMainLooper())
    private var player: ExoPlayer? = null
    private lateinit var video: PlayerView
    private lateinit var message: TextView
    private lateinit var kind: String
    private var showId: String? = null
    private lateinit var ids: Array<String>
    private lateinit var titles: Array<String>
    private lateinit var urls: Array<String>
    private var index = 0
    private var fromStart = false
    private var retries = 0
    private var resumeAt = -1L
    private val save = object : Runnable {
        override fun run() { savePosition(); handler.postDelayed(this, 10_000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_vod_player)
        video = findViewById(R.id.video)
        message = findViewById(R.id.message)
        kind = intent.getStringExtra("kind") ?: Vod.MOVIES
        showId = intent.getStringExtra("show")
        ids = intent.getStringArrayExtra("ids") ?: emptyArray()
        titles = intent.getStringArrayExtra("titles") ?: emptyArray()
        urls = intent.getStringArrayExtra("urls") ?: emptyArray()
        index = intent.getIntExtra("index", 0).coerceIn(0, maxOf(0, ids.size - 1))
        fromStart = intent.getBooleanExtra("fromStart", false)
        if (ids.isEmpty()) finish()
    }

    override fun onStart() {
        super.onStart()
        if (ids.isEmpty()) return
        val p = Players.create(this)
        Players.watchFrameRate(this, p)
        p.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    Player.STATE_READY -> {
                        retries = 0
                        message.text = ""
                        Ui.test("vod-playing ${titles[index]}")
                    }
                    Player.STATE_ENDED -> finishedOne()
                    else -> {}
                }
            }
            override fun onPlayerError(error: PlaybackException) {
                Ui.test("vod-error ${error.errorCodeName}")
                val pos = player?.currentPosition ?: 0
                if (retries++ < 2) {
                    message.text = "Reconnecting…"
                    handler.postDelayed({ open(pos) }, 1_500L * retries)
                } else message.text = "This won't play right now.\n${error.errorCodeName}"
            }
        })
        video.player = p
        player = p
        video.findViewById<TextView>(R.id.vodTitle)?.text = titles[index]
        val start = when {
            resumeAt >= 0 -> resumeAt
            fromStart -> 0L
            else -> Vod.position(ids[index])?.first ?: 0L
        }
        open(start)
        handler.postDelayed(save, 10_000)
    }

    override fun onStop() {
        super.onStop()
        FrameRate.reset(this)
        handler.removeCallbacksAndMessages(null)
        savePosition()
        resumeAt = player?.currentPosition ?: -1L
        player?.release()
        player = null
        video.player = null
    }

    private fun open(at: Long) {
        val p = player ?: return
        p.setMediaItem(MediaItem.fromUri(Uri.parse(urls[index])), at)
        p.prepare()
        p.playWhenReady = true
        video.findViewById<TextView>(R.id.vodTitle)?.text = titles[index]
        video.showController()
    }

    private fun savePosition() {
        if (kind == CATCHUP) return
        val p = player ?: return
        val len = p.duration
        if (len == C.TIME_UNSET || len <= 0) return
        Vod.savePosition(ids[index], p.currentPosition, len, kind, showId)
    }

    /** End of a film or episode: next episode if there is one, otherwise back to the details. */
    private fun finishedOne() {
        if (kind != CATCHUP) player?.let { if (it.duration > 0) Vod.savePosition(ids[index], it.duration, it.duration, kind, showId) }
        if (kind == Vod.SERIES && index + 1 < ids.size) {
            index++
            resumeAt = -1
            message.text = "Next: ${titles[index]}"
            handler.postDelayed({ message.text = "" }, 3_000)
            open(0)
        } else finish()
    }

    private fun seek(by: Long) {
        val p = player ?: return
        val len = p.duration
        var t = p.currentPosition + by
        if (len > 0) t = t.coerceAtMost(len - 1_000)
        p.seekTo(t.coerceAtLeast(0))
        video.showController()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val p = player ?: return super.dispatchKeyEvent(event)
        if (event.action != KeyEvent.ACTION_DOWN) {
            return when (event.keyCode) {
                KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER,
                KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_MENU,
                KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, KeyEvent.KEYCODE_MEDIA_REWIND, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> true
                else -> super.dispatchKeyEvent(event)
            }
        }
        // Holding the button down skips further each time.
        val step = when { event.repeatCount > 20 -> 60_000L; event.repeatCount > 6 -> 30_000L; else -> 10_000L }
        when (event.keyCode) {
            KeyEvent.KEYCODE_DPAD_LEFT -> seek(-step)
            KeyEvent.KEYCODE_DPAD_RIGHT -> seek(step)
            KeyEvent.KEYCODE_MEDIA_REWIND -> seek(-30_000)
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> seek(30_000)
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> {
                if (event.repeatCount == 0) { p.playWhenReady = !p.playWhenReady; video.showController() }
            }
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> video.showController()
            KeyEvent.KEYCODE_MENU -> if (event.repeatCount == 0) Tracks.choose(this, p)
            else -> return super.dispatchKeyEvent(event)
        }
        return true
    }
}

/** Audio and subtitle choice, for any player. */
@OptIn(UnstableApi::class)
object Tracks {
    private class Option(val label: String, val apply: (ExoPlayer) -> Unit, val on: Boolean)

    fun choose(a: Activity, p: ExoPlayer) {
        val opts = ArrayList<Option>()
        val audio = p.currentTracks.groups.filter { it.type == C.TRACK_TYPE_AUDIO && it.isSupported }
        val text = p.currentTracks.groups.filter { it.type == C.TRACK_TYPE_TEXT && it.isSupported }
        if (audio.size > 1) audio.forEachIndexed { i, g ->
            val f = g.getTrackFormat(0)
            opts.add(Option("Audio: ${name(f.language, f.label, "Track ${i + 1}")}", { pl ->
                pl.trackSelectionParameters = pl.trackSelectionParameters.buildUpon()
                    .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, 0)).build()
            }, g.isSelected))
        }
        val subsOff = p.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT) || text.none { it.isSelected }
        if (text.isNotEmpty()) {
            opts.add(Option("Subtitles: off", { pl ->
                pl.trackSelectionParameters = pl.trackSelectionParameters.buildUpon().setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true).build()
            }, subsOff))
            text.forEachIndexed { i, g ->
                val f = g.getTrackFormat(0)
                opts.add(Option("Subtitles: ${name(f.language, f.label, "Track ${i + 1}")}", { pl ->
                    pl.trackSelectionParameters = pl.trackSelectionParameters.buildUpon().setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                        .setOverrideForType(TrackSelectionOverride(g.mediaTrackGroup, 0)).build()
                }, !subsOff && g.isSelected))
            }
        }
        if (opts.isEmpty()) {
            Dialogs.builder(a).setTitle("Audio & subtitles").setMessage("This has just one audio track and no subtitles.")
                .setPositiveButton("OK", null).show()
            return
        }
        val checked = opts.indexOfFirst { it.on }
        Dialogs.builder(a).setTitle("Audio & subtitles")
            .setSingleChoiceItems(opts.map { (if (it.on) "✓  " else "     ") + it.label }.toTypedArray(), checked) { d, which ->
                opts[which].apply(p); d.dismiss()
            }
            .show()
    }

    private fun name(lang: String?, label: String?, fallback: String): String {
        val l = lang?.takeIf { it.isNotBlank() && it != "und" }?.let { Locale(it).displayLanguage.ifBlank { it } }
        return listOfNotNull(l, label?.takeIf { it.isNotBlank() && it != l }).joinToString(" – ").ifEmpty { fallback }
    }
}
