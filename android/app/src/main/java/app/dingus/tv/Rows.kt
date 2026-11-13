package app.dingus.tv

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

/**
 * Rows of cards, Netflix-style: one vertical list of rows, each a horizontal list. The rows share one pool of
 * card views, and a row remembers where you'd scrolled to, so it's smooth on a Fire Stick.
 */
class RowsAdapter(
    private val ctx: Context,
    private val onFocus: (Card) -> Unit,
    private val onClick: (Card) -> Unit,
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
        h.list.layoutManager = LinearLayoutManager(ctx, LinearLayoutManager.HORIZONTAL, false).apply { initialPrefetchItemCount = 6 }
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
        h.cards?.set(row.cards)
        h.list.scrollToPosition(scroll[row.title] ?: 0)
    }

    inner class CardsAdapter : RecyclerView.Adapter<CardsAdapter.C>() {
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
        }

        override fun getItemCount() = cards.size
        override fun getItemViewType(position: Int) = when (cards[position]) { is Card.Title -> 0; is Card.Live, is Card.Show -> 1; is Card.SeeAll -> if ((cards[position] as Card.SeeAll).chip) 3 else 2 }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): C {
            val layout = when (viewType) { 0 -> R.layout.item_poster_row; 1 -> R.layout.item_channel_card; 3 -> R.layout.item_category_chip; else -> R.layout.item_see_all }
            val v = LayoutInflater.from(ctx).inflate(layout, parent, false)
            val c = C(v)
            // Card views are shared between rows, so always look the card up in the row it's in now
            // (not the row that first made the view) – otherwise it can show and open another row's title.
            fun card(): Card? = (c.bindingAdapter as? CardsAdapter)?.cards?.getOrNull(c.bindingAdapterPosition)
            v.setOnClickListener { card()?.let(onClick) }
            v.setOnFocusChangeListener { view, has ->
                Ui.grow(view, has)
                if (has) card()?.let(onFocus)
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
                    c.poster?.let { Posters.load(it, m.poster, (164 * d).toInt(), c.fallback) }
                    c.progress?.apply {
                        visibility = if (card.progress >= 0) View.VISIBLE else View.GONE
                        progress = (card.progress * 1000).toInt()
                    }
                    c.badge?.visibility = if (card.kind == Vod.MOVIES && Vod.watched(m.id)) View.VISIBLE else View.GONE
                    c.newBadge?.visibility = if (card.isNew) View.VISIBLE else View.GONE
                }
                is Card.Live -> {
                    val ch = card.channel
                    val now = Repo.nowNext(ch).first
                    c.title?.text = (if (Store.showNumbers && ch.number > 0) "${ch.number}  " else "") + ch.name
                    c.meta?.text = now?.title ?: ""
                    c.fallback?.text = Ui.initials(ch.name)
                    c.poster?.let { Logos.load(it, ch.logo, (76 * d).toInt(), c.fallback) }
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
                    c.poster?.let { Logos.load(it, ch.logo, (76 * d).toInt(), c.fallback) }
                    val now = System.currentTimeMillis()
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

/** The vertical list of rows: the row you're on always moves to the top (title and all), like Netflix. */
class RowsLayoutManager(ctx: Context) : LinearLayoutManager(ctx) {
    init { initialPrefetchItemCount = 3 }

    override fun requestChildRectangleOnScreen(parent: RecyclerView, child: View, rect: android.graphics.Rect, immediate: Boolean, focusedChildVisible: Boolean): Boolean {
        val dy = child.top - parent.paddingTop
        if (dy == 0) return false
        if (immediate || Ui.lite) parent.scrollBy(0, dy) else parent.smoothScrollBy(0, dy)
        return true
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
