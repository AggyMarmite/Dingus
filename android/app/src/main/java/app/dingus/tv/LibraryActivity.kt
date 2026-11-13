package app.dingus.tv

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.GridLayoutManager
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Films or TV shows: categories down the left, posters on the right. */
class LibraryActivity : BaseActivity() {
    private lateinit var side: SideMenu
    companion object {
        const val KIND = "kind"
        /** Open on this category. */
        const val CATEGORY = "category"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var kind: String
    private lateinit var categories: RecyclerView
    private lateinit var grid: RecyclerView
    private lateinit var empty: TextView
    private lateinit var title: TextView
    private lateinit var count: TextView
    private var lib: Vod.Library? = null
    private var current = Vod.RECENT
    private val posters = PosterAdapter()
    private lateinit var cats: CategoryList

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_library)
        kind = intent.getStringExtra(KIND) ?: Vod.MOVIES
        side = SideMenu.attach(this, if (kind == Vod.MOVIES) SideMenu.Page.FILMS else SideMenu.Page.SHOWS)
        categories = findViewById(R.id.categories)
        grid = findViewById(R.id.grid)
        empty = findViewById(R.id.empty)
        title = findViewById(R.id.title)
        count = findViewById(R.id.count)
        findViewById<TextView>(R.id.heading).text = if (kind == Vod.MOVIES) "Films" else "TV Shows"
        findViewById<View>(R.id.search).setOnClickListener { startActivity(Intent(this, SearchActivity::class.java)) }

        cats = CategoryList(
            onShow = { show(it.id) },
            onPick = { show(it.id); focusGrid() },
            onMenu = { c ->
                if (!c.id.startsWith("\u0001")) Dialogs.categoryOptions(this, kind, cats.items.map { it.id }.filter { !it.startsWith("\u0001") }, c.id, c.name) {
                    setCategories()
                    val i = cats.indexOf(c.id).coerceAtLeast(0)
                    categories.scrollToPosition(i)
                    categories.post { categories.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
                }
            },
        )
        categories.layoutManager = LinearLayoutManager(this)
        categories.adapter = cats
        categories.itemAnimator = null

        val cols = ((resources.displayMetrics.widthPixels / resources.displayMetrics.density - 290 - 44) / 146).toInt().coerceIn(3, 8)
        grid.layoutManager = GridLayoutManager(this, cols)
        grid.adapter = posters
        grid.itemAnimator = null
        grid.setHasFixedSize(true)
        grid.setItemViewCacheSize(cols * 3)

        colsNarrow = cols
        colsWide = ((resources.displayMetrics.widthPixels / resources.displayMetrics.density - 60 - 44) / 146).toInt().coerceIn(cols, 10)
        // Into the films: the categories step aside (more films showing); back to them: they come back. Like Live TV.
        window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { _, n ->
            if (n != null && inside(n, grid)) hideSide() else if (n != null && inside(n, side0)) showSide()
        }

        current = intent.getStringExtra(CATEGORY) ?: getPreferences(MODE_PRIVATE).getString("cat", Vod.RECENT) ?: Vod.RECENT
        load()
    }

    private fun load() {
        val cached = Vod.cached(kind)
        val fromSeeAll = intent.getStringExtra(CATEGORY) != null
        if (cached == null) { empty.text = "Loading ${if (kind == Vod.MOVIES) "films" else "TV shows"}…"; empty.visibility = View.VISIBLE }
        else {
            // Already loaded (the usual case, e.g. from "See all"): show it straight away, no waiting.
            lib = cached
            setCategories()
            show(current, force = true)
            if (fromSeeAll) focusGrid() else categories.post { focusCategory() }
            Ui.test("library $kind ${cached.items.size}")
        }
        scope.launch {
            try {
                val l = Vod.library(kind)
                if (lib !== l) {
                    lib = l
                    setCategories()
                    show(current, force = true)
                    if (fromSeeAll) focusGrid() else categories.post { focusCategory() }
                    Ui.test("library $kind ${l.items.size}")
                }
                if (System.currentTimeMillis() - l.loadedAt > Store.refreshHours * 3600_000L) {
                    lib = Vod.library(kind, fresh = true)
                    setCategories(); show(current, force = true)
                }
            } catch (e: Exception) {
                if (lib == null) { empty.text = "Couldn't load the library.\n${Net.friendly(e)}"; empty.visibility = View.VISIBLE }
                Ui.test("library-failed ${e.message}")
            }
        }
    }

    private fun setCategories() {
        val l = lib ?: return
        val list = Vod.categoriesFor(kind, l)
        if (list.none { it.id == current }) current = list.firstOrNull { it.id == Vod.RECENT }?.id ?: Vod.ALL
        cats.set(list, current)
    }

