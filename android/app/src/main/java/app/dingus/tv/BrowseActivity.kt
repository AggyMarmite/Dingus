package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Films or TV shows, Netflix-style: a banner for what's highlighted, then Continue watching, Recently added and a row per category. */
class BrowseActivity : BaseActivity() {
    companion object { const val KIND = "kind" }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var kind: String
    private lateinit var rows: RecyclerView
    private lateinit var adapter: RowsAdapter
    private lateinit var hero: Hero
    private lateinit var empty: TextView
    private var lib: Vod.Library? = null
    private var focusedOnce = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_browse)
        kind = intent.getStringExtra(KIND) ?: Vod.MOVIES
        rows = findViewById(R.id.rows)
        empty = findViewById(R.id.empty)
        hero = Hero(this, scope)
        adapter = RowsAdapter(this, onFocus = { hero.show(it) }, onClick = { open(it) })
        rows.layoutManager = LinearLayoutManager(this).apply { initialPrefetchItemCount = 3 }
        rows.adapter = adapter
        rows.itemAnimator = null
        rows.setItemViewCacheSize(4)
        findViewById<TextView>(R.id.heading).text = if (kind == Vod.MOVIES) "Films" else "TV Shows"
        findViewById<View>(R.id.search).setOnClickListener { startActivity(Intent(this, SearchActivity::class.java)) }
        findViewById<View>(R.id.allCategories).setOnClickListener {
            startActivity(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, kind))
        }
        load()
    }

    private fun load() {
        if (Vod.cached(kind) == null) { empty.text = "Loading ${if (kind == Vod.MOVIES) "films" else "TV shows"}…"; empty.visibility = View.VISIBLE }
        scope.launch {
            try {
                val l = Vod.library(kind)
                lib = l
                build()
                Ui.test("library $kind ${l.items.size}")
                if (System.currentTimeMillis() - l.loadedAt > 12 * 3600_000L) { lib = Vod.library(kind, fresh = true); build() }
            } catch (e: Exception) {
                if (lib == null) { empty.text = "Couldn't load the library.\n${Net.friendly(e)}"; empty.visibility = View.VISIBLE }
                Ui.test("library-failed ${e.message}")
            }
        }
    }

    private fun build() {
        val l = lib ?: return
        val out = ArrayList<Row>()
        val cont = Vod.continueList(kind, l).map { m ->
            val pos = if (kind == Vod.MOVIES) Vod.position(m.id) else null
            Card.Title(kind, m, if (pos != null && pos.second > 0) pos.first.toFloat() / pos.second else -1f, if (kind == Vod.SERIES) "Continue" else null)
        }
        if (cont.isNotEmpty()) out.add(Row("Continue watching", cont.take(20)))
        if (l.newest.isNotEmpty()) out.add(Row("Recently added", l.newest.take(30).map { Card.Title(kind, it) } +
            Card.SeeAll(kind, Vod.RECENT, "Recently added")))
        for (c in l.categories) {
            if (Store.isHidden("$kind:${c.id}")) continue
            val items = l.byCategory[c.id] ?: continue
            if (items.isEmpty()) continue
            val cards = ArrayList<Card>(minOf(items.size, 25) + 1)
            items.take(24).forEach { cards.add(Card.Title(kind, it)) }
            if (items.size > 24) cards.add(Card.SeeAll(kind, c.id, c.name))
            out.add(Row(c.name, cards))
        }
        adapter.set(out)
        findViewById<TextView>(R.id.count).text = "${l.items.size} ${if (kind == Vod.MOVIES) "films" else "shows"}"
        empty.visibility = if (out.isEmpty()) View.VISIBLE else View.GONE
        if (out.isEmpty()) empty.text = "Nothing here yet."
        if (!focusedOnce && out.isNotEmpty()) {
            focusedOnce = true
            rows.post {
                val first = (rows.findViewHolderForAdapterPosition(0) as? RowsAdapter.H)?.list
                first?.post { first.getChildAt(0)?.requestFocus() }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        if (lib != null) build() // progress and Continue watching may have changed
    }

    override fun onStop() { super.onStop(); Posters.trim() }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun open(card: Card) {
        when (card) {
            is Card.Title -> startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, card.kind).putExtra(DetailActivity.ID, card.item.id))
            is Card.SeeAll -> startActivity(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, card.kind).putExtra(LibraryActivity.CATEGORY, card.categoryId))
            is Card.Live -> {}
        }
    }
}
