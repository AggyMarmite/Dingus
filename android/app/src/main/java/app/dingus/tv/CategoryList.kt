package app.dingus.tv

import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.ViewGroup
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView

/**
 * A list of categories for the remote: moving onto one shows it after a short pause (so scrolling past is smooth),
 * OK picks it, and MENU offers to hide it.
 */
class CategoryList(
    private val onShow: (Category) -> Unit,
    private val onPick: (Category) -> Unit,
    private val onMenu: ((Category) -> Unit)? = null,
) : RecyclerView.Adapter<CategoryList.H>() {
    var items: List<Category> = emptyList(); private set
    private var sel: String? = null
    private val handler = Handler(Looper.getMainLooper())
    private var pendingCat: Category? = null
    private val pending = Runnable { pendingCat?.let(onShow); pendingCat = null }

    fun set(list: List<Category>, selected: String?) { items = list; sel = selected; notifyDataSetChanged() }
    fun select(id: String) {
        val old = items.indexOfFirst { it.id == sel }
        sel = id
        if (old >= 0) notifyItemChanged(old)
        val now = items.indexOfFirst { it.id == id }
        if (now >= 0) notifyItemChanged(now)
    }
    fun indexOf(id: String?) = items.indexOfFirst { it.id == id }
    /** Show what's pending straight away (e.g. when leaving the list). */
    fun flush() { handler.removeCallbacks(pending); pending.run() }

    class H(val v: TextView) : RecyclerView.ViewHolder(v)

    override fun getItemCount() = items.size
    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
        val v = LayoutInflater.from(parent.context).inflate(R.layout.item_category, parent, false) as TextView
        val h = H(v)
        v.setOnFocusChangeListener { _, has ->
            val p = h.bindingAdapterPosition
            if (has && p >= 0) {
                pendingCat = items[p]
                handler.removeCallbacks(pending)
                handler.postDelayed(pending, 250)
            }
        }
        v.setOnClickListener { val p = h.bindingAdapterPosition; if (p >= 0) { handler.removeCallbacks(pending); pendingCat = null; onPick(items[p]) } }
        v.setOnKeyListener { _, code, ev ->
            val p = h.bindingAdapterPosition
            // Up and down one category at a time, scrolling the list as needed (left to itself, focus could stop at
            // the last one showing, or jump out of the list).
            if (ev.action == KeyEvent.ACTION_DOWN && p >= 0 && (code == KeyEvent.KEYCODE_DPAD_DOWN || code == KeyEvent.KEYCODE_DPAD_UP)) {
                val rv = v.parent as? RecyclerView
                val t = if (code == KeyEvent.KEYCODE_DPAD_DOWN) p + 1 else p - 1
                if (rv != null && t in items.indices) {
                    val next = rv.findViewHolderForAdapterPosition(t)?.itemView
                    if (next != null && next.isShown) next.requestFocus()
                    else { rv.scrollToPosition(t); rv.post { rv.findViewHolderForAdapterPosition(t)?.itemView?.requestFocus() } }
                    return@setOnKeyListener true
                }
                if (code == KeyEvent.KEYCODE_DPAD_DOWN) return@setOnKeyListener true // the last one: stay
            }
            if (code == KeyEvent.KEYCODE_MENU && ev.action == KeyEvent.ACTION_UP && p >= 0 && onMenu != null) { onMenu.invoke(items[p]); true }
            else code == KeyEvent.KEYCODE_MENU && onMenu != null
        }
        return h
    }

    override fun onBindViewHolder(h: H, position: Int) {
        val c = items[position]
        h.v.text = c.name
        h.v.isActivated = c.id == sel
    }
}
