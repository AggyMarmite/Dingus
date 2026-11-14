package app.dingus.tv

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.View
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

/**
 * Films, episodes and catch-up: resumes where you left off. Our own controls: the progress bar's knob is play/pause,
 * ◀ ▶ (and the remote's rewind / fast forward) skip 10 seconds, a row of pill buttons under it; Episodes slides in
 * from the right; an "Up next" card near the end of an episode.
 */
@OptIn(UnstableApi::class)
class VodPlayerActivity : BaseActivity() {
    class Entry(val id: String, val title: String, val url: String, val image: String? = null)

    companion object {
        /** A past TV programme (catch-up): plays like a film, but nothing is remembered. */
        const val CATCHUP = "catchup"

        fun intent(ctx: Context, kind: String, showId: String?, entries: List<Entry>, index: Int, fromStart: Boolean, sub: String? = null): Intent =
            Intent(ctx, VodPlayerActivity::class.java)
                .putExtra("kind", kind).putExtra("show", showId)
                .putExtra("ids", entries.map { it.id }.toTypedArray())
                .putExtra("titles", entries.map { it.title }.toTypedArray())
                .putExtra("urls", entries.map { it.url }.toTypedArray())
                .putExtra("images", entries.map { it.image ?: "" }.toTypedArray())
                .putExtra("sub", sub)
                .putExtra("index", index).putExtra("fromStart", fromStart)
    }

