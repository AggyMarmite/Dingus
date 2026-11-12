package app.dingus.tv

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Browse: categories on the left, channels with now/next on the right. Everything works with the remote. */
class MainActivity : Activity() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())

    private lateinit var categories: RecyclerView
    private lateinit var channels: RecyclerView
    private lateinit var title: TextView
    private lateinit var count: TextView
    private lateinit var empty: TextView
    private lateinit var status: TextView
    private lateinit var clock: TextView

    private val catAdapter = CategoryAdapter()
    private val chanAdapter = ChannelAdapter()
    private var current: String = Repo.ALL
    private var loading = false
    private var askedForPlaylist = false
    private val repoListener: () -> Unit = { handler.post { reload() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        categories = findViewById(R.id.categories)
        channels = findViewById(R.id.channels)
        title = findViewById(R.id.title)
        count = findViewById(R.id.count)
        empty = findViewById(R.id.empty)
        status = findViewById(R.id.status)
        clock = findViewById(R.id.clock)

        categories.layoutManager = LinearLayoutManager(this)
        categories.adapter = catAdapter
        categories.itemAnimator = null
        channels.layoutManager = LinearLayoutManager(this)
        channels.adapter = chanAdapter
        channels.itemAnimator = null
        channels.setItemViewCacheSize(12)

        findViewById<View>(R.id.settings).setOnClickListener {
            startActivity(Intent(this, AddPlaylistActivity::class.java))
        }
        current = Store.lastCategory ?: Repo.ALL
        Repo.onChanged(repoListener)

        if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_m3u")?.let { url ->
            Store.playlist = Playlist("Test", "m3u", m3uUrl = url)
            Repo.clear()
        }
    }

    private fun start() {
        val p = Store.playlist
        if (p == null) {
            showEmpty("Add your playlist to get started.\nPress ◀ then choose “Playlist & settings”.")
            title.text = "Welcome"
            count.text = ""
            findViewById<View>(R.id.settings).requestFocus()
            if (!askedForPlaylist) { askedForPlaylist = true; startActivity(Intent(this, AddPlaylistActivity::class.java)) }
            return
        }
        scope.launch {
            if (Repo.channels.isEmpty()) Repo.loadSaved()
            reload()
            if (Repo.channels.isEmpty()) refresh(p, true) else {
                focusList()
                // Refresh quietly in the background if the saved copy is more than 12 hours old.
                if (System.currentTimeMillis() - Store.guideLoadedAt > 12 * 3600_000L) refresh(p, false)
            }
            maybeAutoPlay()
        }
    }

    private suspend fun refresh(p: Playlist, firstTime: Boolean) {
        if (loading) return
        loading = true
        try {
            setStatus("Loading channels…")
            if (firstTime) showEmpty("Loading your channels…")
            try {
                Repo.refreshChannels(p)
            } catch (e: Exception) {
                if (Repo.channels.isEmpty()) showEmpty("Couldn't load your playlist.\n${Net.friendly(e)}")
                setStatus(Net.friendly(e))
                Ui.test("load-failed ${e.message}")
                return
            }
            Ui.test("channels ${Repo.channels.size} categories ${Repo.categories.size}")
            if (firstTime) focusList()
            setStatus("Loading TV guide…")
            try {
                Repo.refreshGuide()
                setStatus("")
                Ui.test("guide ${Repo.guideChannels}")
            } catch (e: Exception) {
                setStatus("TV guide: ${Net.friendly(e)}")
                Ui.test("guide-failed ${e.message}")
            }
        } finally {
            loading = false
        }
    }

    private fun setStatus(s: String) { status.text = s; status.visibility = if (s.isEmpty()) View.GONE else View.VISIBLE }

    private fun showEmpty(s: String) {
        empty.text = s
        empty.visibility = View.VISIBLE
    }

    /** Rebuild both lists from the repo (after a refresh or coming back from the player). */
    private fun reload() {
        val cats = Repo.sideCategories()
        if (cats.none { it.id == current }) current = Repo.ALL
        catAdapter.set(cats)
        showCategory(current, force = true)
    }

    private fun showCategory(id: String, force: Boolean = false) {
        if (id == current && !force && chanAdapter.itemCount > 0) return
        current = id
        Store.lastCategory = id
        catAdapter.selected(id)
        val list = Repo.channelsIn(id)
        chanAdapter.set(list)
        title.text = catAdapter.items.firstOrNull { it.id == id }?.name ?: "All channels"
        count.text = if (list.isEmpty()) "" else "${list.size} channels"
        if (list.isEmpty() && Repo.channels.isNotEmpty()) showEmpty("Nothing here yet.")
        else if (list.isNotEmpty()) empty.visibility = View.GONE
    }

    private fun focusList() {
        channels.post {
            val last = Store.lastChannel
            val i = chanAdapter.items.indexOfFirst { it.id == last }.coerceAtLeast(0)
            if (chanAdapter.itemCount == 0) { categories.getChildAt(0)?.requestFocus(); return@post }
            (channels.layoutManager as LinearLayoutManager).scrollToPositionWithOffset(i, Ui.dp(this, 120))
            channels.post { channels.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
        }
    }

    private fun play(c: Channel) {
        startActivity(Intent(this, PlayerActivity::class.java)
            .putExtra(PlayerActivity.CHANNEL, c.id)
            .putExtra(PlayerActivity.CATEGORY, current))
    }

    private fun maybeAutoPlay() {
        if (!BuildConfig.DEBUG || !intent.getBooleanExtra("dingus_test_play", false)) return
        intent.removeExtra("dingus_test_play")
        Repo.channelsIn(current).firstOrNull()?.let { play(it) }
    }

    private val tick = object : Runnable {
        override fun run() {
            clock.text = Ui.time(System.currentTimeMillis())
            chanAdapter.notifyItemRangeChanged(0, chanAdapter.itemCount, "tick")
            handler.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L)
        }
    }

    override fun onResume() {
        super.onResume()
        Ui.test("main-shown")
        if (Repo.channels.isEmpty() && !loading) start()
        else if (Repo.channels.isNotEmpty()) { reload(); focusList() }
        tick.run()
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
    }

    override fun onDestroy() {
        Ui.test("main-closed")
        Repo.removeListener(repoListener)
        scope.cancel()
        super.onDestroy()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        // MENU on a channel toggles it as a favourite.
        if (keyCode == KeyEvent.KEYCODE_MENU) {
            val v = currentFocus
            val pos = v?.let { channels.findContainingViewHolder(it)?.bindingAdapterPosition } ?: -1
            if (pos >= 0) {
                val c = chanAdapter.items[pos]
                Store.toggleFavourite(c.id)
                val cats = Repo.sideCategories()
                catAdapter.set(cats)
                chanAdapter.notifyItemChanged(pos)
                return true
            }
        }
        // Left from the channel list goes back to the categories, landing on the current one.
        if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT && currentFocus?.let { channels.findContainingViewHolder(it) } != null) {
            val i = catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0)
            categories.scrollToPosition(i)
            categories.post { categories.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
            return true
        }
        if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT && currentFocus?.let { categories.findContainingViewHolder(it) } != null) {
            if (chanAdapter.itemCount > 0) {
                val lm = channels.layoutManager as LinearLayoutManager
                val first = lm.findFirstCompletelyVisibleItemPosition().coerceAtLeast(0)
                channels.findViewHolderForAdapterPosition(first)?.itemView?.requestFocus()
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    // ------------------------------------------------------------------ lists

    private inner class CategoryAdapter : RecyclerView.Adapter<CategoryAdapter.H>() {
        var items: List<Category> = emptyList()
        private var sel: String? = null
        private val pending = Runnable { pendingId?.let { showCategory(it) } }
        private var pendingId: String? = null

        fun set(list: List<Category>) { items = list; notifyDataSetChanged() }
        fun selected(id: String) {
            val old = items.indexOfFirst { it.id == sel }
            sel = id
            if (old >= 0) notifyItemChanged(old)
            val now = items.indexOfFirst { it.id == id }
            if (now >= 0) notifyItemChanged(now)
        }

        inner class H(val v: TextView) : RecyclerView.ViewHolder(v)

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_category, parent, false) as TextView
            val h = H(v)
            // Moving onto a category shows its channels after a short pause (so scrolling past is smooth).
            v.setOnFocusChangeListener { _, has ->
                if (has) {
                    val p = h.bindingAdapterPosition
                    if (p >= 0) {
                        pendingId = items[p].id
                        handler.removeCallbacks(pending)
                        handler.postDelayed(pending, 160)
                    }
                }
            }
            v.setOnClickListener {
                val p = h.bindingAdapterPosition
                if (p >= 0) { handler.removeCallbacks(pending); showCategory(items[p].id); focusFirstChannel() }
            }
            return h
        }

        override fun onBindViewHolder(h: H, position: Int) {
            val c = items[position]
            h.v.text = c.name
            h.v.isActivated = c.id == sel
        }
    }

    private fun focusFirstChannel() {
        if (chanAdapter.itemCount == 0) return
        channels.scrollToPosition(0)
        channels.post { channels.findViewHolderForAdapterPosition(0)?.itemView?.requestFocus() }
    }

    private inner class ChannelAdapter : RecyclerView.Adapter<ChannelAdapter.H>() {
        var items: List<Channel> = emptyList()
        fun set(list: List<Channel>) { items = list; notifyDataSetChanged() }

        inner class H(v: View) : RecyclerView.ViewHolder(v) {
            val logo: ImageView = v.findViewById(R.id.logo)
            val initials: TextView = v.findViewById(R.id.initials)
            val number: TextView = v.findViewById(R.id.number)
            val name: TextView = v.findViewById(R.id.name)
            val fav: TextView = v.findViewById(R.id.fav)
            val now: TextView = v.findViewById(R.id.now)
            val next: TextView = v.findViewById(R.id.next)
            val progress: ProgressBar = v.findViewById(R.id.progress)
        }

        override fun getItemCount() = items.size
        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_channel, parent, false)
            val h = H(v)
            v.setOnClickListener { val p = h.bindingAdapterPosition; if (p >= 0) play(items[p]) }
            return h
        }

        override fun onBindViewHolder(h: H, position: Int, payloads: MutableList<Any>) {
            if (payloads.isNotEmpty()) { bindGuide(h, items[position]); return }
            super.onBindViewHolder(h, position, payloads)
        }

        override fun onBindViewHolder(h: H, position: Int) {
            val c = items[position]
            h.name.text = c.name
            h.number.text = if (c.number > 0) c.number.toString() else ""
            h.initials.text = Ui.initials(c.name)
            h.fav.visibility = if (Store.isFavourite(c.id)) View.VISIBLE else View.GONE
            Logos.load(h.logo, c.logo, Ui.dp(this@MainActivity, 52), h.initials)
            bindGuide(h, c)
        }

        private fun bindGuide(h: H, c: Channel) {
            val (now, next) = Repo.nowNext(c)
            if (now != null) {
                h.now.text = now.title
                h.progress.progress = Ui.progress(now)
                h.progress.visibility = View.VISIBLE
            } else {
                h.now.text = if (Repo.guideChannels == 0) "" else "No guide information"
                h.progress.visibility = View.INVISIBLE
            }
            h.next.text = next?.let { "Next  ${Ui.time(it.start)}  ${it.title}" } ?: ""
            h.next.visibility = if (next == null) View.GONE else View.VISIBLE
        }
    }
}
