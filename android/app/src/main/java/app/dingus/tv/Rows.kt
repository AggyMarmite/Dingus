package app.dingus.tv

import kotlinx.coroutines.launch
import android.content.Context
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView

/** Something shown as a card in a row on Home or in Films / TV Shows. */
sealed class Card {
    class Title(val kind: String, val item: VodItem, val progress: Float = -1f, val label: String? = null, val isNew: Boolean = false) : Card()
    class Live(val channel: Channel) : Card()
    /** A programme on a channel: what's on now (with progress) or coming up ([label] e.g. "In 30 min", "20:45"). */
    class Show(val channel: Channel, val programme: Programme?, val label: String? = null) : Card()
    class SeeAll(val kind: String, val categoryId: String, val name: String, val chip: Boolean = false) : Card()

    /** What the card shows, to tell whether a row really changed. */
    fun sig(): String = when (this) {
        is Title -> "t${item.id}|${(progress * 100).toInt()}|$label|$isNew"
        is Live -> "l${channel.id}"
        is Show -> "s${channel.id}|${programme?.start}|$label"
        is SeeAll -> "a$categoryId|$name"
    }
}

class Row(val title: String, val cards: List<Card>)

/** My list: films and shows saved by hand (held OK on a poster, or the button on the details page). */
object MyList {
    /** The row on Home (both kinds), or on Films / TV Shows (just that kind, when [kind] is given). */
    fun row(kind: String? = null): Row? {
        val libs = listOfNotNull(Vod.cached(Vod.MOVIES)?.let { Vod.MOVIES to it }, Vod.cached(Vod.SERIES)?.let { Vod.SERIES to it }).toMap()
        val items = Vod.myList(libs).filter { kind == null || it.first == kind }
        return if (items.isEmpty()) null else Row("My list", items.take(30).map { (kind, m) -> Card.Title(kind, m) })
    }

    /** Adds or removes a film or show; [redo] rebuilds what's on screen. */
    fun toggle(card: Card, redo: () -> Unit): Boolean {
        val t = card as? Card.Title ?: return false
        val on = !Vod.inMyList(t.kind, t.item.id)
        Vod.setInMyList(t.kind, t.item.id, on)
        Ui.test("my-list ${t.item.name} $on")
        Notice.done(if (on) "Added to My list" else "Removed from My list", 2_500)
        redo()
        return true
    }
}

/**
 * Rows of cards, Netflix-style: one vertical list of rows, each a horizontal list. The rows share one pool of
 * card views, and a row remembers where you'd scrolled to, so it's smooth on a Fire Stick.
 */
