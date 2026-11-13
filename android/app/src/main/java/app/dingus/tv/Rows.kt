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
    class Title(val kind: String, val item: VodItem, val progress: Float = -1f, val label: String? = null) : Card()
    class Live(val channel: Channel) : Card()
    class SeeAll(val kind: String, val categoryId: String, val name: String) : Card()
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
        val same = list.size == rows.size && list.indices.all { list[it].title == rows[it].title }
        rows = list
        if (same) notifyItemRangeChanged(0, list.size) else notifyDataSetChanged()
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
        }

        override fun getItemCount() = cards.size
        override fun getItemViewType(position: Int) = when (cards[position]) { is Card.Title -> 0; is Card.Live -> 1; is Card.SeeAll -> 2 }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): C {
            val layout = when (viewType) { 0 -> R.layout.item_poster_row; 1 -> R.layout.item_channel_card; else -> R.layout.item_see_all }
            val v = LayoutInflater.from(ctx).inflate(layout, parent, false)
            val c = C(v)
            v.setOnClickListener { cards.getOrNull(c.bindingAdapterPosition)?.let(onClick) }
            v.setOnFocusChangeListener { view, has ->
                view.animate().scaleX(if (has) 1.06f else 1f).scaleY(if (has) 1.06f else 1f).setDuration(110).start()
                if (has) cards.getOrNull(c.bindingAdapterPosition)?.let(onFocus)
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
                }
                is Card.Live -> {
                    val ch = card.channel
                    val now = Repo.nowNext(ch).first
                    c.title?.text = (if (ch.number > 0) "${ch.number}  " else "") + ch.name
                    c.meta?.text = now?.title ?: ""
                    c.fallback?.text = Ui.initials(ch.name)
                    c.poster?.let { Logos.load(it, ch.logo, (76 * d).toInt(), c.fallback) }
                    c.progress?.apply {
                        visibility = if (now != null) View.VISIBLE else View.GONE
                        if (now != null) progress = Ui.progress(now)
                    }
                }
                is Card.SeeAll -> {}
            }
        }
    }
}