    private val handler = Handler(Looper.getMainLooper())
    private var player: ExoPlayer? = null
    private lateinit var video: PlayerView
    private lateinit var message: TextView
    private lateinit var controls: View
    private lateinit var bar: PlayBar
    private lateinit var pausedPill: TextView
    private lateinit var pausedLine: android.widget.ProgressBar
    private lateinit var upNext: View
    private lateinit var epPanel: View
    private lateinit var kind: String
    private var showId: String? = null
    private lateinit var ids: Array<String>
    private lateinit var titles: Array<String>
    private lateinit var urls: Array<String>
    private lateinit var images: Array<String>
    private var sub: String? = null
    private var index = 0
    private var fromStart = false
    private var retries = 0
    private var resumeAt = -1L
    private var upNextDismissed = -1
    private val save = object : Runnable {
        override fun run() { savePosition(); handler.postDelayed(this, 10_000) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setContentView(R.layout.activity_vod_player)
        video = findViewById(R.id.video)
        message = findViewById(R.id.message)
        controls = findViewById(R.id.controls)
        bar = findViewById(R.id.bar)
        pausedPill = findViewById(R.id.pausedPill)
        pausedLine = findViewById(R.id.pausedLine)
        upNext = findViewById(R.id.upNext)
        epPanel = findViewById(R.id.epPanel)
        kind = intent.getStringExtra("kind") ?: Vod.MOVIES
        showId = intent.getStringExtra("show")
        ids = intent.getStringArrayExtra("ids") ?: emptyArray()
        titles = intent.getStringArrayExtra("titles") ?: emptyArray()
        urls = intent.getStringArrayExtra("urls") ?: emptyArray()
        images = intent.getStringArrayExtra("images") ?: Array(ids.size) { "" }
        sub = intent.getStringExtra("sub")
        index = intent.getIntExtra("index", 0).coerceIn(0, maxOf(0, ids.size - 1))
        fromStart = intent.getBooleanExtra("fromStart", false)
        if (ids.isEmpty()) { finish(); return }

        val shadeTop = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.TOP_BOTTOM,
            intArrayOf(0xB30A0C12.toInt(), 0))
        findViewById<View>(R.id.topShade).background = shadeTop
        findViewById<View>(R.id.bottomShade).background = android.graphics.drawable.GradientDrawable(
            android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(0xF50A0C12.toInt(), 0xCC0A0C12.toInt(), 0))
        findViewById<View>(R.id.epShade).background = android.graphics.drawable.GradientDrawable(
            android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(0x400A0C12, 0xD90A0C12.toInt(), 0xF70A0C12.toInt()))
        findViewById<View>(R.id.upNextPicBox).clipToOutline = true

        findViewById<View>(R.id.btnTracks).setOnClickListener { player?.let { Tracks.choose(this, it) } }
        findViewById<View>(R.id.btnRestart).setOnClickListener { player?.seekTo(0); bar.skipped = null; showControls() }
        findViewById<View>(R.id.btnEpisodes).apply {
            visibility = if (kind == Vod.SERIES && ids.size > 1) View.VISIBLE else View.GONE
            setOnClickListener { openEpisodes() }
        }
        upNext.setOnClickListener { playIndex(index + 1) }
        showTitle()
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
                        Account.soon()
                    }
                    Player.STATE_ENDED -> finishedOne()
                    else -> {}
                }
            }
            override fun onIsPlayingChanged(isPlaying: Boolean) { tickNow() }
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
        val start = when {
            resumeAt >= 0 -> resumeAt
            fromStart -> 0L
            else -> Vod.position(ids[index])?.first ?: 0L
        }
        open(start)
        handler.postDelayed(save, 10_000)
        handler.post(ticker)
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
        showTitle()
        showControls()
    }

    /** "FILM · 2024 · ★ 7.4" / "THE HOLLOW · SERIES 1" over the title ("Episode 3 · The Ferry"). */
    private fun showTitle() {
        val t = titles[index]
        val ep = Regex("""^(.*?)\s+·\s+S(\d+) E(\d+)\s*(.*)$""").find(t)
        val subV = findViewById<TextView>(R.id.vodSub)
        val titleV = findViewById<TextView>(R.id.vodTitle)
        if (kind == Vod.SERIES && ep != null) {
            val (show, season, num, name) = ep.destructured
            subV.text = "${show.uppercase()}  ·  SERIES $season"
            titleV.text = if (name.isBlank()) "Episode $num" else "Episode $num  ·  $name"
        } else {
            subV.text = sub ?: when (kind) { CATCHUP -> "CATCH UP"; Vod.SERIES -> "TV SHOW"; else -> "FILM" }
            titleV.text = t
        }
    }

    private fun savePosition() {
        if (kind == CATCHUP) return
        val p = player ?: return
        val len = p.duration
        if (len == C.TIME_UNSET || len <= 0) return
        Vod.savePosition(ids[index], p.currentPosition, len, kind, showId)
    }

    /** End of a film or episode: the next episode if that's set to happen, otherwise back to the details. */
    private fun finishedOne() {
        if (kind != CATCHUP) player?.let { if (it.duration > 0) Vod.savePosition(ids[index], it.duration, it.duration, kind, showId) }
        if (kind == Vod.SERIES && index + 1 < ids.size && Store.autoNext) playIndex(index + 1) else finish()
    }

    private fun playIndex(i: Int) {
        if (i !in ids.indices) return
        savePosition()
        index = i
        resumeAt = -1
        upNext.visibility = View.GONE
        closeEpisodes()
        Ui.test("vod-episode $i")
        open(Vod.position(ids[i])?.first ?: 0L)
    }

    // ------------------------------------------------------------------ the controls

    private val hideControls = Runnable { hideControlsNow() }

    private fun showControls(focusBar: Boolean = false) {
        if (epPanel.visibility == View.VISIBLE) return
        controls.visibility = View.VISIBLE
        pausedPill.visibility = View.GONE; pausedLine.visibility = View.GONE
        val f = currentFocus
        if (focusBar || f == null || !inside(f, controls)) { if (upNext.visibility != View.VISIBLE || !upNext.hasFocus()) bar.requestFocus() }
        handler.removeCallbacks(hideControls)
        handler.postDelayed(hideControls, 4_500)
        tickNow()
    }

    private fun hideControlsNow() {
        handler.removeCallbacks(hideControls)
        controls.visibility = View.GONE
        bar.skipped = null
        tickNow()
    }

    private fun inside(v: View, parent: View): Boolean {
        var p: Any? = v
        while (p is View) { if (p === parent) return true; p = p.parent }
        return false
    }

    private val ticker = object : Runnable {
        override fun run() { tickNow(); handler.postDelayed(this, 500) }
    }

    private fun tickNow() {
        val p = player ?: return
        val len = p.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: 0L
        val pos = p.currentPosition
        bar.duration = len; bar.position = pos; bar.buffered = p.bufferedPosition; bar.playing = p.playWhenReady
        bar.invalidate()
        findViewById<TextView>(R.id.vodPos).text = PlayBar.clock(pos)
        findViewById<TextView>(R.id.vodLeft).text = if (len > 0)
            "−${PlayBar.clock(len - pos)}  ·  ends ${Ui.time(System.currentTimeMillis() + (len - pos))}" else ""
        findViewById<TextView>(R.id.vodClock).text = Ui.time(System.currentTimeMillis())
        // Paused with the controls hidden: the small pill and thin line.
        val paused = !p.playWhenReady && controls.visibility != View.VISIBLE && epPanel.visibility != View.VISIBLE
        pausedPill.visibility = if (paused) View.VISIBLE else View.GONE
        pausedLine.visibility = pausedPill.visibility
        if (paused) {
            pausedPill.text = android.text.SpannableStringBuilder("❚❚  Paused").apply {
                setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), 0, length, 0)
                if (len > 0) { val st = length; append("  ·  ${PlayBar.clock(pos)} / ${PlayBar.clock(len)}")
                    setSpan(android.text.style.ForegroundColorSpan(0xFF8B94A7.toInt()), st, length, 0) }
            }
            pausedLine.progress = if (len > 0) (pos * 1000 / len).toInt() else 0
        }
        // Near the end of an episode: Up next.
        val nearEnd = kind == Vod.SERIES && index + 1 < ids.size && len > 5 * 60_000L && len - pos in 1..45_000L
        if (nearEnd && upNextDismissed != index && upNext.visibility != View.VISIBLE && epPanel.visibility != View.VISIBLE) {
            upNext.visibility = View.VISIBLE
            val n = Regex("""S\d+ E(\d+)\s*(.*)$""").find(titles[index + 1])
            findViewById<TextView>(R.id.upNextTitle).text = n?.let { m ->
                val (num, name) = m.destructured; if (name.isBlank()) "Episode $num" else "Episode $num  ·  $name" } ?: titles[index + 1]
            Posters.load(findViewById(R.id.upNextPic), images.getOrNull(index + 1)?.ifEmpty { null }, Ui.dp(this, 140), wide = true)
            upNext.requestFocus()
            Ui.test("vod-up-next")
        } else if (!nearEnd && upNext.visibility == View.VISIBLE) upNext.visibility = View.GONE
    }

    private var skipFrom = -1L
    private val endSkip = Runnable { skipFrom = -1L; bar.skipped = null; bar.invalidate() }

    private fun skip(by: Long) {
        val p = player ?: return
        if (skipFrom < 0) skipFrom = p.currentPosition
        val len = p.duration
        var t = p.currentPosition + by
        if (len > 0) t = t.coerceAtMost(len - 1_000)
        t = t.coerceAtLeast(0)
        p.seekTo(t)
        showControls(focusBar = true)
        bar.position = t; bar.skipped = t - skipFrom; bar.invalidate()
        handler.removeCallbacks(endSkip); handler.postDelayed(endSkip, 1_500)
        Ui.test("vod-skip ${by / 1000}")
    }

    private fun togglePause() {
        val p = player ?: return
        p.playWhenReady = !p.playWhenReady
        Ui.test("vod-${if (p.playWhenReady) "play" else "pause"}")
        if (p.playWhenReady) showControls() else { showControls(); }
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val p = player ?: return super.dispatchKeyEvent(event)
        val code = event.keyCode
        val down = event.action == KeyEvent.ACTION_DOWN
        // The Episodes list: it moves about by itself; BACK closes it.
        if (epPanel.visibility == View.VISIBLE) {
            if (code == KeyEvent.KEYCODE_BACK) { if (!down) closeEpisodes(); return true }
            return super.dispatchKeyEvent(event)
        }
        // The Up next card, when it has the remote: OK plays it, BACK puts it away.
        if (upNext.visibility == View.VISIBLE && upNext.hasFocus()) {
            when (code) {
                KeyEvent.KEYCODE_BACK -> { if (!down) { upNextDismissed = index; upNext.visibility = View.GONE; showControls(focusBar = true) }; return true }
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> { if (!down && event.repeatCount == 0) playIndex(index + 1); return true }
                KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_DOWN -> { if (down) { upNextDismissed = index; showControls(focusBar = true) }; return true }
            }
        }
        if (!down) {
            return when (code) {
                KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN,
                KeyEvent.KEYCODE_MENU, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD, KeyEvent.KEYCODE_MEDIA_REWIND,
                KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE, KeyEvent.KEYCODE_MEDIA_PLAY, KeyEvent.KEYCODE_MEDIA_PAUSE -> true
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> if (controls.visibility == View.VISIBLE && !bar.isFocused) super.dispatchKeyEvent(event) else true
                else -> super.dispatchKeyEvent(event)
            }
        }
        val shown = controls.visibility == View.VISIBLE
        if (shown) { handler.removeCallbacks(hideControls); handler.postDelayed(hideControls, 4_500) }
        // Holding ◀ ▶ skips further each time.
        val step = when { event.repeatCount > 20 -> 60_000L; event.repeatCount > 6 -> 30_000L; else -> 10_000L }
        val onBar = !shown || bar.isFocused
        when (code) {
            KeyEvent.KEYCODE_MEDIA_REWIND -> skip(-10_000)
            KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> skip(10_000)
            KeyEvent.KEYCODE_MEDIA_PLAY -> { p.playWhenReady = true; showControls() }
            KeyEvent.KEYCODE_MEDIA_PAUSE -> { p.playWhenReady = false; showControls() }
            KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> if (event.repeatCount == 0) togglePause()
            KeyEvent.KEYCODE_MENU -> if (event.repeatCount == 0) Tracks.choose(this, p)
            KeyEvent.KEYCODE_DPAD_LEFT -> if (onBar) skip(-step) else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_RIGHT -> if (onBar) skip(step) else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER ->
                if (onBar) { if (event.repeatCount == 0) { togglePause(); bar.requestFocus() } } else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_DOWN -> when {
                !shown -> showControls(focusBar = true)
                bar.isFocused -> (0 until findViewById<android.view.ViewGroup>(R.id.vodButtons).childCount)
                    .map { findViewById<android.view.ViewGroup>(R.id.vodButtons).getChildAt(it) }
                    .firstOrNull { it.visibility == View.VISIBLE }?.requestFocus()
                else -> {}
            }
            KeyEvent.KEYCODE_DPAD_UP -> if (!shown) showControls(focusBar = true) else if (!bar.isFocused) bar.requestFocus()
            else -> return super.dispatchKeyEvent(event)
        }
        return true
    }

    // ------------------------------------------------------------------ Episodes

    private class Ep(val index: Int, val season: Int, val number: Int, val name: String)

    private val eps: List<Ep> by lazy {
        titles.mapIndexed { i, t ->
            val m = Regex("""S(\d+) E(\d+)\s*(.*)$""").find(t)
            if (m != null) Ep(i, m.groupValues[1].toInt(), m.groupValues[2].toInt(), m.groupValues[3])
            else Ep(i, 0, i + 1, t.substringAfterLast("  ·  "))
        }
    }
    private var epSeason = -1
    private val epAdapter = EpAdapter()

    private fun openEpisodes() {
        hideControlsNow()
        upNext.visibility = View.GONE
        epPanel.visibility = View.VISIBLE
        pausedPill.visibility = View.GONE; pausedLine.visibility = View.GONE
        val show = Regex("""^(.*?)\s+·\s+S\d+""").find(titles[index])?.groupValues?.get(1) ?: ""
        findViewById<TextView>(R.id.epShow).text = show.uppercase()
        val list = findViewById<androidx.recyclerview.widget.RecyclerView>(R.id.epList)
        if (list.adapter == null) {
            list.layoutManager = androidx.recyclerview.widget.LinearLayoutManager(this)
            list.adapter = epAdapter
            list.itemAnimator = null
        }
        val seasons = eps.map { it.season }.distinct()
        val row = findViewById<android.widget.LinearLayout>(R.id.epSeasons)
        row.removeAllViews()
        if (seasons.size > 1) for (s in seasons) {
            val b = TextView(android.view.ContextThemeWrapper(this, R.style.Choice), null, 0, R.style.Choice)
            b.text = if (s > 0) "Series $s" else "Episodes"; b.textSize = 13f; b.tag = s
            b.setPadding(Ui.dp(this, 16), Ui.dp(this, 7), Ui.dp(this, 16), Ui.dp(this, 7))
            b.layoutParams = android.widget.LinearLayout.LayoutParams(-2, -2).apply { marginEnd = Ui.dp(this@VodPlayerActivity, 8) }
            b.setOnFocusChangeListener { _, has -> if (has) showEpSeason(s) }
            b.setOnClickListener { focusEp() }
            row.addView(b)
        }
        epSeason = -1
        showEpSeason(eps[index].season)
        focusEp()
        if (!Ui.lite) { val body = findViewById<View>(R.id.epBody); body.translationX = Ui.dp(this, 60).toFloat(); body.alpha = 0f
            body.animate().translationX(0f).alpha(1f).setDuration(200).start() }
        Ui.test("vod-episodes ${eps.size}")
    }

    private fun showEpSeason(s: Int) {
        if (s == epSeason) return
        epSeason = s
        val row = findViewById<android.widget.LinearLayout>(R.id.epSeasons)
        for (i in 0 until row.childCount) row.getChildAt(i).isActivated = row.getChildAt(i).tag == s
        epAdapter.set(eps.filter { it.season == s })
    }

    private fun focusEp() {
        val list = findViewById<androidx.recyclerview.widget.RecyclerView>(R.id.epList)
        val i = epAdapter.items.indexOfFirst { it.index == index }.coerceAtLeast(0)
        list.scrollToPosition(i)
        list.post { list.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
    }

    private fun closeEpisodes() {
        if (epPanel.visibility != View.VISIBLE) return
        epPanel.visibility = View.GONE
        showControls(focusBar = true)
    }

    private inner class EpAdapter : androidx.recyclerview.widget.RecyclerView.Adapter<EpAdapter.H>() {
        var items: List<Ep> = emptyList()
        fun set(l: List<Ep>) { items = l; notifyDataSetChanged() }
        inner class H(v: View) : androidx.recyclerview.widget.RecyclerView.ViewHolder(v) {
            val num: TextView = v.findViewById(R.id.num)
            val pic: android.widget.ImageView = v.findViewById(R.id.pic)
            val playing: View = v.findViewById(R.id.playing)
            val prog: android.widget.ProgressBar = v.findViewById(R.id.prog)
            val title: TextView = v.findViewById(R.id.title)
            val sub: TextView = v.findViewById(R.id.sub)
        }
        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: android.view.ViewGroup, viewType: Int): H {
            val v = layoutInflater.inflate(R.layout.item_vod_episode, parent, false)
            v.findViewById<View>(R.id.picBox).clipToOutline = true
            val h = H(v)
            v.setOnClickListener { val p = h.bindingAdapterPosition; if (p >= 0) { val e = items[p]; if (e.index == index) closeEpisodes() else playIndex(e.index) } }
            return h
        }
        override fun onBindViewHolder(h: H, position: Int) {
            val e = items[position]
            h.num.text = "E${e.number}"
            h.title.text = if (e.name.isBlank()) "Episode ${e.number}" else "${e.number}.  ${e.name}"
            val pos = Vod.position(ids[e.index])
            h.sub.text = when {
                e.index == index -> "Playing now"
                pos != null && pos.second > 0 -> "${(pos.second - pos.first) / 60_000} min left"
                Vod.watched(ids[e.index]) -> "Watched"
                else -> ""
            }
            h.playing.visibility = if (e.index == index) View.VISIBLE else View.GONE
            if (pos != null && pos.second > 0) { h.prog.visibility = View.VISIBLE; h.prog.progress = (pos.first * 1000 / pos.second).toInt() }
            else h.prog.visibility = View.GONE
            Posters.load(h.pic, images.getOrNull(e.index)?.ifEmpty { null }, Ui.dp(this@VodPlayerActivity, 68), h.num, wide = true)
        }
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
