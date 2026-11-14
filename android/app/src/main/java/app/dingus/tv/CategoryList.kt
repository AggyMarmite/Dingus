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
            CatPill.focus(v)
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
        CatPill.bind(h.v, c.name, c.count, c.id == sel)
    }
}

/**
 * Category and section pills: the count drawn at the right-hand end (darker on the white highlight), and the name
 * bold when it's highlighted or the one showing.
 */
object CatPill {
    fun bind(v: TextView, name: String, count: Int, activated: Boolean) {
        v.text = name
        v.isActivated = activated
        val d = if (count >= 0) Count(v, Ui.count(count)) else null
        v.setCompoundDrawablesRelative(null, null, d, null)
        style(v)
    }

    fun style(v: TextView) {
        v.setTypeface(null, if (v.isFocused || v.isActivated) android.graphics.Typeface.BOLD else android.graphics.Typeface.NORMAL)
    }

    /** Call from the item's focus listener. */
    fun focus(v: TextView) = style(v)

    private class Count(val v: TextView, val text: String) : android.graphics.drawable.Drawable() {
        private val p = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
            textSize = v.textSize * 0.8f
        }
        private val w = p.measureText(text).toInt()
        override fun getIntrinsicWidth() = w
        override fun getIntrinsicHeight() = (p.textSize * 1.2f).toInt()
        override fun isStateful() = true
        override fun onStateChange(state: IntArray): Boolean { invalidateSelf(); return true }
        override fun draw(c: android.graphics.Canvas) {
            val focused = state.contains(android.R.attr.state_focused)
            p.color = if (focused) 0xFF4B5565.toInt() else Themes.color(v.context, R.attr.dMuted)
            val b = bounds
            c.drawText(text, b.right - w.toFloat(), b.centerY() - (p.descent() + p.ascent()) / 2, p)
        }
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(cf: android.graphics.ColorFilter?) {}
        @Deprecated("Deprecated in Java") override fun getOpacity() = android.graphics.PixelFormat.TRANSLUCENT
    }
}
