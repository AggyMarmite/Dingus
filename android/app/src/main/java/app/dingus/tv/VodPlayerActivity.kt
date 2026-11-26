package app.dingus.tv

import kotlinx.coroutines.launch
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
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
    private var triedBackup = false
    private var guess = 0 // the file didn't say what it is: 1 = try it as HLS, 2 = as DASH (Players.item)
    // Sound but no picture (some 4K / HDR files on the TV's video layer): after a few seconds, draw the picture
    // the way the rest of the screen is drawn instead, and say what the file is.
    private var rendered = false
    private var textured = false
    private var texture: android.view.TextureView? = null
    private val noPicture = Runnable { checkPicture() }
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
        findViewById<View>(R.id.leftShade).background = android.graphics.drawable.GradientDrawable(
            android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(0xB305070A.toInt(), 0x6605070A, 0))
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
        findViewById<View>(R.id.upNextPlay).setOnClickListener { playIndex(index + 1) }
        findViewById<View>(R.id.upNextEpisodes).setOnClickListener { openEpisodes() }
        showTitle()
    }

    /** The VPN or network is about to change: close the stream cleanly (keeping the place), then carry on (Streams). */
    private var closedAt = -1L
    private val stream = Streams.Playing(
        close = { player?.let { closedAt = if (skipTo >= 0) skipTo else it.currentPosition; it.stop(); message.text = "Reconnecting…" } },
        reopen = { if (player != null && closedAt >= 0) { val t = closedAt; closedAt = -1L; open(t) } },
    )

    override fun onStart() {
        super.onStart()
        if (ids.isEmpty()) return
        Streams.add(stream)
        val p = Players.create(this)
        Players.watchFrameRate(this, p)
        p.addListener(object : Player.Listener {
            override fun onPlaybackStateChanged(state: Int) {
                when (state) {
                    // Buffering again after it started – not because of a skip (that has to fill up again
                    // wherever it lands): the VPN may move to a quicker server.
                    Player.STATE_BUFFERING -> if (rendered && skipTo < 0 && !afterSkip && player?.playWhenReady == true) { Ui.test("vod-rebuffer"); SlowVpn.onBuffering(this@VodPlayerActivity, "vod:${ids[index]}") }
                    Player.STATE_READY -> {
                        afterSkip = false
                        retries = 0; triedBackup = false
                        message.text = ""
                        if (!rendered) { handler.removeCallbacks(noPicture); handler.postDelayed(noPicture, 4_000) }
                        Ui.test("vod-playing ${titles[index]}")
                        Account.soon()
                    }
                    Player.STATE_ENDED -> finishedOne()
                    else -> {}
                }
            }
            override fun onIsPlayingChanged(isPlaying: Boolean) { tickNow() }
            override fun onPositionDiscontinuity(old: Player.PositionInfo, new: Player.PositionInfo, reason: Int) {
                if (reason == Player.DISCONTINUITY_REASON_SEEK || reason == Player.DISCONTINUITY_REASON_SEEK_ADJUSTMENT) {
                    afterSkip = true; Ui.test("vod-skipped")
                }
            }
            override fun onRenderedFirstFrame() {
                rendered = true
                handler.removeCallbacks(noPicture)
                if (message.text.startsWith("No picture") || message.text.startsWith("There's no picture")) message.text = ""
                Ui.test("vod-first-frame${if (textured) " (texture)" else ""}")
            }
            override fun onPlayerError(error: PlaybackException) {
                Ui.test("vod-error ${error.errorCodeName}")
                val pos = player?.currentPosition ?: 0
                if (Players.unrecognised(error) && guess < 2) { guess++; Ui.test("vod-format-guess $guess"); open(pos); return }
                if (retries++ < 2) {
                    message.text = "Reconnecting…"
                    handler.postDelayed({ open(pos) }, 1_500L * retries)
                } else if (!triedBackup && Failover.hasBackups() && Players.serverDown(error)) {
                    triedBackup = true
                    message.text = "Trying a backup server…"
                    Thread {
                        val ok = Failover.recover()
                        runOnUiThread { if (ok) { retries = 0; open(pos) } else { message.text = cantPlay(error); Failover.vpnElsewhere() } }
                    }.start()
                } else {
                    message.text = cantPlay(error)
                    if (Players.serverDown(error) || Players.refused(error) || Players.network(error)) {
                        Streams.failedAt = System.currentTimeMillis(); Account.soon() // (the menu's status says so)
                        // Through a VPN: find a location this plays through; it picks up where it was once found.
                        if (Vpn.status == Vpn.Status.ON) { closedAt = pos; Vpn.streamBlocked(Failover.rewrite(urls[index])) } else Failover.vpnElsewhere()
                    }
                }
            }
        })
        video.player = p
        player = p
        PictureSize.apply(video, PictureSize.get(null))
        textured = false
        texture?.visibility = View.GONE
        video.videoSurfaceView?.visibility = View.VISIBLE
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
        Streams.remove(stream)
        FrameRate.reset(this)
        handler.removeCallbacksAndMessages(null)
        savePosition()
        resumeAt = if (skipTo >= 0) skipTo else player?.currentPosition ?: -1L
        skipTo = -1L
        // Let go of the stream completely (its connection closes with it), then look at the connections again.
        if (player != null) Account.soon()
        player?.release()
        player = null
        video.player = null
    }

    /** Called a few seconds after playing starts: no first frame means a black screen, so try the other surface. */
    private fun checkPicture() {
        val p = player ?: return
        val f = p.videoFormat ?: return // no video track (or not chosen yet): nothing to show
        if (rendered || p.playbackState != Player.STATE_READY || !p.playWhenReady) return
        val hdr = f.colorInfo?.colorTransfer.let {
            if (it == androidx.media3.common.C.COLOR_TRANSFER_ST2084) " · HDR10/Dolby Vision"
            else if (it == androidx.media3.common.C.COLOR_TRANSFER_HLG) " · HLG" else ""
        }
        val info = "${f.sampleMimeType?.removePrefix("video/") ?: "?"} ${f.width}x${f.height}$hdr · ${Players.lastDecoder.ifEmpty { "decoder ?" }}"
        Log.w("Dingus", "No picture: $info")
        if (textured) { message.text = "There's no picture for this one on this Fire TV.\nPress BACK to go back."; Ui.test("vod-no-picture $info"); return }
        textured = true
        message.text = "No picture yet, trying another way…"
        Ui.test("vod-texture-fallback $info")
        // The texture goes inside the player view's own picture frame, so the picture keeps its shape.
        val frame = video.findViewById<android.view.ViewGroup>(androidx.media3.ui.R.id.exo_content_frame) ?: video
        val tv = texture ?: android.view.TextureView(this).also {
            frame.addView(it, android.widget.FrameLayout.LayoutParams(android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.MATCH_PARENT))
            texture = it
        }
        tv.visibility = View.VISIBLE
        video.videoSurfaceView?.visibility = View.GONE
        p.setVideoTextureView(tv)
        handler.postDelayed(noPicture, 5_000)
    }

    private fun cantPlay(e: PlaybackException): String {
        Log.w("Dingus", "Won't play: ${e.errorCodeName}", e)
        return "This won't play right now.\n${Players.plain(e)}\nPress BACK to go back."
    }

    // ------------------------------------------------------------------ skipping the intro (TheIntroDB)

    private val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.Main)
    private var segments: Intros.Segments? = null
    private var segmentsFor = -1

    /** Looks up where this episode's intro, recap and credits are. */
    private fun lookUpIntro() {
        if (kind != Vod.SERIES || segmentsFor == index) return
        segmentsFor = index; segments = null
        val e = eps.getOrNull(index)?.takeIf { it.season > 0 } ?: return
        val sid = showId ?: return
        val i = index
        scope.launch {
            val tmdb = Vod.cachedDetails(Vod.SERIES, sid)?.tmdbId?.takeIf { it.isNotBlank() && it != "0" }
                ?: Vod.cached(Vod.SERIES)?.byId?.get(sid)?.let { kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { TitleLogos.tmdbIdFor(Vod.SERIES, it, null) } }
                ?: return@launch
            val s = Intros.find(tmdb, e.season, e.number)
            if (index == i) segments = s
        }
    }

    /** The part to skip right now (intro or recap), with its button's words. */
    private fun skippable(pos: Long): Pair<Intros.Part, String>? {
        val s = segments ?: return null
        for ((p, name) in listOfNotNull(s.recap?.let { it to "Skip recap" }, s.intro?.let { it to "Skip intro" })) {
            val end = if (p.end < 0) Long.MAX_VALUE else p.end
            if (pos >= p.start && pos < end - 2_000) return p to name
        }
        return null
    }

    private fun skipPart() {
        val p = player ?: return
        val (part, _) = skippable(p.currentPosition) ?: return
        val len = p.duration.takeIf { it > 0 } ?: return
        p.seekTo(if (part.end < 0) len - 1_000 else part.end)
        findViewById<View>(R.id.skipIntro).visibility = View.GONE
        Ui.test("skip-intro")
    }

    override fun onDestroy() { scope.coroutineContext[kotlinx.coroutines.Job]?.cancel(); super.onDestroy() }

    private fun open(at: Long) {
        if (Vpn.holding) { closedAt = at; message.text = "Waiting for the VPN to reconnect…"; return } // (kill switch: Streams reopens it)
        val p = player ?: return
        rendered = false
        handler.removeCallbacks(noPicture)
        handler.postDelayed(noPicture, 5_000)
        p.setMediaItem(Players.item(Failover.rewrite(urls[index]), guess), at)
        lookUpIntro()
        p.prepare()
        p.playWhenReady = true
        showTitle()
        showControls()
    }

    /** "S02 E01" for the episode playing (null for a film). */
    private fun episodeLabel(): String? {
        if (kind != Vod.SERIES) return null
        val ep = Regex("""\sS(\d+) E(\d+)""").find(titles.getOrNull(index) ?: return null) ?: return null
        return "S%02d E%02d".format(ep.groupValues[1].toInt(), ep.groupValues[2].toInt())
    }

    /**
     * The details over the bar, as on the pause screen: "SEASON 1 · EPISODE 3 · THE FERRY" over the show's logo (or
     * its name), then year / rating / genre and two lines of the description. (Fetched if they aren't known yet.)
     */
    private fun showTitle() {
        val t = titles[index]
        val ep = Regex("""^(.*?)\s+·\s+S(\d+) E(\d+)\s*(.*)$""").find(t)
        val subV = findViewById<TextView>(R.id.vodSub)
        val titleV = findViewById<TextView>(R.id.vodTitle)
        val logoV = findViewById<android.widget.ImageView>(R.id.vodLogo)
        val metaV = findViewById<TextView>(R.id.vodMeta)
        val plotV = findViewById<TextView>(R.id.vodPlot)
        val id = ids[index]
        val series = kind == Vod.SERIES && ep != null
        val detKind = if (series) Vod.SERIES else kind
        val item = when {
            series -> showId?.let { sid -> Vod.cached(Vod.SERIES)?.byId?.get(sid) }
            kind == Vod.MOVIES -> Vod.cached(kind)?.byId?.get(id)
            else -> null
        }
        val d = item?.let { Vod.cachedDetails(detKind, it.id) }
        if (series) {
            val (show, season, num, name) = ep!!.destructured
            subV.text = listOfNotNull("SEASON $season", "EPISODE $num", name.takeIf { it.isNotBlank() }?.uppercase()).joinToString("  ·  ")
            titleV.text = show
        } else {
            // (The details screen's own line – "FILM · 2023 · ★ 6.1" – only when the details themselves aren't known.)
            subV.text = when (kind) { CATCHUP -> sub ?: "CATCH UP"; Vod.SERIES -> "TV SHOW"; else -> if (d == null && sub != null) sub else "FILM" }
            titleV.text = t
        }
        TitleLogos.show(logoV, titleV, item?.let { TitleLogos.cached(detKind, it) })
        if (item != null && d != null) {
            val genre = d.genre?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() }?.take(2)?.joinToString(", ")
            metaV.text = Badges.line(this, d.year.takeIf { it > 0 } ?: item.year, Ratings.cached(detKind, item), d.rating?.toFloatOrNull() ?: item.rating,
                listOfNotNull(genre), metaV.textSize)
            metaV.visibility = View.VISIBLE
            val plot = if (series) d.episodes.firstOrNull { it.id == id }?.plot?.takeIf { it.isNotBlank() } ?: d.plot else d.plot
            plotV.text = plot ?: ""
            plotV.visibility = if (plot.isNullOrBlank()) View.GONE else View.VISIBLE
        } else {
            metaV.visibility = View.GONE; plotV.visibility = View.GONE
            // Not known yet (straight from Continue watching, say): fetched, then shown.
            if (item != null && detailsAsked.add("$detKind:${item.id}")) scope.launch {
                try { Vod.details(detKind, item) } catch (_: Exception) { return@launch }
                if (!isDestroyed && ids.getOrNull(index) == id) showTitle()
            }
        }
        if (item != null && Ratings.cached(detKind, item) == null && d != null && ratingsAsked.add("$detKind:${item.id}")) scope.launch {
            Ratings.find(detKind, item, d.tmdbId) ?: return@launch
            if (!isDestroyed && ids.getOrNull(index) == id) showTitle()
        }
        if (item != null && TitleLogos.enabled && TitleLogos.cached(detKind, item) == null && logosAsked.add("$detKind:${item.id}")) scope.launch {
            try { TitleLogos.find(detKind, item, d?.tmdbId) } catch (_: Exception) { null } ?: return@launch
            if (!isDestroyed && ids.getOrNull(index) == id) showTitle()
        }
        Ui.test("vod-details ${titleV.text} meta ${metaV.visibility == View.VISIBLE} plot ${plotV.visibility == View.VISIBLE}")
    }
    private val detailsAsked = HashSet<String>()
    private val ratingsAsked = HashSet<String>()
    private val logosAsked = HashSet<String>()

    private fun savePosition() {
        if (kind == CATCHUP) return
        val p = player ?: return
        val len = p.duration
        if (len == C.TIME_UNSET || len <= 0) return
        Vod.savePosition(ids[index], p.currentPosition, len, kind, showId, episodeLabel())
    }

    /** End of a film or episode: the next episode if that's set to happen, otherwise back to the details. */
    private fun finishedOne() {
        if (kind != CATCHUP) player?.let { if (it.duration > 0) Vod.savePosition(ids[index], it.duration, it.duration, kind, showId, episodeLabel()) }
        if (kind == Vod.SERIES && index + 1 < ids.size && Store.autoNext) playIndex(index + 1) else finish()
    }

    private fun playIndex(i: Int) {
        if (i !in ids.indices) return
        savePosition()
        index = i
        resumeAt = -1
        guess = 0; retries = 0; triedBackup = false; skipTo = -1L
        upNext.visibility = View.GONE
        closeEpisodes()
        Ui.test("vod-episode $i")
        open(Vod.position(ids[i])?.first ?: 0L)
    }

    // ------------------------------------------------------------------ the controls

    private val hideControls = Runnable { hideControlsNow() }
    /** The "Next episode" card comes up this long before the end. */
    private val UP_NEXT_MS = 25_000L

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

    private var lastBuf = -1L
    private fun tickNow() {
        val p = player ?: return
        if (p.bufferedPosition != lastBuf && lastBuf >= 0 && p.playWhenReady) Streams.dataAt = System.currentTimeMillis()
        lastBuf = p.bufferedPosition
        val len = p.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: 0L
        val pos = if (skipTo >= 0) skipTo else p.currentPosition // (while skipping: where you're going)
        bar.duration = len; bar.position = pos; bar.buffered = p.bufferedPosition; bar.playing = p.playWhenReady
        bar.invalidate()
        showPos(pos)
        findViewById<TextView>(R.id.vodLeft).text = if (len > 0) "−${PlayBar.clock(len - pos)}" else ""
        findViewById<TextView>(R.id.vodRemain).text = if (len > 0) "${PauseScreen.left(len - pos)}  ·  ends ${Ui.time(System.currentTimeMillis() + (len - pos))}" else ""
        findViewById<TextView>(R.id.vodClock).text = Ui.time(System.currentTimeMillis())
        // Paused with the controls hidden: the small pill and thin line.
        val paused = !p.playWhenReady && controls.visibility != View.VISIBLE && epPanel.visibility != View.VISIBLE
        if (!p.playWhenReady && epPanel.visibility != View.VISIBLE) { if (pausedSince == 0L) pausedSince = System.currentTimeMillis() } else pausedSince = 0L
        val rich = paused && pausedSince > 0 && System.currentTimeMillis() - pausedSince > 5_000 && upNext.visibility != View.VISIBLE
        if (rich) { if (pauseScreen?.showing != true) pauseDetails(); pauseScreen?.time(pos, len) } else pauseScreen?.hide()
        pausedPill.visibility = if (paused && !rich) View.VISIBLE else View.GONE
        pausedLine.visibility = pausedPill.visibility
        if (paused) {
            pausedPill.text = android.text.SpannableStringBuilder("❚❚  Paused").apply {
                setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), 0, length, 0)
                if (len > 0) { val st = length; append("  ·  ${PlayBar.clock(pos)} / ${PlayBar.clock(len)}")
                    setSpan(android.text.style.ForegroundColorSpan(0xFF8B94A7.toInt()), st, length, 0) }
            }
            pausedLine.progress = if (len > 0) (pos * 1000 / len).toInt() else 0
        }
        // During the intro or recap: Skip intro (OK while the controls are hidden).
        val skip = skippable(pos)
        findViewById<TextView>(R.id.skipIntro).apply {
            val show = skip != null && epPanel.visibility != View.VISIBLE && upNext.visibility != View.VISIBLE
            if (show) text = "${skip!!.second}  ⏭"
            if (show && visibility != View.VISIBLE) Ui.test("skip-intro shown")
            visibility = if (show) View.VISIBLE else View.GONE
        }
        // Near the end of an episode (or where its credits start): the next one, with a countdown when Auto-play is on.
        val creditsAt = segments?.credits?.start?.takeIf { it > len / 2 && len - it > UP_NEXT_MS }
        val nearEnd = kind == Vod.SERIES && index + 1 < ids.size && len > 5 * 60_000L &&
            (len - pos in 1..UP_NEXT_MS || creditsAt != null && pos >= creditsAt && pos < len)
        if (nearEnd && upNextDismissed != index && upNext.visibility != View.VISIBLE && epPanel.visibility != View.VISIBLE) {
            upNext.visibility = View.VISIBLE
            val n = Regex("""S(\d+) E(\d+)\s*(.*)$""").find(titles[index + 1])
            findViewById<TextView>(R.id.upNextTitle).text = n?.let { m ->
                val (_, num, name) = m.destructured; if (name.isBlank()) "Episode $num" else "Episode $num  ·  $name" } ?: titles[index + 1]
            findViewById<TextView>(R.id.upNextSub).text = n?.let { m -> "S${m.groupValues[1]}  ·  E${m.groupValues[2]}" } ?: ""
            findViewById<View>(R.id.upNextTrack).visibility = if (Store.autoNext) View.VISIBLE else View.GONE
            Posters.load(findViewById(R.id.upNextPic), images.getOrNull(index + 1)?.ifEmpty { null }, Ui.dp(this, 150), wide = true)
            findViewById<View>(R.id.upNextPlay).requestFocus()
            Ui.test("vod-up-next")
        } else if (!nearEnd && upNext.visibility == View.VISIBLE) upNext.visibility = View.GONE
        if (upNext.visibility == View.VISIBLE) {
            val left = (len - pos).coerceIn(0L, UP_NEXT_MS)
            findViewById<View>(R.id.upNextFill).apply { pivotX = 0f; scaleX = left.toFloat() / UP_NEXT_MS }
            findViewById<TextView>(R.id.upNextPlay).text = if (Store.autoNext) "Play now  ·  ${(left + 999) / 1000}" else "Play now"
        }
    }

    private var skipFrom = -1L
    private val endSkip = Runnable { skipFrom = -1L; bar.skipped = null; bar.invalidate(); showPos(bar.position) }

    /** The time, bottom left – with how far you're jumping ("+0:30", in yellow) while skipping. */
    private fun showPos(pos: Long) {
        val sk = bar.skipped
        val t = android.text.SpannableStringBuilder(PlayBar.clock(pos))
        if (sk != null && sk != 0L) {
            val at = t.length
            t.append("   ").append(if (sk < 0) "−" else "+").append(PlayBar.clock(kotlin.math.abs(sk)))
            t.setSpan(android.text.style.ForegroundColorSpan(0xFFFBBF24.toInt()), at, t.length, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
        findViewById<TextView>(R.id.vodPos).text = t
    }

    /** Where the skip presses add up to: the player only jumps once they stop (held ◀/▶ repeats many times a second). */
    private var skipTo = -1L
    /** Just skipped (or restarted, or jumped past the intro), until it's playing again: buffering then is expected. */
    private var afterSkip = false
    private val doSkip = Runnable { val t = skipTo; skipTo = -1L; if (t >= 0) player?.seekTo(t) }

    private fun skip(by: Long) {
        val p = player ?: return
        if (skipFrom < 0) skipFrom = p.currentPosition
        val len = p.duration
        var t = (if (skipTo >= 0) skipTo else p.currentPosition) + by
        if (len > 0) t = t.coerceAtMost(len - 1_000)
        t = t.coerceAtLeast(0)
        skipTo = t
        handler.removeCallbacks(doSkip); handler.postDelayed(doSkip, 450)
        showControls(focusBar = true)
        bar.position = t; bar.skipped = t - skipFrom; bar.invalidate(); showPos(t)
        handler.removeCallbacks(endSkip); handler.postDelayed(endSkip, 1_500)
        Ui.test("vod-skip ${by / 1000}")
    }

    // The rich pause screen: after a few seconds paused with the controls away.
    private var pauseScreen: PauseScreen? = null
    private var pausedSince = 0L

    private fun pauseDetails() {
        val screen = pauseScreen ?: PauseScreen(this).also { pauseScreen = it }
        val t = titles.getOrNull(index) ?: return
        val ep = Regex("""^(.*?)\s+·\s+S(\d+) E(\d+)\s*(.*)$""").find(t)
        val id = ids.getOrNull(index) ?: return
        if (kind == Vod.SERIES && ep != null) {
            val (show, season, num, name) = ep.destructured
            val d = showId?.let { Vod.cachedDetails(Vod.SERIES, it) }
            val plot = d?.episodes?.firstOrNull { it.id == id }?.plot?.takeIf { it.isNotBlank() } ?: d?.plot
            val item = showId?.let { sid -> Vod.cached(Vod.SERIES)?.byId?.get(sid) }
            screen.show(listOfNotNull("SEASON $season", "EPISODE $num", name.takeIf { it.isNotBlank() }?.uppercase()).joinToString("  ·  "),
                show, item?.let { TitleLogos.cached(Vod.SERIES, it) }, plot)
        } else {
            val d = if (kind == Vod.MOVIES) Vod.cachedDetails(kind, id) else null
            val item = if (kind == Vod.MOVIES) Vod.cached(kind)?.byId?.get(id) else null
            // (The player's own line when it has one – "FILM · 2023 · ★ 6.1" – so the details aren't said twice.)
            screen.show(sub ?: listOfNotNull(if (kind == CATCHUP) "CATCH UP" else "FILM", d?.year?.takeIf { it > 0 }?.toString(), d?.genre?.split(',')?.firstOrNull()?.trim()?.uppercase()).joinToString("  ·  "),
                t, item?.let { TitleLogos.cached(kind, it) }, d?.plot)
        }
    }

    private fun togglePause() {
        val p = player ?: return
        p.playWhenReady = !p.playWhenReady
        Ui.test("vod-${if (p.playWhenReady) "play" else "pause"}")
        if (p.playWhenReady) showControls() else { showControls(); }
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (backHold(event)) return true
        val p = player ?: return super.dispatchKeyEvent(event)
        val code = event.keyCode
        val down = event.action == KeyEvent.ACTION_DOWN
        // The Episodes list: it moves about by itself; BACK closes it.
        if (epPanel.visibility == View.VISIBLE) {
            if (code == KeyEvent.KEYCODE_BACK) { if (!down) closeEpisodes(); return true }
            return super.dispatchKeyEvent(event)
        }
        // The pause screen: ▶ or OK carries on; anything else brings back the controls.
        if (pauseScreen?.showing == true) {
            if (down && event.repeatCount == 0) {
                pauseScreen?.hide(); pausedSince = 0L
                when (code) {
                    KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MEDIA_PLAY, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE -> togglePause()
                    else -> showControls(focusBar = true)
                }
            }
            return true
        }
        // Skip intro showing and the controls hidden: OK skips.
        if ((code == KeyEvent.KEYCODE_DPAD_CENTER || code == KeyEvent.KEYCODE_ENTER) && controls.visibility != View.VISIBLE &&
            findViewById<View>(R.id.skipIntro).visibility == View.VISIBLE) {
            if (!down && event.repeatCount == 0) skipPart()
            return true
        }
        // The Up next card, when it has the remote: OK plays it, BACK puts it away.
        if (upNext.visibility == View.VISIBLE && upNext.hasFocus()) {
            val onEpisodes = findViewById<View>(R.id.upNextEpisodes).hasFocus()
            fun putAway() { upNextDismissed = index; upNext.visibility = View.GONE; showControls(focusBar = true) }
            when (code) {
                KeyEvent.KEYCODE_BACK -> { if (!down) putAway(); return true }
                // Play now is the card's OK; Episodes is a button, so it works as one.
                KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER -> {
                    if (onEpisodes) return super.dispatchKeyEvent(event)
                    if (!down && event.repeatCount == 0) playIndex(index + 1)
                    return true
                }
                KeyEvent.KEYCODE_DPAD_RIGHT -> return super.dispatchKeyEvent(event)
                KeyEvent.KEYCODE_DPAD_LEFT -> if (onEpisodes) return super.dispatchKeyEvent(event) else { if (down) putAway(); return true }
                KeyEvent.KEYCODE_DPAD_DOWN -> { if (down) putAway(); return true }
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
            KeyEvent.KEYCODE_MENU -> if (event.repeatCount == 0) {
                val items = listOf("Audio & subtitles", "Picture size  ·  ${PictureSize.label(PictureSize.get(null))}")
                Dialogs.builder(this).setItems(items.toTypedArray()) { _, i ->
                    if (i == 0) Tracks.choose(this, p) else PictureSize.choose(this, null, "films and episodes") { PictureSize.apply(video, it) }
                }.show()
            }
            KeyEvent.KEYCODE_DPAD_LEFT -> if (onBar) skip(-step) else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_RIGHT -> if (onBar) skip(step) else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER ->
                if (onBar) { if (event.repeatCount == 0) { togglePause(); bar.requestFocus() } } else return super.dispatchKeyEvent(event)
            KeyEvent.KEYCODE_DPAD_DOWN -> if (!shown || bar.isFocused) {
                // ▼ goes to the buttons (bringing the controls up first if they're hidden).
                showControls()
                val row = findViewById<android.view.ViewGroup>(R.id.vodButtons)
                (0 until row.childCount).map { row.getChildAt(it) }.firstOrNull { it.visibility == View.VISIBLE }?.requestFocus()
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
        upNextDismissed = index // (so the card doesn't pop back up behind the list)
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
            b.text = if (s > 0) "Season $s" else "Episodes"; b.textSize = 13f; b.tag = s
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