class RowsAdapter(
    private val ctx: Context,
    private val onFocus: (Card) -> Unit,
    private val onClick: (Card) -> Unit,
    /** Held OK on a film or show: true when it dealt with it (so the click doesn't also open it). */
    private val onLong: (Card) -> Boolean = { false },
) : RecyclerView.Adapter<RowsAdapter.H>() {
    var rows: List<Row> = emptyList(); private set
    private val pool = RecyclerView.RecycledViewPool().apply { setMaxRecycledViews(0, 24); setMaxRecycledViews(1, 16) }
    private val scroll = HashMap<String, Int>()
    private val d = ctx.resources.displayMetrics.density

    fun set(list: List<Row>) {
        val old = rows
        val same = list.size == old.size && list.indices.all { list[it].title == old[it].title }
        rows = list
        if (!same) { notifyDataSetChanged(); return }
        // Only redraw the rows whose cards changed: redrawing a row under the remote can move the highlight.
        for (i in list.indices) {
            val a = old[i].cards; val b = list[i].cards
            if (a.size != b.size || a.indices.any { a[it].sig() != b[it].sig() }) notifyItemChanged(i)
        }
    }

    class H(v: View) : RecyclerView.ViewHolder(v) {
        val title: TextView = v.findViewById(R.id.rowTitle)
        val list: RecyclerView = v.findViewById(R.id.rowList)
        var cards: CardsAdapter? = null
        var key = ""
    }

    override fun getItemCount() = rows.size

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
        val h = H(LayoutInflater.from(ctx).inflate(R.layout.item_row, parent, false))
        h.list.layoutManager = RowLayoutManager(ctx)
        h.list.setRecycledViewPool(pool)
        h.list.itemAnimator = null
        h.list.setHasFixedSize(true)
        val cards = CardsAdapter()
        h.cards = cards
        h.list.adapter = cards
        return h
    }

    override fun onBindViewHolder(h: H, position: Int) {
        val row = rows[position]
        if (h.key.isNotEmpty()) (h.list.layoutManager as LinearLayoutManager).findFirstVisibleItemPosition().let { if (it >= 0) scroll[h.key] = it }
        h.key = row.title
        h.title.text = row.title
        h.cards?.wide = wideRow(row.title)
        h.cards?.set(row.cards)
        h.list.scrollToPosition(scroll[row.title] ?: 0)
    }

    /** Trending and Because you watched: wide cards with the title's art (the other rows keep posters). */
    private fun wideRow(title: String) = !Ui.lite && (title.startsWith("Trending") || title.startsWith("Because you watched"))

    /** Pictures and logos not known yet: looked up once each in the background, then the card's drawn again. */
    private val asked = HashSet<String>()
    private fun lookUp(kind: String, item: VodItem, redraw: () -> Unit) {
        if (!TitleLogos.enabled || !asked.add("$kind:${item.id}:${item.name}")) return
        Background.scope.launch {
            try { TitleLogos.find(kind, item, Vod.cachedDetails(kind, item.id)?.tmdbId) } catch (_: Exception) {}
            android.os.Handler(android.os.Looper.getMainLooper()).post(redraw)
        }
    }

    inner class CardsAdapter : RecyclerView.Adapter<CardsAdapter.C>() {
        var wide = false
        var cards: List<Card> = emptyList()
        fun set(list: List<Card>) { cards = list; notifyDataSetChanged() }

        inner class C(v: View) : RecyclerView.ViewHolder(v) {
            val poster: ImageView? = v.findViewById(R.id.poster)
            val fallback: TextView? = v.findViewById(R.id.fallback)
            val title: TextView? = v.findViewById(R.id.title)
            val meta: TextView? = v.findViewById(R.id.meta)
            val progress: ProgressBar? = v.findViewById(R.id.progress)
            val badge: TextView? = v.findViewById(R.id.badge)
            val newBadge: TextView? = v.findViewById(R.id.newBadge)
            val art: ImageView? = v.findViewById(R.id.art)
            val cornerLogo: ImageView? = v.findViewById(R.id.cornerLogo)
            val liveTag: View? = v.findViewById(R.id.liveTag)
            val titleArt: ImageView? = v.findViewById(R.id.titleArt)
            val titleText: TextView? = v.findViewById(R.id.titleText)
        }

        override fun getItemCount() = cards.size
        override fun getItemViewType(position: Int) = when (cards[position]) { is Card.Title -> if (wide) 4 else 0; is Card.Live, is Card.Show -> 1; is Card.SeeAll -> if ((cards[position] as Card.SeeAll).chip) 3 else 2 }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): C {
            val layout = when (viewType) { 0 -> R.layout.item_poster_row; 4 -> R.layout.item_wide_card; 1 -> R.layout.item_channel_card; 3 -> R.layout.item_category_chip; else -> R.layout.item_see_all }
            val v = LayoutInflater.from(ctx).inflate(layout, parent, false)
            val c = C(v)
            // Card views are shared between rows, so always look the card up in the row it's in now
            // (not the row that first made the view) – otherwise it can show and open another row's title.
            fun card(): Card? = (c.bindingAdapter as? CardsAdapter)?.cards?.getOrNull(c.bindingAdapterPosition)
            v.setOnClickListener { card()?.let(onClick) }
            v.setOnLongClickListener { card()?.let(onLong) ?: false }
            // ▶ on the last card of a row stays there (it doesn't drop to the row below).
            v.setOnKeyListener { _, code, ev ->
                code == android.view.KeyEvent.KEYCODE_DPAD_RIGHT && ev.action == android.view.KeyEvent.ACTION_DOWN &&
                    c.bindingAdapterPosition >= 0 && c.bindingAdapterPosition == ((c.bindingAdapter as? CardsAdapter)?.cards?.size ?: 0) - 1
            }
            v.setOnFocusChangeListener { view, has ->
                Ui.grow(view, has)
                // Artwork-tinted page: the poster's colour behind the rows and round the highlight – only once the
                // highlight has stopped on a card (working it out and redrawing the whole page on every step of a
                // held-down button is what made Home lag).
                Tint.glow(view, null, false)
                if (has && card() is Card.Title) Tint.settle(view) {
                    val tint = Tint.colourOf(c.poster) ?: return@settle
                    Tint.glow(view, tint, true)
                    (ctx as? android.app.Activity)?.let { Tint.to(it, tint) }
                }
                if (has) {
                    card()?.let(onFocus)
                    // Film and show logos for the next few titles, so they're ready as you move along.
                    (c.bindingAdapter as? CardsAdapter)?.let { TitleLogos.lookAhead(it.cards, c.bindingAdapterPosition) }
                }
            }
            return c
        }

        override fun onBindViewHolder(c: C, position: Int) {
            when (val card = cards[position]) {
                is Card.Title -> {
                    val m = card.item
                    c.title?.text = m.name
                    c.fallback?.text = m.name
                    c.meta?.text = card.label ?: listOfNotNull(m.year.takeIf { it > 0 }?.toString(), m.rating.takeIf { it > 0 }?.let { "★ %.1f".format(it) }).joinToString("   ")
                    if (c.titleArt != null) {
                        // Wide: the background picture, and the title's logo on it (or its name in bold).
                        val bg = TitleLogos.cachedBackdrop(card.kind, m) ?: Vod.cachedDetails(card.kind, m.id)?.backdrop
                        c.poster?.let { Posters.load(it, bg ?: m.poster, (164 * d).toInt(), c.fallback, wide = bg != null) }
                        val logo = TitleLogos.cached(card.kind, m)
                        c.titleText?.text = m.name
                        c.titleText?.visibility = if (logo == null) View.VISIBLE else View.GONE
                        c.titleArt?.visibility = if (logo == null) View.GONE else View.VISIBLE
                        if (logo != null) Logos.load(c.titleArt!!, logo, (52 * d).toInt(), null, wide = true)
                        if (!TitleLogos.knowsBackdrop(card.kind, m)) lookUp(card.kind, m) { if (position < cards.size && cards[position] === card) notifyItemChanged(position) }
                    } else c.poster?.let { Posters.load(it, m.poster, (164 * d).toInt(), c.fallback) }
                    c.progress?.apply {
                        visibility = if (card.progress >= 0) View.VISIBLE else View.GONE
                        progress = (card.progress * 1000).toInt()
                    }
                    c.badge?.visibility = if (card.kind == Vod.MOVIES && Vod.watched(m.id)) View.VISIBLE else View.GONE
                    c.newBadge?.visibility = if (card.isNew || Upcoming.isArrived(m.id)) View.VISIBLE else View.GONE
                }
                is Card.Live -> {
                    val ch = card.channel
                    val now = Repo.nowNext(ch).first
                    c.title?.text = (if (Store.showNumbers && ch.number > 0) "${ch.number}  " else "") + ch.name
                    c.meta?.text = now?.title ?: ""
                    c.fallback?.text = Ui.initials(ch.name)
                    // The programme's own picture when there is one (the logo small in the corner), else the logo.
                    val t = now?.let { TitleLogos.forProgramme(it) }
                    val pic = t?.let { TitleLogos.cachedBackdrop(it.first, it.second) }
                    if (pic != null && c.art != null && !Ui.lite) {
                        c.art.visibility = View.VISIBLE; c.poster?.visibility = View.INVISIBLE; c.fallback?.visibility = View.GONE
                        Posters.load(c.art, pic, (117 * d).toInt(), null, wide = true)
                        c.cornerLogo?.let { cl -> cl.visibility = if (ch.logo.isNullOrBlank()) View.GONE else View.VISIBLE; Logos.load(cl, ch.logo, (20 * d).toInt()) }
                    } else {
                        c.art?.visibility = View.GONE; c.cornerLogo?.visibility = View.GONE; c.poster?.visibility = View.VISIBLE
                        c.poster?.let { Logos.load(it, ch.logo, (76 * d).toInt(), c.fallback) }
                        if (t != null && !TitleLogos.knowsBackdrop(t.first, t.second)) lookUp(t.first, t.second) { if (position < cards.size && cards[position] === card) notifyItemChanged(position) }
                    }
                    c.liveTag?.visibility = if (now != null) View.VISIBLE else View.GONE
                    c.progress?.apply {
                        visibility = if (now != null) View.VISIBLE else View.GONE
                        if (now != null) progress = Ui.progress(now)
                    }
                }
                is Card.Show -> {
                    val ch = card.channel
                    val p = card.programme
                    c.title?.text = p?.title ?: ch.name
                    c.meta?.text = listOfNotNull(ch.name, card.label).joinToString("  ·  ")
                    c.fallback?.text = Ui.initials(ch.name)
                    // The programme's own picture when there is one (the logo small in the corner), else the logo.
                    val t = p?.let { TitleLogos.forProgramme(it) }
                    val pic = t?.let { TitleLogos.cachedBackdrop(it.first, it.second) }
                    if (pic != null && c.art != null && !Ui.lite) {
                        c.art.visibility = View.VISIBLE; c.poster?.visibility = View.INVISIBLE; c.fallback?.visibility = View.GONE
                        Posters.load(c.art, pic, (117 * d).toInt(), null, wide = true)
                        c.cornerLogo?.let { cl -> cl.visibility = if (ch.logo.isNullOrBlank()) View.GONE else View.VISIBLE; Logos.load(cl, ch.logo, (20 * d).toInt()) }
                    } else {
                        c.art?.visibility = View.GONE; c.cornerLogo?.visibility = View.GONE; c.poster?.visibility = View.VISIBLE
                        c.poster?.let { Logos.load(it, ch.logo, (76 * d).toInt(), c.fallback) }
                        if (t != null && !TitleLogos.knowsBackdrop(t.first, t.second)) lookUp(t.first, t.second) { if (position < cards.size && cards[position] === card) notifyItemChanged(position) }
                    }
                    val now = System.currentTimeMillis()
                    c.liveTag?.visibility = if (p != null && p.start <= now && now < p.stop) View.VISIBLE else View.GONE
                    c.progress?.apply {
                        val on = p != null && p.start <= now && now < p.stop
                        visibility = if (on) View.VISIBLE else View.GONE
                        if (on) progress = Ui.progress(p!!)
                    }
                }
                is Card.SeeAll -> if (card.chip) (c.itemView as TextView).text = card.name
            }
        }
    }
}

