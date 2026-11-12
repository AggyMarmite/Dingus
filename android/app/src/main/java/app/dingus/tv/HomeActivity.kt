package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Home: continue watching, favourite and recent channels, new films and shows – and the way to everything else. */
class HomeActivity : BaseActivity() {
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
        adapter = RowsAdapter(this, onFocus = { hero.show(it) }, onClick = { open(it) })
        rows.layoutManager = LinearLayoutManager(this).apply { initialPrefetchItemCount = 3 }
        rows.adapter = adapter
        rows.itemAnimator = null
        rows.setItemViewCacheSize(4)

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
        if (System.currentTimeMillis() - Store.updateCheckedAt > 12 * 3600_000L) Updater.check(this, manual = false)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
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
                if (Repo.channels.isEmpty()) Repo.loadSaved()
                build()
                val p = Store.playlist ?: return@launch
                if (Repo.channels.isEmpty()) {
                    empty.text = "Loading your channels…"; empty.visibility = View.VISIBLE
                    try { Repo.refreshChannels(p) } catch (e: Exception) {
                        empty.text = "Couldn't load your playlist.\n${Net.friendly(e)}"; return@launch
                    }
                    build()
                    try { Repo.refreshGuide() } catch (_: Exception) {}
                    build()
                } else if (System.currentTimeMillis() - Store.guideLoadedAt > 12 * 3600_000L) {
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

    private fun build() {
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
                startActivity(Intent(this, PlayerActivity::class.java).putExtra(PlayerActivity.CHANNEL, card.channel.id).putExtra(PlayerActivity.CATEGORY, cat))
            }
            is Card.SeeAll -> startActivity(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, card.kind).putExtra(LibraryActivity.CATEGORY, card.categoryId))
        }
    }
}
