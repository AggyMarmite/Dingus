package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.TextView
import android.view.KeyEvent
import androidx.recyclerview.widget.ConcatAdapter
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * Home, like the Windows app: a big feature at the top (carry on watching, what's on, live sport, new films), then
 * favourite channels, coming up next, live sport now, tonight's picks, new films and shows. "Lite" swaps it for a
 * simpler page (a banner and rows) for devices that struggle.
 */
class HomeActivity : BaseActivity() {
    private val lite = Store.homeLite
    private var feature: HomeFeature? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())
    private lateinit var rows: RecyclerView
    private lateinit var adapter: RowsAdapter
    private lateinit var hero: Hero
    private lateinit var empty: TextView
    private var askedForPlaylist = false
    private var loading = false
    private var focusedOnce = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_home)
        rows = findViewById(R.id.rows)
        empty = findViewById(R.id.empty)
        hero = Hero(this, scope)
        adapter = RowsAdapter(this, onFocus = { if (lite) hero.show(it) }, onClick = { open(it) })
        rows.layoutManager = RowsLayoutManager(this)
        if (lite) rows.adapter = adapter
        else {
            // The big feature scrolls with the rows, as the first item.
            findViewById<View>(R.id.hero).visibility = View.GONE
            val f = HomeFeature(this)
            feature = f
            rows.adapter = ConcatAdapter(f, adapter)
        }
        findViewById<TextView>(R.id.tabLite).apply {
            text = if (lite) "Full home" else "Lite"
            setOnClickListener { Store.homeLite = !lite; Ui.test("home-lite ${!lite}"); recreate() }
        }
        rows.itemAnimator = null
        rows.setItemViewCacheSize(4)

        // Opening the app straight into Live TV (Settings → Open Dingus on). Home stays underneath for BACK.
        if (savedInstanceState == null && intent?.action == Intent.ACTION_MAIN && Store.startScreen == "live" && Store.playlist != null)
            startActivity(Intent(this, MainActivity::class.java))

        findViewById<View>(R.id.tabHome).isActivated = true
        findViewById<View>(R.id.tabHome).setOnClickListener { focusFirstCard() }
        findViewById<View>(R.id.tabGuide).setOnClickListener { startActivity(Intent(this, MainActivity::class.java)) }
        findViewById<View>(R.id.tabFilms).setOnClickListener { startActivity(Intent(this, BrowseActivity::class.java).putExtra(BrowseActivity.KIND, Vod.MOVIES)) }
        findViewById<View>(R.id.tabShows).setOnClickListener { startActivity(Intent(this, BrowseActivity::class.java).putExtra(BrowseActivity.KIND, Vod.SERIES)) }
        findViewById<View>(R.id.tabSearch).setOnClickListener { startActivity(Intent(this, SearchActivity::class.java)) }
        findViewById<View>(R.id.tabSettings).setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }
    }

    override fun onResume() {
        super.onResume()
        Ui.test("home-shown")
        val vod = if (Vod.available) View.VISIBLE else View.GONE
        findViewById<View>(R.id.tabFilms).visibility = vod
        findViewById<View>(R.id.tabShows).visibility = vod
        tick.run()
        if (Store.playlist == null) {
            empty.text = "Welcome to Dingus.\nAdd your playlist to get started."
            empty.visibility = View.VISIBLE
            adapter.set(emptyList())
            findViewById<View>(R.id.tabSettings).requestFocus()
            if (!askedForPlaylist) { askedForPlaylist = true; startActivity(Intent(this, AddPlaylistActivity::class.java)) }
            return
        }
        load()
        feature?.start()
        if (System.currentTimeMillis() - Store.updateCheckedAt > 12 * 3600_000L) Updater.check(this, manual = false)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
        feature?.stop()
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (event.action == KeyEvent.ACTION_DOWN) feature?.noteKey()
        return super.dispatchKeyEvent(event)
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private val tick = object : Runnable {
        override fun run() {
            findViewById<TextView>(R.id.clock).text = Ui.time(System.currentTimeMillis())
            handler.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L)
        }
    }

    /** Channels and guide from the saved copy (refreshed when old), films and shows if the playlist has them. */
    private fun load() {
        if (loading) { build(); return }
        loading = true
        scope.launch {
            try {
                Vpn.startup() // "connect when Dingus starts": before anything loads
                if (Repo.channels.isEmpty()) Repo.loadSaved()
                build()
                val p = Store.playlist ?: return@launch
                if (Repo.channels.isEmpty()) {
                    empty.text = "Loading your channels…"; empty.visibility = View.VISIBLE
                    var tries = 0
                    while (true) {
                        try { Repo.refreshChannels(p); break } catch (e: Exception) {
                            // The network may not be up yet (just after waking): try again a few times by itself.
                            if (++tries > 4) { empty.text = "Couldn't load your playlist.\n${Net.friendly(e)}"; return@launch }
                            empty.text = "Waiting for the network…"
                            kotlinx.coroutines.delay(3_000L * tries)
                        }
                    }
                    build()
                    try { Repo.refreshGuide() } catch (_: Exception) {}
                    build()
                } else if (Store.refreshDue) {
                    try { Repo.refreshChannels(p); Repo.refreshGuide() } catch (_: Exception) {}
                    build()
                }
                if (Vod.available) {
                    for (k in listOf(Vod.MOVIES, Vod.SERIES)) try { Vod.library(k) } catch (_: Exception) {}
                    build()
                }
            } finally { loading = false }
        }
    }

    private fun build() { if (lite) buildLite() else buildFull() }

    private fun buildLite() {
        val out = ArrayList<Row>()
        val cont = ArrayList<Card>()
        Vod.cached(Vod.MOVIES)?.let { lib ->
            for (m in Vod.continueList(Vod.MOVIES, lib)) {
                val pos = Vod.position(m.id) ?: continue
                cont.add(Card.Title(Vod.MOVIES, m, if (pos.second > 0) pos.first.toFloat() / pos.second else -1f))
            }
        }
        Vod.cached(Vod.SERIES)?.let { lib -> Vod.continueList(Vod.SERIES, lib).forEach { cont.add(Card.Title(Vod.SERIES, it, label = "Continue")) } }
        if (cont.isNotEmpty()) out.add(Row("Continue watching", cont.take(20)))
        val favs = Repo.channelsIn(Repo.FAV)
        if (favs.isNotEmpty()) out.add(Row("Favourite channels", favs.take(30).map { Card.Live(it) }))
        val recent = Repo.channelsIn(Repo.RECENT)
        if (recent.isNotEmpty()) out.add(Row("Recently watched", recent.take(20).map { Card.Live(it) }))
        Vod.cached(Vod.MOVIES)?.let { lib -> if (lib.items.isNotEmpty()) out.add(Row("New films", lib.newest.take(24).map { Card.Title(Vod.MOVIES, it) })) }
        Vod.cached(Vod.SERIES)?.let { lib -> if (lib.items.isNotEmpty()) out.add(Row("New TV shows", lib.newest.take(24).map { Card.Title(Vod.SERIES, it) })) }
        // Nothing personal yet: start people off with some channels.
        if (favs.isEmpty() && recent.isEmpty() && Repo.channels.isNotEmpty())
            out.add(1.coerceAtMost(out.size), Row("Channels", Repo.channelsIn(Repo.ALL).take(30).map { Card.Live(it) }))
        adapter.set(out)
        empty.visibility = if (out.isEmpty() && Repo.channels.isEmpty()) View.VISIBLE else View.GONE
        Ui.test("home rows ${out.size}")
        if (!focusedOnce && out.isNotEmpty()) { focusedOnce = true; focusFirstCard() }
    }

    private fun focusFirstCard() {
        rows.scrollToPosition(0)
        if (!lite) {
            rows.post {
                val v = rows.findViewHolderForAdapterPosition(0)?.itemView?.findViewById<View>(R.id.heroWatch)
                if (v != null) v.requestFocus() else rows.postDelayed({ rows.findViewHolderForAdapterPosition(0)?.itemView?.findViewById<View>(R.id.heroWatch)?.requestFocus() }, 80)
            }
            return
        }
        rows.post {
            val row = rows.findViewHolderForAdapterPosition(0) as? RowsAdapter.H
            val card = row?.list?.getChildAt(0)
            if (card != null) card.requestFocus() else rows.postDelayed({ (rows.findViewHolderForAdapterPosition(0) as? RowsAdapter.H)?.list?.getChildAt(0)?.requestFocus() }, 80)
        }
    }

    private fun open(card: Card) {
        when (card) {
            is Card.Title -> startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, card.kind).putExtra(DetailActivity.ID, card.item.id))
            is Card.Live -> {
                val cat = if (Store.isFavourite(card.channel.id)) Repo.FAV else Repo.ALL
                startActivity(Intent(this, MainActivity::class.java).putExtra(MainActivity.PLAY, card.channel.id).putExtra(MainActivity.PLAY_CATEGORY, cat))
            }
            is Card.Show -> {
                val p = card.programme
                if (p != null && p.start > System.currentTimeMillis()) {
                    // Still to come: set a reminder, or watch the channel now.
                    Dialogs.builder(this).setTitle(p.title).setMessage("${card.channel.name}  ·  ${Ui.day(p.start)}${Ui.span(p)}")
                        .setPositiveButton(if (Reminders.has(card.channel, p)) "Cancel reminder" else "Remind me") { _, _ ->
                            Reminders.toggle(card.channel, p); build()
                        }
                        .setNegativeButton("Watch ${card.channel.name} now") { _, _ -> watch(card.channel) }
                        .show()
                } else watch(card.channel)
            }
            is Card.SeeAll -> startActivity(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, card.kind).putExtra(LibraryActivity.CATEGORY, card.categoryId))
        }
    }

    private fun watch(c: Channel) {
        val cat = if (Store.isFavourite(c.id)) Repo.FAV else Repo.ALL
        startActivity(Intent(this, MainActivity::class.java).putExtra(MainActivity.PLAY, c.id).putExtra(MainActivity.PLAY_CATEGORY, cat))
    }

    private fun openTitle(kind: String, m: VodItem) =
        startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, kind).putExtra(DetailActivity.ID, m.id))

    private fun guide() = startActivity(Intent(this, MainActivity::class.java))

    private fun left(p: Programme, now: Long) = "${(p.stop - now + 59_999) / 60_000} min left"
    private fun until(p: Programme, now: Long): String {
        val mins = (p.start - now + 59_999) / 60_000
        return if (mins < 60) "In $mins min" else Ui.time(p.start)
    }

    /** The full Home: the feature slides, then the rows. */
    private fun buildFull() {
        val now = System.currentTimeMillis()
        val slides = ArrayList<Slide>()
        val out = ArrayList<Row>()
        val favs = Repo.channelsIn(Repo.FAV)
        val recent = Repo.channelsIn(Repo.RECENT)
        val mine = (favs + recent).distinctBy { it.id }

        // Carry on watching: the last channel, and a film or show you're part-way through.
        Store.lastChannel?.let { Repo.channel(it) }?.let { c ->
            val p = Repo.nowNext(c).first
            slides.add(Slide("Carry on watching", p?.title ?: c.name,
                listOfNotNull(c.name, p?.let { Ui.span(it) }, p?.let { left(it, now) }).joinToString("  ·  "), p?.desc,
                p?.let { Ui.progress(it) / 1000f } ?: -1f, watch = { watch(c) }, second = "Live TV" to { guide() }))
        }
        val cont = ArrayList<Card>()
        Vod.cached(Vod.MOVIES)?.let { lib ->
            for (m in Vod.continueList(Vod.MOVIES, lib)) {
                val pos = Vod.position(m.id) ?: continue
                cont.add(Card.Title(Vod.MOVIES, m, if (pos.second > 0) pos.first.toFloat() / pos.second else -1f))
            }
        }
        Vod.cached(Vod.SERIES)?.let { lib -> Vod.continueList(Vod.SERIES, lib).forEach { cont.add(Card.Title(Vod.SERIES, it, label = "Continue")) } }
        (cont.firstOrNull() as? Card.Title)?.let { t ->
            slides.add(Slide("Continue watching", t.item.name, listOfNotNull(if (t.kind == Vod.MOVIES) "Film" else "TV show", t.item.year.takeIf { it > 0 }?.toString()).joinToString("  ·  "),
                null, t.progress, Vod.cachedDetails(t.kind, t.item.id)?.backdrop ?: t.item.poster, watch = { openTitle(t.kind, t.item) }))
        }

        // What's on your channels now, and next.
        val onNow = mine.mapNotNull { c -> Repo.nowNext(c).first?.let { c to it } }
        onNow.take(2).forEach { (c, p) ->
            if (slides.none { it.title == p.title }) slides.add(Slide("On now", p.title, "${c.name}  ·  ${Ui.span(p)}  ·  ${left(p, now)}", p.desc,
                Ui.progress(p) / 1000f, watch = { watch(c) }, second = "Live TV" to { guide() }))
        }

        // Live sport now and tonight's picks: from your channels first, then the rest (only so many, to stay quick).
        val scan = (mine + Repo.channelsIn(Repo.ALL).take(3000)).distinctBy { it.id }
        val sport = ArrayList<Card.Show>()
        val tonight = ArrayList<Pair<Channel, Programme>>()
        val cal = java.util.Calendar.getInstance().apply { set(java.util.Calendar.HOUR_OF_DAY, 18); set(java.util.Calendar.MINUTE, 0) }
        val eveningFrom = maxOf(now, cal.timeInMillis)
        cal.set(java.util.Calendar.HOUR_OF_DAY, 23); cal.set(java.util.Calendar.MINUTE, 59)
        val eveningTo = cal.timeInMillis
        for (c in scan) {
            val (p, _) = Repo.nowNext(c)
            if (p != null && p.genre == Genres.SPORT && sport.size < 20) sport.add(Card.Show(c, p, left(p, now)))
            if (tonight.size < 40) for (q in Repo.programmes(c)) {
                if (q.start in eveningFrom..eveningTo && (q.genre == Genres.SPORT || q.genre == Genres.FILM)) { tonight.add(c to q); break }
            }
            if (sport.size >= 20 && tonight.size >= 40) break
        }
        sport.firstOrNull()?.let { s ->
            val p = s.programme!!
            if (slides.none { it.title == p.title }) slides.add(Slide("Live sport", p.title, "${s.channel.name}  ·  ${Ui.span(p)}  ·  ${left(p, now)}", p.desc,
                Ui.progress(p) / 1000f, watch = { watch(s.channel) }, second = "Live TV" to { guide() }))
        }
        Vod.cached(Vod.MOVIES)?.newest?.firstOrNull()?.let { m ->
            slides.add(Slide("New film", m.name, listOfNotNull(m.year.takeIf { it > 0 }?.toString(), m.rating.takeIf { it > 0 }?.let { "★ %.1f".format(it) }).joinToString("  ·  "),
                Vod.cachedDetails(Vod.MOVIES, m.id)?.plot, art = m.poster, watch = { openTitle(Vod.MOVIES, m) },
                second = "Films" to { startActivity(Intent(this, BrowseActivity::class.java).putExtra(BrowseActivity.KIND, Vod.MOVIES)) }))
        }
        // Nothing personal yet: something on now to start with.
        if (slides.isEmpty()) Repo.channelsIn(Repo.ALL).firstNotNullOfOrNull { c -> Repo.nowNext(c).first?.let { c to it } }?.let { (c, p) ->
            slides.add(Slide("On now", p.title, "${c.name}  ·  ${Ui.span(p)}", p.desc, Ui.progress(p) / 1000f, watch = { watch(c) }, second = "Live TV" to { guide() }))
        }

        if (cont.isNotEmpty()) out.add(Row("Continue watching", cont.take(20)))
        if (favs.isNotEmpty()) out.add(Row("Favourite channels", favs.take(30).map { c -> Card.Show(c, Repo.nowNext(c).first) }))
        val next = mine.mapNotNull { c -> Repo.nowNext(c).second?.let { Card.Show(c, it, until(it, now)) } }
        if (next.isNotEmpty()) out.add(Row("Coming up next", next.take(20)))
        if (sport.isNotEmpty()) out.add(Row("Live sport now", sport))
        if (tonight.isNotEmpty()) out.add(Row("Tonight's picks", tonight.sortedBy { it.second.start }.take(20).map { (c, q) ->
            Card.Show(c, q, Ui.time(q.start) + "  ·  " + (Genres.names.firstOrNull { it.first == q.genre }?.second ?: ""))
        }))
        if (recent.isNotEmpty()) out.add(Row("Recently watched", recent.take(20).map { c -> Card.Show(c, Repo.nowNext(c).first) }))
        if (favs.isEmpty() && recent.isEmpty() && Repo.channels.isNotEmpty())
            out.add(Row("Channels", Repo.channelsIn(Repo.ALL).take(30).map { c -> Card.Show(c, Repo.nowNext(c).first) }))
        Vod.cached(Vod.MOVIES)?.let { lib -> if (lib.items.isNotEmpty()) out.add(Row("New films", lib.newest.take(24).map { Card.Title(Vod.MOVIES, it, isNew = true) })) }
        Vod.cached(Vod.SERIES)?.let { lib -> if (lib.items.isNotEmpty()) out.add(Row("New TV shows", lib.newest.take(24).map { Card.Title(Vod.SERIES, it, isNew = true) })) }

        feature?.set(slides.take(5))
        adapter.set(out)
        empty.visibility = if (out.isEmpty() && slides.isEmpty() && Repo.channels.isEmpty()) View.VISIBLE else View.GONE
        Ui.test("home rows ${out.size} slides ${slides.size}")
        if (!focusedOnce && (slides.isNotEmpty() || out.isNotEmpty())) { focusedOnce = true; focusFirstCard() }
    }
}
