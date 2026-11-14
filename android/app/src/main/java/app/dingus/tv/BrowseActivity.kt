package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
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

/**
 * Films or TV shows. A banner for the highlighted title over either rows (Featured: Continue watching, Recently
 * added, Top rated and a row per category) or a poster grid (one category). LEFT at the left edge brings in the
 * categories panel, like Live TV; moving through it changes what's showing, RIGHT or OK goes back into the titles.
 */
class BrowseActivity : BaseActivity() {
    private lateinit var side: SideMenu
    companion object {
        const val KIND = "kind"
        /** Open on this category (in the grid) rather than Featured. */
        const val CATEGORY = "category"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var kind: String
    private lateinit var root: ViewGroup
    private lateinit var panel: View
    private lateinit var categories: RecyclerView
    private lateinit var cats: CategoryList
    private lateinit var rows: RecyclerView
    private lateinit var grid: RecyclerView
    private lateinit var adapter: RowsAdapter
    private lateinit var hero: Hero
    private lateinit var heroView: View
    private lateinit var browseArt: ImageView
    private lateinit var gridHead: View
    private lateinit var gridTitle: TextView
    private lateinit var gridCount: TextView
    private lateinit var heroText: TextView
    private lateinit var heroBody: View
    private lateinit var crumb: TextView
    private lateinit var empty: TextView
    private val posters = PosterAdapter()
    private var lib: Vod.Library? = null
    private var current = Vod.FEATURED
    private var focusedOnce = false
    private var colsNarrow = 4
    private var colsWide = 6
    private var compact = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_browse)
        kind = intent.getStringExtra(KIND) ?: Vod.MOVIES
        side = SideMenu.attach(this, if (kind == Vod.MOVIES) SideMenu.Page.FILMS else SideMenu.Page.SHOWS)
        root = findViewById(R.id.browseRoot)
        panel = findViewById(R.id.catPanel)
        categories = findViewById(R.id.categories)
        rows = findViewById(R.id.rows)
        grid = findViewById(R.id.grid)
        empty = findViewById(R.id.empty)
        crumb = findViewById(R.id.crumb)
        heroView = findViewById(R.id.hero)
        gridHead = findViewById(R.id.gridHead)
        gridTitle = findViewById(R.id.gridTitle)
        gridCount = findViewById(R.id.gridCount)
        heroText = findViewById(R.id.heroText)
        findViewById<TextView>(R.id.heading).text = if (kind == Vod.MOVIES) "Films" else "TV Shows"
        // The picture behind the whole screen, darker on the left and at the bottom (like Home).
        val bgc = Themes.color(this, R.attr.dBg); val clear = bgc and 0x00FFFFFF
        findViewById<View>(R.id.heroArt).visibility = View.GONE
        findViewById<View>(R.id.heroShade).visibility = View.GONE
        findViewById<View>(R.id.browseShade).background = android.graphics.drawable.LayerDrawable(arrayOf(
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
                intArrayOf((bgc and 0x00FFFFFF) or (0xEB shl 24), (bgc and 0x00FFFFFF) or (0x8C shl 24), clear, clear)),
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP,
                intArrayOf(bgc, (bgc and 0x00FFFFFF) or (0xB3 shl 24), clear, clear)),
        ))
        val accent = Themes.color(this, R.attr.dAccent)
        findViewById<View>(R.id.browseGlow).background = android.graphics.drawable.GradientDrawable().apply {
            gradientType = android.graphics.drawable.GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf((accent and 0x00FFFFFF) or (0x44 shl 24), (accent and 0x00FFFFFF) or (0x12 shl 24), 0)
            gradientRadius = resources.displayMetrics.widthPixels * 0.42f
            setGradientCenter(0.75f, 0.25f)
        }
        browseArt = findViewById(R.id.browseArt)
        hero = Hero(this, scope, browseArt, resources.displayMetrics.heightPixels * 2 / 3)
        findViewById<TextView>(R.id.screenName).text = if (kind == Vod.MOVIES) "Films" else "TV Shows"
        heroBody = findViewById(R.id.heroBody)
        compact = true; setCompact(false) // (the full size banner: taller here than Home's)

        adapter = RowsAdapter(this, onFocus = { hero.show(it) }, onClick = { open(it) })
        // The row you're on sits right under the banner, fully in view (the next one peeking below); moving down slides
        // the rows up one, so nothing jumps about or gets cut off.
        rows.layoutManager = RowsLayoutManager(this, centered = false)
        rows.adapter = adapter
        rows.itemAnimator = null
        rows.setItemViewCacheSize(4)
        // Further down the rows, the picture behind dims (the rows stay easy to read).
        val dim = findViewById<View>(R.id.browseDim)
        rows.addOnScrollListener(object : RecyclerView.OnScrollListener() {
            override fun onScrolled(rv: RecyclerView, dx: Int, dy: Int) {
                val a = if ((rv.layoutManager as LinearLayoutManager).findFirstVisibleItemPosition() > 0) 0.55f else 0f
                if (dim.alpha != a) dim.animate().alpha(a).setDuration(if (Ui.lite) 0 else 200).start()
            }
        })

        cats = CategoryList(
            onShow = { show(it.id) },
            onPick = { show(it.id); focusContent() },
            onMenu = { c ->
                if (!c.id.startsWith("\u0001")) Dialogs.categoryOptions(this, kind, cats.items.map { it.id }.filter { !it.startsWith("\u0001") }, c.id, c.name) {
                    setCategories()
                    if (current == Vod.FEATURED) build()
                    focusCategory(c.id)
                }
            },
        )
        categories.layoutManager = LinearLayoutManager(this)
        categories.adapter = cats
        categories.itemAnimator = null

        val wdp = resources.displayMetrics.widthPixels / resources.displayMetrics.density - 60 - 56
        colsWide = (wdp / 146).toInt().coerceIn(3, 10)
        colsNarrow = ((wdp - 246) / 146).toInt().coerceIn(3, colsWide)
        grid.layoutManager = GridLayoutManager(this, colsWide)
        grid.adapter = posters
        grid.itemAnimator = null
        grid.setHasFixedSize(true)
        grid.setItemViewCacheSize(colsWide * 3)

        // Into the titles: the categories step aside. Back onto them: they come back.
        window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { _, n ->
            if (n == null) return@addOnGlobalFocusChangeListener
            if (inside(n, rows) || inside(n, grid)) hidePanel() else if (inside(n, panel)) showPanel()
        }

        current = intent.getStringExtra(CATEGORY) ?: Vod.FEATURED
        load()
    }

    private fun load() {
        val cached = Vod.cached(kind)
        if (cached == null) { empty.text = "Loading ${if (kind == Vod.MOVIES) "films" else "TV shows"}…"; empty.visibility = View.VISIBLE }
        else got(cached)
        scope.launch {
            try {
                val l = Vod.library(kind)
                if (lib !== l) got(l)
                if (System.currentTimeMillis() - l.loadedAt > Store.refreshHours * 3600_000L) {
                    val f = Vod.library(kind, fresh = true)
                    if (f !== lib) { lib = f; setCategories(); show(current, force = true) }
                }
            } catch (e: Exception) {
                if (lib == null) { empty.text = "Couldn't load the library.\n${Net.friendly(e)}"; empty.visibility = View.VISIBLE }
                Ui.test("library-failed ${e.message}")
            }
        }
    }

    private fun got(l: Vod.Library) {
        lib = l
        setCategories()
        show(current, force = true)
        if (!focusedOnce) { focusedOnce = true; focusContent() }
        Ui.test("library $kind ${l.items.size}")
    }

    private fun setCategories() {
        val l = lib ?: return
        val list = Vod.categoriesFor(kind, l)
        if (list.none { it.id == current }) current = Vod.FEATURED
        cats.set(list, current)
    }

    /** Show a category: Featured as rows, anything else as a grid of posters. */
    private fun show(id: String, force: Boolean = false) {
        val l = lib ?: return
        if (id == current && !force) return
        val changed = id != current
        current = id
        cats.select(id)
        setCompact(false)
        val name = cats.items.firstOrNull { it.id == id }?.name ?: ""
        if (id == Vod.FEATURED) {
            grid.visibility = View.GONE
            rows.visibility = View.VISIBLE
            heroView.visibility = View.VISIBLE
            gridHead.visibility = View.GONE
            browseArt.visibility = View.VISIBLE
            findViewById<View>(R.id.screenName).visibility = View.VISIBLE
            build()
            if (changed) rows.scrollToPosition(0)
            Ui.test("browse-cat Featured ${adapter.itemCount}")
        } else {
            val items = Vod.itemsIn(kind, l, id)
            rows.visibility = View.GONE
            grid.visibility = View.VISIBLE
            posters.set(items)
            if (changed) grid.scrollToPosition(0)
            // No banner here: the category's name and how many, then the posters (more of them showing).
            heroView.visibility = View.GONE
            gridHead.visibility = View.VISIBLE
            browseArt.visibility = View.INVISIBLE // (just the glow behind a grid)
            findViewById<View>(R.id.screenName).visibility = View.GONE
            gridTitle.text = name
            gridCount.text = "·  ${Ui.count(items.size)} ${if (kind == Vod.MOVIES) (if (items.size == 1) "film" else "films") else (if (items.size == 1) "show" else "shows")}  ·  ◀ categories"
            empty.text = "Nothing here."
            empty.visibility = if (items.isEmpty()) View.VISIBLE else View.GONE
            Ui.test("browse-cat $name ${items.size}")
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
            (if (l.items.size > 30) listOf(Card.SeeAll(kind, Vod.RECENT, "Recently added")) else emptyList())))
        val top = Vod.topRated(l)
        if (top.size >= 5) out.add(Row("Top rated", top.take(24).map { Card.Title(kind, it) } +
            (if (top.size > 24) listOf(Card.SeeAll(kind, Vod.TOP, "Top rated")) else emptyList())))
        for (c in Store.ordered(kind, l.categories) { it.id }) {
            if (Store.isHidden("$kind:${c.id}")) continue
            val items = l.byCategory[c.id] ?: continue
            if (items.isEmpty()) continue
            val cards = ArrayList<Card>(minOf(items.size, 25) + 1)
            items.take(24).forEach { cards.add(Card.Title(kind, it)) }
            if (items.size > 24) cards.add(Card.SeeAll(kind, c.id, c.name))
            out.add(Row(c.name, cards))
        }
        adapter.set(out)
        if (current == Vod.FEATURED) {
            empty.visibility = if (out.isEmpty()) View.VISIBLE else View.GONE
            if (out.isEmpty()) empty.text = "Nothing here yet."
        }
    }

    // ------------------------------------------------------------------ focus and the panel

    /**
     * Into the titles (from the categories, or on opening): the first one showing. Just after a category has
     * changed its titles aren't laid out yet, so this keeps trying for a moment until there's one to go to.
     */
    private var focusAsk = 0

    private fun focusContent(tries: Int = 0, ask: Int = ++focusAsk) {
        if (ask != focusAsk) return // asked again since, or moved elsewhere
        val target: View? = if (current == Vod.FEATURED) {
            val lm = rows.layoutManager as LinearLayoutManager
            val p = lm.findFirstCompletelyVisibleItemPosition().takeIf { it >= 0 } ?: lm.findFirstVisibleItemPosition()
            val list = (rows.findViewHolderForAdapterPosition(maxOf(0, p)) as? RowsAdapter.H)?.list
            list?.let { l -> (0 until l.childCount).map { l.getChildAt(it) }.firstOrNull { it.isFocusable && it.right > 0 } }
        } else if (posters.itemCount > 0) {
            val lm = grid.layoutManager as GridLayoutManager
            grid.findViewHolderForAdapterPosition(maxOf(0, lm.findFirstCompletelyVisibleItemPosition()))?.itemView
        } else null
        if (target != null && target.requestFocus()) { Ui.test("browse-into ${if (current == Vod.FEATURED) "rows" else "grid"}"); return }
        if (tries < 30) rows.postDelayed({ focusContent(tries + 1, ask) }, 50)
    }

    private fun focusCategory(id: String = current) {
        val i = cats.indexOf(id).coerceAtLeast(0)
        val v = categories.findViewHolderForAdapterPosition(i)?.itemView
        if (v != null && v.isShown) { v.requestFocus(); return }
        categories.scrollToPosition(i)
        categories.post { categories.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
    }

    private fun smoothly() {
        if (!Ui.lite) android.transition.TransitionManager.beginDelayedTransition(root,
            android.transition.ChangeBounds().setDuration(200).setInterpolator(android.view.animation.DecelerateInterpolator()))
    }

    private fun showPanel() {
        if (panel.visibility == View.VISIBLE) return
        smoothly()
        panel.visibility = View.VISIBLE
        (grid.layoutManager as GridLayoutManager).spanCount = colsNarrow
        Ui.test("browse-panel shown")
    }

    private fun hidePanel() {
        if (panel.visibility != View.VISIBLE) return
        smoothly()
        panel.visibility = View.GONE
        (grid.layoutManager as GridLayoutManager).spanCount = colsWide
        Ui.test("browse-panel hidden")
    }

    /** Scrolled down the grid: a smaller banner, so more posters show. */
    private fun setCompact(on: Boolean) {
        if (on == compact) return
        compact = on
        // (No animation: the grid resizing under the remote while moving would throw the highlight about.)
        val lp = heroView.layoutParams
        val d = resources.displayMetrics.density
        lp.height = ((if (on) 150 else 212) * d).toInt()
        heroView.layoutParams = lp
        // Room above the logo for the picture behind (less when it's small).
        heroBody.setPadding(heroBody.paddingLeft, ((if (on) 14 else 52) * d).toInt(), heroBody.paddingRight, heroBody.paddingBottom)
        // Everything has to fit the smaller banner (nothing cut off): a smaller logo, no description.
        heroText.visibility = if (on) View.GONE else View.VISIBLE
        heroText.maxLines = 2
        val logo = findViewById<View>(R.id.heroLogo)
        logo.layoutParams = logo.layoutParams.apply { height = ((if (on) 56 else 64) * resources.displayMetrics.density).toInt() }
    }

    private fun inside(v: View, parent: View): Boolean {
        var p: Any? = v
        while (p is View) { if (p === parent) return true; p = p.parent }
        return false
    }

    /** The highlighted title is as far left as it goes (so LEFT should bring in the categories). */
    private fun atLeftEdge(f: View): Boolean {
        if (inside(f, grid)) {
            val item = grid.findContainingItemView(f) ?: return false
            val pos = grid.getChildAdapterPosition(item)
            return pos >= 0 && pos % (grid.layoutManager as GridLayoutManager).spanCount == 0
        }
        if (inside(f, rows)) {
            val list = f.parent as? RecyclerView ?: return false
            return list !== rows && list.getChildAdapterPosition(f) == 0
        }
        return false
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val f = currentFocus
        val down = event.action == KeyEvent.ACTION_DOWN
        if (!side.isOpen && f != null) {
            when (event.keyCode) {
                KeyEvent.KEYCODE_DPAD_LEFT -> if (down && atLeftEdge(f)) { focusAsk++; showPanel(); focusCategory(); return true }
                KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_DPAD_DOWN -> if (down && inside(f, panel)) focusAsk++ // (staying in the categories)
                KeyEvent.KEYCODE_DPAD_RIGHT -> if (down && inside(f, panel)) { cats.flush(); focusContent(); return true }
                KeyEvent.KEYCODE_MENU -> if (!inside(f, panel)) {
                    if (down) { showPanel(); focusCategory() }
                    return true
                }
            }
        }
        if (down && !side.isOpen && f != null && inside(f, grid) &&
            (event.keyCode == KeyEvent.KEYCODE_DPAD_DOWN || event.keyCode == KeyEvent.KEYCODE_DPAD_UP)) { gridStep(f, event.keyCode == KeyEvent.KEYCODE_DPAD_DOWN); return true }
        if (side.onKey(event)) return true
        if (down && event.keyCode == KeyEvent.KEYCODE_DPAD_UP && current == Vod.FEATURED && RowNav.up(rows, currentFocus)) return true
        return super.dispatchKeyEvent(event)
    }

    /**
     * Up and down the grid one row at a time, straight up or down from where you are (left to itself, focus could
     * jump to another column, skip a row or get stuck at the bottom of what's showing).
     */
    private fun gridStep(f: View, downwards: Boolean) {
        val item = grid.findContainingItemView(f) ?: return
        val pos = grid.getChildAdapterPosition(item)
        if (pos < 0) return
        val span = (grid.layoutManager as GridLayoutManager).spanCount
        val n = posters.itemCount
        var t = if (downwards) pos + span else pos - span
        if (downwards && t >= n) { if (pos / span == (n - 1) / span) return; t = n - 1 } // the last row, shorter: its last one
        if (t < 0) return
        val lm = grid.layoutManager as GridLayoutManager
        val v = grid.findViewHolderForAdapterPosition(t)?.itemView
        if (v != null) v.requestFocus() // (the grid scrolls it fully into view)
        else {
            // Not laid out yet: bring its row in (at the bottom going down, the top going up), then move there.
            val rowH = item.height + Ui.dp(this, 12)
            lm.scrollToPositionWithOffset(t, if (downwards) maxOf(0, grid.height - grid.paddingBottom - rowH) else 0)
            grid.post { grid.findViewHolderForAdapterPosition(t)?.itemView?.requestFocus() }
        }
        Ui.test("grid-step $t")
    }

    /** BACK steps out, like Live TV: titles → categories → leave. */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        val f = currentFocus
        if (!side.isOpen && f != null && (inside(f, rows) || inside(f, grid))) { showPanel(); focusCategory(); return }
        super.onBackPressed()
    }

    override fun onResume() {
        super.onResume()
        // Coming back from watching: Continue watching and the progress bars may have changed.
        if (lib != null) {
            setCategories()
            when (current) {
                Vod.FEATURED -> build()
                Vod.CONTINUE -> show(current, force = true)
                else -> posters.notifyItemRangeChanged(0, posters.itemCount)
            }
        }
    }

    /** The time, top right (like Home). */
    private val tick = object : Runnable {
        override fun run() {
            crumb.text = Ui.time(System.currentTimeMillis())
            crumb.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L + 50)
        }
    }

    override fun onStart() { super.onStart(); crumb.removeCallbacks(tick); tick.run() }

    override fun onStop() { super.onStop(); crumb.removeCallbacks(tick); Posters.trim() }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun open(card: Card) {
        when (card) {
            is Card.Title -> startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, card.kind).putExtra(DetailActivity.ID, card.item.id))
            is Card.SeeAll -> { show(card.categoryId); focusContent() }
            is Card.Live, is Card.Show -> {}
        }
    }

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
                if (p >= 0) startActivity(Intent(this@BrowseActivity, DetailActivity::class.java)
                    .putExtra(DetailActivity.KIND, kind).putExtra(DetailActivity.ID, items[p].id))
            }
            v.setOnFocusChangeListener { view, has ->
                Ui.grow(view, has, 120)
                val p = holder.bindingAdapterPosition
                if (has && p >= 0) {
                }
            }
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
}