/**
 * The vertical list of rows: the row you're on moves to the top (title and all), like Netflix – or, [centered],
 * to the middle of the screen (Home), with the rows either side peeking in. A big feature (Home's) always goes to
 * the top.
 */
class RowsLayoutManager(ctx: Context, private val centered: Boolean = false) : LinearLayoutManager(ctx) {
    init { initialPrefetchItemCount = 3 }

    override fun requestChildRectangleOnScreen(parent: RecyclerView, child: View, rect: android.graphics.Rect, immediate: Boolean, focusedChildVisible: Boolean): Boolean {
        val dy = when {
            !centered || child.findViewById<View>(R.id.heroWatch) != null -> child.top - parent.paddingTop
            // The first row under Home's feature: up to the top (its title level with the clock), so the feature
            // goes right off rather than half of it staying above.
            isJustAfterFeature(parent, child) -> child.top - (16 * parent.resources.displayMetrics.density).toInt()
            else -> (child.top + child.height / 2) - parent.height / 2
        }
        if (dy == 0) return false
        Scrolling.by(parent, 0, dy, immediate)
        return true
    }

    private fun isJustAfterFeature(parent: RecyclerView, child: View): Boolean {
        val pos = parent.getChildAdapterPosition(child)
        if (pos != 1) return false
        return parent.findViewHolderForAdapterPosition(0)?.itemView?.findViewById<View>(R.id.heroWatch) != null ||
            (parent.adapter is androidx.recyclerview.widget.ConcatAdapter) // (the feature may be scrolled away, unbound)
    }
}

