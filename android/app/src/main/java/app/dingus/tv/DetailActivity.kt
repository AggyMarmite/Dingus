package app.dingus.tv

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** A film or TV show: details, play/resume, and for shows the seasons and episodes. */
class DetailActivity : BaseActivity() {
    companion object {
        const val KIND = "kind"
        const val ID = "id"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var kind: String
    private var item: VodItem? = null
    private var details: Details? = null
    /** A Coming soon title (not in the playlist yet): just its trailer and "Let me know when it's here". */
    private var coming: Upcoming.Item? = null
    private var season = -1
    private val episodes = EpisodeAdapter()
    private lateinit var play: TextView
    private lateinit var restart: TextView
    private lateinit var status: TextView
    private lateinit var episodeList: RecyclerView

    override fun finish() { super.finish(); Motion.closing(this) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_detail)
        if (savedInstanceState == null) Motion.rise(findViewById(R.id.title), findViewById(R.id.meta), findViewById(R.id.plot), findViewById(R.id.buttons))
        kind = intent.getStringExtra(KIND) ?: Vod.MOVIES
        play = findViewById(R.id.play)
        restart = findViewById(R.id.restart)
        status = findViewById(R.id.status)
        episodeList = findViewById(R.id.episodes)
        // (The same glide as the rows elsewhere, with the next episodes kept ready so moving along never jumps.)
        episodeList.layoutManager = RowLayoutManager(this)
        episodeList.adapter = episodes
        episodeList.itemAnimator = null

        play.setOnClickListener { playMain(fromStart = false) }
        restart.setOnClickListener { playMain(fromStart = true) }
        findViewById<TextView>(R.id.myList).setOnClickListener {
            coming?.let { u ->
                val on = !Upcoming.hasAlert(u.tmdbId)
                Upcoming.setAlert(u, on)
                Notice.done(if (on) "🔔  We'll let you know when ${u.title} is here" else "Alert cancelled", 3_000)
                refreshButtons(); return@setOnClickListener
            }
            val m = item ?: return@setOnClickListener
            Vod.setInMyList(kind, m.id, !Vod.inMyList(kind, m.id))
            Ui.test("my-list ${m.name} ${Vod.inMyList(kind, m.id)}")
            refreshButtons()
        }
        findViewById<TextView>(R.id.watched).setOnClickListener {
            val m = item ?: return@setOnClickListener
            Vod.setWatched(m.id, !Vod.watched(m.id), kind)
            Ui.test("watched ${m.name} ${Vod.watched(m.id)}")
            refreshButtons()
        }
        play.requestFocus()
        // The picture behind: darker on the left (under the details) and along the bottom (under the rows).
        val bgc = Themes.color(this, R.attr.dBg); val clear = bgc and 0x00FFFFFF
        findViewById<View>(R.id.detailShade).background = android.graphics.drawable.LayerDrawable(arrayOf(
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
                intArrayOf(clear or (0xF0 shl 24), clear or (0xB8 shl 24), clear or (0x33 shl 24), clear)),
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP,
                intArrayOf(bgc, clear or (0xC0 shl 24), clear, clear)),
        ))

        val id = intent.getStringExtra(ID)
        if (id != null && id.startsWith("tmdb:")) { showComing(id); return }
        // Straight away when the films are already loaded (they almost always are): the title, picture and – if
        // they've been looked up before – the details, all before the first frame, so the page is never blank.
        Vod.cached(kind)?.byId?.get(id)?.let { m ->
            item = m
            showBasics(m)
            Vod.cachedDetails(kind, m.id)?.let { details = it; showDetails() }
        }
        scope.launch {
            val lib = try { Vod.library(kind) } catch (e: Exception) { status.text = "Couldn't load this: ${Net.friendly(e)}\nPress BACK to go back."; return@launch }
            val it = lib.byId[id] ?: run {
                Notice.done(if (kind == Vod.MOVIES) "That film isn't in your playlist any more" else "That show isn't in your playlist any more", 4_000)
                finish(); return@launch
            }
            if (item?.id != it.id) { item = it; showBasics(it) }
            if (details == null) status.text = "Loading details…"
            try {
                details = Vod.details(kind, it)
                status.text = ""
                showDetails()
                Ui.test("details ${it.name} episodes ${details?.episodes?.size}")
            } catch (e: Exception) {
                status.text = "Couldn't load the details: ${Net.friendly(e)}"
                if (kind == Vod.SERIES) play.visibility = View.GONE
            }
            refreshButtons()
            if (play.visibility != View.VISIBLE) findViewById<View>(R.id.myList).requestFocus() // (focus was on Play)
        }
    }

    /** Coming soon: its picture, logo, details, cast and trailer, and the alert – no Play or My list. */
    private fun showComing(id: String) {
        val u = Upcoming.find(id) ?: run { finish(); return }
        coming = u
        item = u.vodItem
        showBasics(u.vodItem)
        details = Upcoming.details(u)
        showDetails()
        status.text = "Coming ${Upcoming.shortDate(u)}"
        play.visibility = View.GONE; restart.visibility = View.GONE
        findViewById<View>(R.id.watched).visibility = View.GONE
        refreshButtons()
        findViewById<View>(R.id.myList).requestFocus()
        Ui.test("coming-page ${u.title}")
    }

    private fun showBasics(m: VodItem) {
        findViewById<TextView>(R.id.title).text = m.name
        findViewById<TextView>(R.id.fallback).text = m.name
        findViewById<TextView>(R.id.meta).text = meta(m.year, null, m.rating.takeIf { it > 0 }?.let { "%.1f".format(it) }, null)
        // The wide picture if it's already known; until then the poster behind (better than nothing).
        val wide = TitleLogos.cachedBackdrop(kind, m) ?: Vod.cachedDetails(kind, m.id)?.backdrop
        Posters.load(findViewById<ImageView>(R.id.backdrop), wide ?: m.poster, resources.displayMetrics.heightPixels / 2, wide = wide != null)
    }

    private fun meta(year: Int, duration: String?, rating: String?, genre: String?) = listOfNotNull(
        year.takeIf { it > 0 }?.toString(),
        duration?.let { niceDuration(it) },
        rating?.toFloatOrNull()?.takeIf { it > 0 }?.let { "★ %.1f".format(it) },
        genre,
    ).joinToString("  ·  ")

    /** "01:52:10" or "112 min" → "1 h 52 min". */
    private fun niceDuration(s: String): String? {
        val parts = s.split(':').mapNotNull { it.trim().toIntOrNull() }
        val mins = when {
            parts.size == 3 -> parts[0] * 60 + parts[1]
            parts.size == 2 -> parts[0]
            else -> Regex("""\d+""").find(s)?.value?.toIntOrNull() ?: return null
        }
        if (mins <= 0) return null
        return if (mins >= 60) "${mins / 60} h ${mins % 60} min" else "$mins min"
    }

    private fun showDetails() {
        val d = details ?: return
        val m = item ?: return
        d.title?.let { findViewById<TextView>(R.id.title).text = it }
        val metaView = findViewById<TextView>(R.id.meta)
        // The year, TMDB's UK age rating (a badge) and score (TMDB's mark), then the length and genre.
        val provScore = (d.rating?.toFloatOrNull() ?: m.rating)
        fun withRatings(r: Ratings.Info?): CharSequence =
            Badges.line(this, d.year, r, provScore, listOfNotNull(d.duration?.let { niceDuration(it) }, d.genre), metaView.textSize)
        metaView.text = withRatings(Ratings.cached(kind, m))
        if (Ratings.cached(kind, m) == null) scope.launch { Ratings.find(kind, m, d.tmdbId)?.let { if (!isDestroyed) metaView.text = withRatings(it) } }
        val plot = findViewById<TextView>(R.id.plot)
        plot.text = d.plot ?: ""
        val people = findViewById<TextView>(R.id.people)
        people.text = d.director?.let { "Director: $it" } ?: d.cast?.let { "Cast: $it" } ?: ""
        people.visibility = if (people.text.isEmpty()) View.GONE else View.VISIBLE
        d.backdrop?.let { Posters.load(findViewById<ImageView>(R.id.backdrop), it, resources.displayMetrics.heightPixels, wide = true) } // (full screen: full size)
        // The official title logo (from TMDB), in place of the plain title.
        TitleLogos.cached(kind, m)?.let { TitleLogos.show(findViewById(R.id.titleLogo), findViewById(R.id.title), it) }
        // The cast: under the buttons on a film; under the episodes on a show (the page scrolls down to it).
        if (kind == Vod.SERIES) castUnderEpisodes()
        showCast(m, d.tmdbId)
        // A trailer (TMDB's, from YouTube), played inside Dingus, when there's one.
        if (TitleLogos.enabled) scope.launch {
            val key = Tmdb.trailer(kind, m, d.tmdbId) ?: return@launch
            if (isDestroyed) return@launch
            findViewById<TextView>(R.id.trailer).apply {
                visibility = View.VISIBLE
                setOnClickListener { Ui.test("trailer-play"); if (!Tmdb.openInYouTubeApp(this@DetailActivity, key)) TrailerActivity.open(this@DetailActivity, key, m.name) }
            }
        }
        if (TitleLogos.enabled) scope.launch {
            TitleLogos.find(kind, m, d.tmdbId)?.let { TitleLogos.show(findViewById(R.id.titleLogo), findViewById(R.id.title), it) }
        }

        if (kind == Vod.SERIES && d.episodes.isNotEmpty()) Vod.clearNewEpisode(m.id, d.episodes.size)
        if (kind == Vod.SERIES) {
            plot.maxLines = 2
            people.visibility = View.GONE
            findViewById<View>(R.id.castHead).visibility = View.GONE
            val seasons = d.episodes.map { it.season }.distinct()
            if (seasons.isEmpty()) { status.text = "No episodes yet."; play.visibility = View.GONE; return }
            val row = findViewById<LinearLayout>(R.id.seasons)
            row.removeAllViews()
            for (s in seasons) {
                val b = LayoutInflater.from(this).inflate(R.layout.item_season, row, false) as TextView
                b.text = if (s > 0) "Season $s" else "Episodes"
                b.tag = s
                // A season changes only with OK (highlighting one while moving about no longer switches it).
                b.setOnClickListener { showSeason(s); focusEpisode() }
                row.addView(b)
            }
            // ▲ from the episodes lands on the season being shown, not whichever chip happens to be above.
            row.viewTreeObserver.addOnGlobalFocusChangeListener { old, new ->
                if (old == null || new == null || new.parent !== row || new.isActivated) return@addOnGlobalFocusChangeListener
                var p: android.view.ViewParent? = old.parent
                while (p != null && p !== episodeList) p = p.parent
                if (p === episodeList) (0 until row.childCount).map { row.getChildAt(it) }.firstOrNull { it.isActivated }?.requestFocus()
            }
            findViewById<View>(R.id.seasonsScroll).visibility = View.VISIBLE
            findViewById<View>(R.id.episodeHint).visibility = View.VISIBLE
            episodeList.visibility = View.VISIBLE
            val last = item?.let { Vod.lastEpisode(it.id) }
            showSeason(d.episodes.firstOrNull { it.id == last }?.season ?: seasons.first())
        }
    }

    private fun showSeason(s: Int) {
        val d = details ?: return
        if (s == season) return
        season = s
        val row = findViewById<LinearLayout>(R.id.seasons)
        for (i in 0 until row.childCount) row.getChildAt(i).isActivated = row.getChildAt(i).tag == s
        val list = d.episodes.filter { it.season == s }
        episodes.set(list)
        // Episodes the provider sent without a description (or picture): filled in from TMDB.
        val m = item
        if (m != null && list.any { it.plot.isNullOrBlank() }) scope.launch {
            val extra = Tmdb.season(m, d.tmdbId, s)
            if (extra.isEmpty() || season != s) return@launch
            episodes.update(list.map { e ->
                val (plot, still) = extra[e.number] ?: return@map e
                if (!e.plot.isNullOrBlank() && !e.image.isNullOrBlank()) e
                else Episode(e.id, e.title, e.season, e.number, e.ext, e.plot?.takeIf { it.isNotBlank() } ?: plot, e.image?.takeIf { it.isNotBlank() } ?: still, e.duration)
            })
            Ui.test("episodes filled from tmdb ${extra.size}")
        }
    }

    private fun focusEpisode() {
        val last = item?.let { Vod.lastEpisode(it.id) }
        val i = episodes.items.indexOfFirst { it.id == last }.coerceAtLeast(0)
        episodeList.scrollToPosition(i)
        episodeList.post { episodeList.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
    }

    /** The episode "Play" means: the one you were watching (or the one after it, if finished), else the first. */
    private fun nextEpisode(): Episode? {
        val eps = details?.episodes ?: return null
        val last = item?.let { Vod.lastEpisode(it.id) } ?: return eps.firstOrNull()
        val i = eps.indexOfFirst { it.id == last }
        if (i < 0) return eps.firstOrNull()
        return if (Vod.position(last) == null && Vod.watched(last)) eps.getOrNull(i + 1) ?: eps[i] else eps[i]
    }

    private fun refreshButtons() {
        coming?.let { u ->
            // The My list button becomes the alert.
            findViewById<TextView>(R.id.myList).apply {
                visibility = View.VISIBLE
                val on = Upcoming.hasAlert(u.tmdbId)
                text = if (on) "🔔  We'll let you know  ✓" else "🔔  Let me know when it's here"
                isActivated = on
            }
            return
        }
        val m = item ?: return
        findViewById<TextView>(R.id.myList).apply {
            visibility = View.VISIBLE
            val on = Vod.inMyList(kind, m.id)
            text = if (on) "✓  In My list" else "＋  My list"
            isActivated = on
        }
        if (kind == Vod.MOVIES) {
            val pos = Vod.position(m.id)
            play.text = if (pos != null) "▶  Resume from ${clock(pos.first)}" else "▶  Play"
            restart.visibility = if (pos != null) View.VISIBLE else View.GONE
            showResume(pos)
            findViewById<TextView>(R.id.watched).apply {
                visibility = View.VISIBLE
                text = if (Vod.watched(m.id)) "Mark as unwatched" else "Mark as watched"
                isActivated = Vod.watched(m.id)
            }
        } else {
            val e = nextEpisode() ?: return
            val pos = Vod.position(e.id)
            val label = if (e.season > 0) "S${e.season} E${e.number}" else "E${e.number}"
            play.text = if (pos != null) "▶  Resume $label from ${clock(pos.first)}" else "▶  Play $label"
            restart.visibility = if (pos != null) View.VISIBLE else View.GONE
            play.visibility = View.VISIBLE
        }
        episodes.notifyItemRangeChanged(0, episodes.itemCount)
        Ui.test("detail-button ${play.text}")
    }

    /** Part-watched: a bar for how far, and "1 h 09 min left". */
    private fun showResume(pos: Pair<Long, Long>?) {
        val box = findViewById<View>(R.id.resumeBox)
        if (pos == null || pos.second <= 0) { box.visibility = View.GONE; return }
        box.visibility = View.VISIBLE
        findViewById<ProgressBar>(R.id.resumeBar).progress = (pos.first * 1000 / pos.second).toInt()
        findViewById<TextView>(R.id.resumeLeft).text = "${minutes((pos.second - pos.first) / 60_000)} left"
    }

    private fun minutes(m: Long) = if (m >= 60) "${m / 60} h %02d min".format(m % 60) else "$m min"

    private fun clock(ms: Long): String {
        val s = ms / 1000
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }

    private fun playMain(fromStart: Boolean) {
        // (Still loading: say so, rather than nothing happening.)
        val m = item ?: run { Notice.done("Just a moment – still loading", 2_000); return }
        if (kind == Vod.SERIES && details == null) { Notice.done("Just a moment – still loading the episodes", 2_000); return }
        if (kind == Vod.MOVIES) {
            startActivity(VodPlayerActivity.intent(this, Vod.MOVIES, null,
                listOf(VodPlayerActivity.Entry(m.id, m.name, Vod.movieUrl(m))), 0, fromStart,
                listOfNotNull("FILM", m.year.takeIf { it > 0 }?.toString(), m.rating.takeIf { it > 0 }?.let { "★ %.1f".format(it) }).joinToString("  ·  ")))
        } else nextEpisode()?.let { playEpisode(it, fromStart) }
    }

    private fun playEpisode(e: Episode, fromStart: Boolean) {
        val m = item ?: return
        val all = details?.episodes ?: return
        val entries = all.map { VodPlayerActivity.Entry(it.id, "${m.name}  ·  ${epLabel(it)}", Vod.episodeUrl(it), it.image) }
        startActivity(VodPlayerActivity.intent(this, Vod.SERIES, m.id, entries, all.indexOf(e), fromStart))
    }

    private fun epLabel(e: Episode): String {
        val n = if (e.season > 0) "S${e.season} E${e.number}" else "E${e.number}"
        val t = e.title.substringAfterLast(" - ").trim()
        return if (t.isEmpty() || t.equals("Episode", true)) n else "$n  $t"
    }

    override fun onResume() { super.onResume(); refreshButtons() }

    /** The time, top right (like everywhere else). */
    private val tick = object : Runnable {
        override fun run() {
            val c = findViewById<TextView>(R.id.clock) ?: return
            c.text = Ui.time(System.currentTimeMillis())
            c.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L + 50)
        }
    }
    override fun onStart() { super.onStart(); findViewById<View>(R.id.clock)?.removeCallbacks(tick); tick.run() }
    override fun onStop() { super.onStop(); findViewById<View>(R.id.clock)?.removeCallbacks(tick) }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private inner class EpisodeAdapter : RecyclerView.Adapter<EpisodeAdapter.H>() {
        var items: List<Episode> = emptyList()
        fun set(list: List<Episode>) { items = list; notifyDataSetChanged() }
        /** The same episodes with more filled in: redrawn where they are, so the highlight stays put. */
        fun update(list: List<Episode>) { if (list.size != items.size) return set(list); items = list; notifyItemRangeChanged(0, list.size) }
        private val h by lazy { (125 * resources.displayMetrics.density).toInt() }

        inner class H(v: View) : RecyclerView.ViewHolder(v) {
            val num: TextView = v.findViewById(R.id.num)
            val image: ImageView = v.findViewById(R.id.image)
            val title: TextView = v.findViewById(R.id.title)
            val plot: TextView = v.findViewById(R.id.plot)
            val progress: ProgressBar = v.findViewById(R.id.progress)
            val bigNum: TextView = v.findViewById(R.id.bigNum)
            val len: TextView = v.findViewById(R.id.len)
            val watchedTag: View = v.findViewById(R.id.watchedTag)
        }

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_episode, parent, false)
            v.findViewById<View>(R.id.imageBox).clipToOutline = true
            val holder = H(v)
            v.setOnClickListener { val p = holder.bindingAdapterPosition; if (p >= 0) playEpisode(items[p], false) }
            // Highlighted: the card grows a little and shows what the episode's about.
            v.setOnFocusChangeListener { view, has ->
                Ui.grow(view, has)
                // (Its space is always kept: the cards changing height as you moved along re-laid the page out, and
                // the highlight could jump up to the buttons.)
                holder.plot.visibility = if (has && holder.plot.text.isNotEmpty()) View.VISIBLE else View.INVISIBLE
            }
            // MENU on an episode marks it watched or not.
            v.setOnKeyListener { _, code, ev ->
                // ◀ ▶ go episode to episode, always within the row (never off it to the buttons); the row glides.
                if (code == android.view.KeyEvent.KEYCODE_DPAD_RIGHT || code == android.view.KeyEvent.KEYCODE_DPAD_LEFT) {
                    if (ev.action != android.view.KeyEvent.ACTION_DOWN) return@setOnKeyListener true
                    val p = holder.bindingAdapterPosition
                    val t = p + if (code == android.view.KeyEvent.KEYCODE_DPAD_RIGHT) 1 else -1
                    if (p < 0 || t < 0 || t >= items.size) return@setOnKeyListener true
                    val next = episodeList.findViewHolderForAdapterPosition(t)?.itemView
                    if (next != null) next.requestFocus()
                    else { episodeList.smoothScrollToPosition(t); episodeList.postDelayed({ episodeList.findViewHolderForAdapterPosition(t)?.itemView?.requestFocus() }, 120) }
                    return@setOnKeyListener true
                }
                if (code != android.view.KeyEvent.KEYCODE_MENU) return@setOnKeyListener false
                val p = holder.bindingAdapterPosition
                if (ev.action == android.view.KeyEvent.ACTION_UP && p >= 0) {
                    val e = items[p]
                    Vod.setWatched(e.id, !Vod.watched(e.id), Vod.SERIES, item?.id)
                    Ui.test("watched ${e.title} ${Vod.watched(e.id)}")
                    notifyItemChanged(p)
                    refreshButtons()
                }
                true
            }
            return holder
        }

        override fun onBindViewHolder(holder: H, position: Int) {
            val e = items[position]
            holder.num.text = "" // (the big number's on the card either way)
            holder.bigNum.text = e.number.toString()
            val t = e.title.substringAfterLast(" - ").trim()
            holder.title.text = if (t.isEmpty() || t.equals("Episode", true)) "Episode ${e.number}" else t
            val pos = Vod.position(e.id)
            val len = e.duration?.let { niceDuration(it) }
            holder.len.text = len ?: ""; holder.len.visibility = if (len != null) View.VISIBLE else View.GONE
            holder.watchedTag.visibility = if (Vod.watched(e.id) && pos == null) View.VISIBLE else View.GONE
            val left = if (pos != null && pos.second > 0) "${minutes((pos.second - pos.first) / 60_000)} left" else null
            holder.plot.text = listOfNotNull(e.plot?.trim()?.takeIf { it.isNotEmpty() }, left).joinToString("  ·  ")
            holder.plot.visibility = if (holder.itemView.hasFocus() || holder.itemView.isFocused) (if (holder.plot.text.isNotEmpty()) View.VISIBLE else View.GONE) else View.GONE
            Posters.load(holder.image, e.image, h, holder.num, wide = true)
            if (pos != null && pos.second > 0) {
                holder.progress.visibility = View.VISIBLE
                holder.progress.progress = (pos.first * 1000 / pos.second).toInt()
            } else holder.progress.visibility = View.GONE
        }
    }

    // ------------------------------------------------------------------ cast

    private var castShown = false

    /** A row of who's in it (photos from TMDB); OK on someone opens their page. */
    private fun castUnderEpisodes() {
        val box = findViewById<LinearLayout>(R.id.detailContent)
        val head = findViewById<View>(R.id.castHead); val scroll = findViewById<View>(R.id.castScroll)
        if (box.indexOfChild(scroll) > box.indexOfChild(episodeList)) return
        box.removeView(head); box.removeView(scroll)
        val at = box.indexOfChild(episodeList) + 1
        box.addView(head, at); box.addView(scroll, at + 1)
        (head.layoutParams as LinearLayout.LayoutParams).topMargin = Ui.dp(this, 18)
    }

    private fun showCast(m: VodItem, tmdbId: String?) {
        if (castShown) return
        castShown = true
        scope.launch {
            val cast = People.cast(kind, m, tmdbId)
            if (cast.isEmpty() || isFinishing) return@launch
            val row = findViewById<LinearLayout>(R.id.castRow)
            row.removeAllViews()
            for (a in cast) {
                val v = layoutInflater.inflate(R.layout.item_cast, row, false)
                v.findViewById<TextView>(R.id.name).text = a.name
                v.findViewById<TextView>(R.id.role).text = a.role
                v.findViewById<TextView>(R.id.initials).text = Ui.initials(a.name)
                val photo = v.findViewById<ImageView>(R.id.photo)
                photo.clipToOutline = true
                photo.outlineProvider = object : android.view.ViewOutlineProvider() {
                    override fun getOutline(view: View, o: android.graphics.Outline) { o.setOval(0, 0, view.width, view.height) }
                }
                (photo.parent as View).apply {
                    clipToOutline = true
                    background = android.graphics.drawable.GradientDrawable().apply { shape = android.graphics.drawable.GradientDrawable.OVAL; setColor(Themes.color(this@DetailActivity, R.attr.dSurface2)) }
                }
                Posters.load(photo, a.photo, Ui.dp(this@DetailActivity, 84), v.findViewById(R.id.initials))
                v.setOnFocusChangeListener { _, has -> if (has) People.prefetch(a.id) }
                v.setOnClickListener {
                    startActivity(Intent(this@DetailActivity, PersonActivity::class.java).putExtra(PersonActivity.ID, a.id).putExtra(PersonActivity.NAME, a.name))
                }
                row.addView(v)
            }
            findViewById<View>(R.id.castScroll).visibility = View.VISIBLE
            findViewById<View>(R.id.castHead).visibility = View.VISIBLE
            // Get ready for someone being picked: the first few people's pages, and the playlist to match against.
            cast.take(3).forEach { People.prefetch(it.id) }
            People.warm()
        }
    }

}
