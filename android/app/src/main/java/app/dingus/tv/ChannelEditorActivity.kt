package app.dingus.tv

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView

/** Rename, renumber or hide channels (kept per playlist; the playlist itself isn't changed). */
class ChannelEditorActivity : BaseActivity() {
    private lateinit var channels: RecyclerView
    private val adapter = ChannelAdapter()
    private var current = Repo.ALL
    private var changed = false
    private lateinit var cats: CategoryList

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_channel_editor)
        channels = findViewById(R.id.channels)
        channels.layoutManager = LinearLayoutManager(this)
        channels.adapter = adapter
        channels.itemAnimator = null
        cats = CategoryList(onShow = { show(it.id) }, onPick = { show(it.id); channels.post { channels.getChildAt(0)?.requestFocus() } })
        val rv = findViewById<RecyclerView>(R.id.categories)
        rv.layoutManager = LinearLayoutManager(this)
        rv.adapter = cats
        rv.itemAnimator = null
        // Every category, including hidden ones, so their channels can be edited too.
        cats.set(listOf(Category(Repo.ALL, "All channels")) + Repo.categories, current)
        show(current)
        rv.post { rv.getChildAt(0)?.requestFocus() }
    }

    private fun show(id: String) {
        current = id
        cats.select(id)
        val all = Repo.allForEditing()
        adapter.set(if (id == Repo.ALL) all else all.filter { it.categoryId == id })
        findViewById<TextView>(R.id.title).text = cats.items.firstOrNull { it.id == id }?.name ?: ""
    }

    private fun editChannel(c: Channel) {
        val e = ChannelEdits.all()[c.id]
        val hidden = e?.hidden == true
        val opts = listOfNotNull("Rename", "Change number", if (hidden) "Show this channel" else "Hide this channel", if (e != null) "Undo my changes" else null)
        Dialogs.builder(this).setTitle(c.name)
            .setItems(opts.toTypedArray()) { _, i ->
                when (opts[i]) {
                    "Rename" -> Dialogs.input(this, "New name", c.name, "Channel name") { n ->
                        save(c, ChannelEdits.Edit(n.ifEmpty { null }, e?.number, hidden))
                    }
                    "Change number" -> Dialogs.input(this, "Channel number", c.number.toString(), "e.g. 101", number = true) { n ->
                        save(c, ChannelEdits.Edit(e?.name, n.toIntOrNull(), hidden))
                    }
                    "Hide this channel" -> save(c, ChannelEdits.Edit(e?.name, e?.number, true))
                    "Show this channel" -> save(c, ChannelEdits.Edit(e?.name, e?.number, false))
                    "Undo my changes" -> save(c, null)
                }
            }
            .show()
    }

    private fun save(c: Channel, e: ChannelEdits.Edit?) {
        ChannelEdits.set(c.id, e)
        changed = true
        Repo.reapply()
        show(current)
    }

    override fun onPause() {
        super.onPause()
        if (changed) { changed = false; Repo.reapply() }
    }

    private inner class ChannelAdapter : RecyclerView.Adapter<ChannelAdapter.H>() {
        var items: List<Channel> = emptyList()
        fun set(list: List<Channel>) { items = list; notifyDataSetChanged() }
        private val logoH by lazy { (50 * resources.displayMetrics.density).toInt() }

        inner class H(v: View) : RecyclerView.ViewHolder(v) {
            val image: ImageView = v.findViewById(R.id.image)
            val initials: TextView = v.findViewById(R.id.initials)
            val title: TextView = v.findViewById(R.id.title)
            val subtitle: TextView = v.findViewById(R.id.subtitle)
        }

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_result, parent, false)
            val h = H(v)
            v.setOnClickListener { val p = h.bindingAdapterPosition; if (p >= 0) editChannel(items[p]) }
            return h
        }

        override fun onBindViewHolder(h: H, position: Int) {
            val c = items[position]
            val e = ChannelEdits.all()[c.id]
            h.title.text = (if (c.number > 0) "${c.number}   " else "") + c.name
            h.title.alpha = if (e?.hidden == true) 0.45f else 1f
            h.subtitle.text = listOfNotNull(
                if (e?.hidden == true) "Hidden" else null,
                if (e?.name != null) "Renamed" else null,
                if (e?.number != null) "Renumbered" else null,
            ).joinToString("  ·  ")
            h.initials.text = Ui.initials(c.name)
            Logos.load(h.image, c.logo, logoH, h.initials)
        }
    }
}