/** Rows kept centred ([RowsLayoutManager] centered): room below the last row so it can be, and the other rows dimmed. */
object CentredRows {
    fun attach(a: android.app.Activity, rows: RecyclerView) {
        rows.post { rows.setPadding(rows.paddingLeft, rows.paddingTop, rows.paddingRight, rows.height / 2) }
        fun dim(item: View, animate: Boolean) {
            if (item.findViewById<View>(R.id.heroWatch) != null) { item.alpha = 1f; return }
            val f = a.currentFocus
            val holder = f?.let { rows.findContainingItemView(it) }
            val a1 = if (holder == null || holder === item) 1f else 0.45f
            if (item.alpha == a1) return
            if (animate && !Ui.lite) item.animate().alpha(a1).setDuration(160).start() else { item.animate().cancel(); item.alpha = a1 }
        }
        a.window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { _, _ ->
            for (i in 0 until rows.childCount) dim(rows.getChildAt(i), true)
        }
        rows.addOnChildAttachStateChangeListener(object : RecyclerView.OnChildAttachStateChangeListener {
            override fun onChildViewAttachedToWindow(v: View) = dim(v, false)
            override fun onChildViewDetachedFromWindow(v: View) { v.animate().cancel(); v.alpha = 1f }
        })
    }
}