    private fun show(id: String, force: Boolean = false) {
        val l = lib ?: return
        if (id == current && !force) return
        current = id
        getPreferences(MODE_PRIVATE).edit().putString("cat", id).apply()
        cats.select(id)
        val items = Vod.itemsIn(kind, l, id)
        posters.set(items)
        grid.scrollToPosition(0)
        title.text = cats.items.firstOrNull { it.id == id }?.name ?: ""
        count.text = if (items.isEmpty()) "" else "${items.size}"
        empty.text = "Nothing here."
        empty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
    }

    private var colsNarrow = 4
    private var colsWide = 6
    private val side0 by lazy { findViewById<View>(R.id.libSide) }

    private fun inside(v: View, parent: View): Boolean {
        var p: Any? = v
        while (p is View) { if (p === parent) return true; p = p.parent }
        return false
    }

    private fun smoothly() {
        if (!Ui.lite) android.transition.TransitionManager.beginDelayedTransition(side0.parent as android.view.ViewGroup,
            android.transition.ChangeBounds().setDuration(200).setInterpolator(android.view.animation.DecelerateInterpolator()))
    }

    private fun hideSide() {
        if (side0.visibility != View.VISIBLE) return
        smoothly()
        side0.visibility = View.GONE
        (grid.layoutManager as GridLayoutManager).spanCount = colsWide
        Ui.test("library-side hidden")
    }

    private fun showSide() {
        if (side0.visibility == View.VISIBLE) return
        smoothly()
        side0.visibility = View.VISIBLE
        (grid.layoutManager as GridLayoutManager).spanCount = colsNarrow
        Ui.test("library-side shown")
    }

    private fun focusCategory() {
        val i = cats.indexOf(current).coerceAtLeast(0)
        categories.scrollToPosition(i)
        categories.post { categories.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
    }

    private fun focusGrid() {
        if (posters.itemCount == 0) return
        grid.post { (grid.findViewHolderForAdapterPosition(0)?.itemView ?: grid.getChildAt(0))?.requestFocus() }
    }

    override fun onResume() {
        super.onResume()
        // Coming back from watching: "Continue watching" and the progress bars may have changed.
        if (lib != null) {
            val wasContinue = current == Vod.CONTINUE
            setCategories()
            if (wasContinue) show(current, force = true) else posters.notifyItemRangeChanged(0, posters.itemCount)
        }
    }

    override fun onStop() { super.onStop(); Posters.trim() }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private inner class PosterAdapter : RecyclerView.Adapter<PosterAdapter.H>() {
        var items: List<VodItem> = emptyList()
        fun set(list: List<VodItem>) { items = list; notifyDataSetChanged() }
        private val h by lazy { (189 * resources.displayMetrics.density).toInt() }

        inner class H(v: View) : RecyclerView.ViewHolder(v) {
            val poster: ImageView = v.findViewById(R.id.poster)
            val fallback: TextView = v.findViewById(R.id.fallback)
            val title: TextView = v.findViewById(R.id.title)
            val meta: TextView = v.findViewById(R.id.meta)
            val progress: ProgressBar = v.findViewById(R.id.progress)
        }

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_poster, parent, false)
            val holder = H(v)
            v.setOnClickListener {
                val p = holder.bindingAdapterPosition
                if (p >= 0) startActivity(Intent(this@LibraryActivity, DetailActivity::class.java)
                    .putExtra(DetailActivity.KIND, kind).putExtra(DetailActivity.ID, items[p].id))
            }
            v.setOnFocusChangeListener { view, has -> Ui.grow(view, has, 120) }
            return holder
        }

        override fun onBindViewHolder(holder: H, position: Int) {
            val m = items[position]
            holder.title.text = m.name
            holder.fallback.text = m.name
            holder.meta.text = listOfNotNull(m.year.takeIf { it > 0 }?.toString(), m.rating.takeIf { it > 0 }?.let { "★ %.1f".format(it) }).joinToString("   ")
            Posters.load(holder.poster, m.poster, h, holder.fallback)
            val pos = if (kind == Vod.MOVIES) Vod.position(m.id) else null
            if (pos != null && pos.second > 0) {
                holder.progress.visibility = View.VISIBLE
                holder.progress.progress = (pos.first * 1000 / pos.second).toInt()
            } else holder.progress.visibility = View.GONE
        }
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        // LEFT from the first column of films: back to the categories (they slide back in).
        if (event.action == android.view.KeyEvent.ACTION_DOWN && event.keyCode == android.view.KeyEvent.KEYCODE_DPAD_LEFT) {
            val f = currentFocus
            if (f != null && inside(f, grid)) {
                val item = grid.findContainingItemView(f)
                val pos = item?.let { grid.getChildAdapterPosition(it) } ?: -1
                if (pos >= 0 && pos % (grid.layoutManager as GridLayoutManager).spanCount == 0) {
                    showSide(); categories.post { focusCategory() }; return true
                }
            }
        }
        return side.onKey(event) || super.dispatchKeyEvent(event)
    }

}