/**
 * UP in a list of rows goes to the row above, every time. Left to itself, focus search can skip rows or stay put
 * when the row above has scrolled out of sight.
 */
object RowNav {
    /** The adapter position of the row (or Home's feature) holding [focus], or -1 when it's not in [rows]. */
    fun position(rows: RecyclerView, focus: View?): Int {
        val item = focus?.let { rows.findContainingItemView(it) } ?: return -1
        return rows.getChildAdapterPosition(item)
    }

    private fun target(rows: RecyclerView, pos: Int): View? {
        val v = rows.findViewHolderForAdapterPosition(pos)?.itemView ?: return null
        v.findViewById<View>(R.id.heroWatch)?.let { return it }
        val list = (rows.getChildViewHolder(v) as? RowsAdapter.H)?.list ?: return null
        for (i in 0 until list.childCount) { val c = list.getChildAt(i); if (c.right > 0 && c.isFocusable) return c }
        return list.getChildAt(0)
    }

    fun focus(rows: RecyclerView, pos: Int) {
        target(rows, pos)?.let { if (it.requestFocus()) return }
        if (Ui.lite) {
            rows.scrollToPosition(pos)
            rows.post { if (target(rows, pos)?.requestFocus() != true) rows.postDelayed({ target(rows, pos)?.requestFocus() }, 80) }
            return
        }
        // The row above has scrolled out of sight: glide back up to it (as going down does), then move onto it.
        var done = false
        fun land() { if (!done) { done = true; target(rows, pos)?.requestFocus() } }
        rows.addOnScrollListener(object : RecyclerView.OnScrollListener() {
            override fun onScrollStateChanged(rv: RecyclerView, state: Int) {
                if (state == RecyclerView.SCROLL_STATE_IDLE) { rv.removeOnScrollListener(this); land() }
            }
        })
        rows.smoothScrollToPosition(pos)
        rows.postDelayed({ land() }, 700) // (in case it didn't need to move)
    }

    /** Handles UP when the focus is in a row below the first; true when it did. */
    fun up(rows: RecyclerView, focus: View?): Boolean {
        val p = position(rows, focus)
        if (p <= 0) return false
        focus(rows, p - 1); return true
    }
}

/**
 * Scrolling a list to the highlight, smoothly: a short eased glide for a single press; while a button's held down (a
 * glide still going when the next step comes) it goes straight there, so the list keeps up instead of stuttering.
 */
object Scrolling {
    private val ease = Motion.glide
    fun by(rv: RecyclerView, dx: Int, dy: Int, immediate: Boolean = false) {
        if (immediate || Ui.lite || rv.scrollState == RecyclerView.SCROLL_STATE_SETTLING) { rv.stopScroll(); rv.scrollBy(dx, dy) }
        else rv.smoothScrollBy(dx, dy, ease, 300)
    }
}

/** Horizontal rows: the same steady scrolling as the rows themselves, the highlighted title kept in from the edge. */
class RowLayoutManager(ctx: Context) : LinearLayoutManager(ctx, HORIZONTAL, false) {
    init { initialPrefetchItemCount = 6 }
    override fun requestChildRectangleOnScreen(parent: RecyclerView, child: View, rect: android.graphics.Rect, immediate: Boolean, focusedChildVisible: Boolean): Boolean {
        val pad = parent.paddingLeft
        val right = parent.width - parent.paddingRight
        val dx = when {
            child.left < pad -> child.left - pad
            child.right > right -> child.right - right + (child.width / 3).coerceAtMost(right - pad - child.width).coerceAtLeast(0)
            else -> 0
        }
        if (dx == 0) return false
        Scrolling.by(parent, dx, 0, immediate)
        return true
    }
}
