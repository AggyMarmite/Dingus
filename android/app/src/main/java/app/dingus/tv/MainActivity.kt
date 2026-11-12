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
import android.widget.ProgressBar
import android.widget.TextView
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.ui.PlayerView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** The TV guide, opened on start. Everything works with the remote. */
class MainActivity : Activity(), GuideGrid.Listener {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())

    private lateinit var grid: GuideGrid
    private lateinit var menu: View
    private lateinit var scrim: View
    private lateinit var categories: RecyclerView
    private lateinit var empty: TextView
    private lateinit var status: TextView
    private lateinit var hint: TextView
    private lateinit var clock: TextView
    private lateinit var previewView: PlayerView
    private lateinit var previewText: TextView
    private val hintText by lazy { hint.text }

    private val catAdapter = CategoryAdapter()
    private var current: String = Repo.ALL
    private var loading = false
    private var askedForPlaylist = false
    private var updateChecked = false
    private val repoListener: () -> Unit = { handler.post { onRepoChanged() } }

    private var preview: ExoPlayer? = null
    private var previewing: Channel? = null
    private var previewRetries = 0
    private val startPreview = Runnable { grid.current?.let { if (previewing?.id != it.id) preview(it) } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        grid = findViewById(R.id.grid)
        menu = findViewById(R.id.menu)
        scrim = findViewById(R.id.scrim)
        categories = findViewById(R.id.categories)
        empty = findViewById(R.id.empty)
        status = findViewById(R.id.status)
        hint = findViewById(R.id.hint)
        clock = findViewById(R.id.clock)
        previewView = findViewById(R.id.preview)
        previewText = findViewById(R.id.previewText)
        hintText.length

        grid.listener = this
        categories.layoutManager = LinearLayoutManager(this)
        categories.adapter = catAdapter
        categories.itemAnimator = null
        findViewById<View>(R.id.settings).setOnClickListener { startActivity(Intent(this, SettingsActivity::class.java)) }

        current = Store.lastCategory ?: Repo.ALL
        Repo.onChanged(repoListener)
        handleTestExtras(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleTestExtras(intent)
    }

    private fun handleTestExtras(i: Intent) {
        if (!BuildConfig.DEBUG) return
        i.getStringExtra("dingus_test_m3u")?.let { url ->
            Store.playlist = Playlist("Test", "m3u", m3uUrl = url)
            Store.lastCategory = null
            current = Repo.ALL
            Repo.clear()
        }
        i.getStringExtra("dingus_test_update_api")?.let { api ->
            Updater.api = api
            Updater.check(this, manual = true, auto = true)
        }
    }

    private fun start() {
        val p = Store.playlist
        if (p == null) {
            grid.setChannels(emptyList(), null)
            showEmpty("Welcome to Dingus.\nPress OK to add your playlist.")
            setInfo(null, null)
            if (!askedForPlaylist) { askedForPlaylist = true; startActivity(Intent(this, AddPlaylistActivity::class.java)) }
            return
        }
        scope.launch {
            if (Repo.channels.isEmpty()) Repo.loadSaved()
            showCategory(current, Store.lastChannel)
            grid.requestFocus()
            if (Repo.channels.isEmpty()) refresh(p, true)
            else if (System.currentTimeMillis() - Store.guideLoadedAt > 12 * 3600_000L) refresh(p, false)
            maybeAutoPlay()
        }
    }

    private suspend fun refresh(p: Playlist, firstTime: Boolean) {
        if (loading) return
        loading = true
        try {
            setStatus("Updating channels…")
            if (firstTime) showEmpty("Loading your channels…")
            try {
                Repo.refreshChannels(p)
            } catch (e: Exception) {
                if (Repo.channels.isEmpty()) showEmpty("Couldn't load your playlist.\n${Net.friendly(e)}\n\nPress ◀ for settings.")
                setStatus(Net.friendly(e))
                Ui.test("load-failed ${e.message}")
                return
            }
            Ui.test("channels ${Repo.channels.size} categories ${Repo.categories.size}")
            setStatus("Updating the TV guide…")
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

    private fun setStatus(s: String) {
        status.text = s
        status.visibility = if (s.isEmpty()) View.GONE else View.VISIBLE
        hint.text = s.ifEmpty { hintText }
    }

    private fun showEmpty(s: String?) {
        empty.text = s ?: ""
        empty.visibility = if (s == null) View.GONE else View.VISIBLE
    }

    private fun onRepoChanged() {
        val focus = grid.current?.id ?: Store.lastChannel
        catAdapter.set(Repo.sideCategories())
        if (catAdapter.items.none { it.id == current }) current = Repo.ALL
        val list = Repo.channelsIn(current)
        if (list.map { it.id } != gridIds) showCategory(current, focus) else grid.refresh()
    }

    private var gridIds: List<String> = emptyList()

    private fun showCategory(id: String, focusId: String?) {
        current = id
        Store.lastCategory = id
        val cats = Repo.sideCategories()
        catAdapter.set(cats)
        catAdapter.selected(id)
        val list = Repo.channelsIn(id)
        gridIds = list.map { it.id }
        grid.title = cats.firstOrNull { it.id == id }?.name ?: "All channels"
        grid.setChannels(list, focusId)
        when {
            list.isNotEmpty() -> showEmpty(null)
            Repo.channels.isNotEmpty() -> { showEmpty("Nothing here yet."); setInfo(null, null) }
        }
    }

    // ------------------------------------------------------------------ the info panel and preview

    override fun onSelected(c: Channel, p: Programme?) {
        setInfo(c, p)
        handler.removeCallbacks(startPreview)
        if (previewing?.id != c.id) handler.postDelayed(startPreview, 900)
    }

    private fun setInfo(c: Channel?, p: Programme?) {
        val ch = findViewById<TextView>(R.id.infoChannel)
        val title = findViewById<TextView>(R.id.infoTitle)
        val time = findViewById<TextView>(R.id.infoTime)
        val prog = findViewById<ProgressBar>(R.id.infoProgress)
        val live = findViewById<View>(R.id.infoLive)
        val desc = findViewById<TextView>(R.id.infoDesc)
        ch.text = c?.let { (if (it.number > 0) "${it.number}   " else "") + it.name } ?: ""
        val now = System.currentTimeMillis()
        if (p == null) {
            title.text = c?.name ?: ""
            time.text = if (c != null) "No guide information" else ""
            prog.visibility = View.GONE; live.visibility = View.GONE
            desc.text = ""
            return
        }
        title.text = p.title
        val mins = (p.stop - p.start) / 60_000
        val airing = p.start <= now && now < p.stop
        time.text = when {
            airing -> "${Ui.span(p)}   ·   ${(p.stop - now + 59_999) / 60_000} min left"
            else -> "${Ui.day(p.start)}${Ui.span(p)}   ·   $mins min"
        }
        prog.visibility = if (airing) View.VISIBLE else View.GONE
        live.visibility = if (airing) View.VISIBLE else View.GONE
        prog.progress = Ui.progress(p)
        desc.text = p.desc ?: ""
    }

    private fun preview(c: Channel) {
        if (!Store.preview) { stopPreview(); previewText.text = c.name; return }
        val p = preview ?: Players.create(this, small = true).also { pl ->
            preview = pl
            previewView.player = pl
            pl.addListener(object : Player.Listener {
                override fun onPlaybackStateChanged(state: Int) {
                    if (state == Player.STATE_READY) { previewRetries = 0; previewText.text = ""; Ui.test("preview ${previewing?.name}") }
                    if (state == Player.STATE_ENDED) previewing?.let { retryPreview(it) }
                }
                override fun onPlayerError(error: PlaybackException) {
                    val ch = previewing ?: return
                    if (previewRetries < 2) retryPreview(ch) else previewText.text = "${ch.name}\nisn't playing right now"
                }
            })
        }
        previewing = c
        previewRetries = 0
        previewText.text = "Loading…"
        previewView.visibility = View.VISIBLE
        p.setMediaItem(Players.item(c))
        p.prepare()
        p.playWhenReady = true
    }

    private fun retryPreview(c: Channel) {
        previewRetries++
        handler.postDelayed({ if (previewing?.id == c.id) { preview?.prepare(); preview?.playWhenReady = true } }, 1_500)
    }

    private fun stopPreview() {
        handler.removeCallbacks(startPreview)
        preview?.release()
        preview = null
        previewing = null
        previewView.player = null
    }

    // ------------------------------------------------------------------ actions

    override fun onPlay(c: Channel) {
        stopPreview() // one connection at a time: the full-screen player takes over
        startActivity(Intent(this, PlayerActivity::class.java)
            .putExtra(PlayerActivity.CHANNEL, c.id)
            .putExtra(PlayerActivity.CATEGORY, current))
    }

    override fun onFavourite(c: Channel) {
        val on = Store.toggleFavourite(c.id)
        catAdapter.set(Repo.sideCategories())
        catAdapter.selected(current)
        setStatus(if (on) "Added ${c.name} to Favourites" else "Removed ${c.name} from Favourites")
        handler.postDelayed({ if (!loading) setStatus("") }, 2_500)
        if (current == Repo.FAV) showCategory(Repo.FAV, c.id)
    }

    override fun onLeftEdge() = openMenu()

    private fun openMenu() {
        if (menu.visibility == View.VISIBLE) return
        menu.visibility = View.VISIBLE
        scrim.visibility = View.VISIBLE
        menu.translationX = -menu.width.toFloat().coerceAtLeast(300f)
        menu.animate().translationX(0f).setDuration(160).start()
        val i = catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0)
        categories.scrollToPosition(i)
        categories.post {
            val v = categories.findViewHolderForAdapterPosition(i)?.itemView
            if (v != null) v.requestFocus() else findViewById<View>(R.id.settings).requestFocus()
        }
        Ui.test("menu-open")
    }

    private fun closeMenu() {
        if (menu.visibility != View.VISIBLE) return
        handler.removeCallbacks(catAdapter.pending)
        catAdapter.pending.run()
        menu.animate().translationX(-menu.width.toFloat()).setDuration(140).withEndAction {
            menu.visibility = View.GONE
        }.start()
        scrim.visibility = View.GONE
        grid.requestFocus()
    }

    private fun maybeAutoPlay() {
        if (!BuildConfig.DEBUG || !intent.getBooleanExtra("dingus_test_play", false)) return
        intent.removeExtra("dingus_test_play")
        grid.current?.let { onPlay(it) }
    }

    private val tick = object : Runnable {
        override fun run() {
            clock.text = Ui.time(System.currentTimeMillis())
            grid.refresh()
            handler.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L)
        }
    }

    override fun onResume() {
        super.onResume()
        Ui.test("main-shown")
        if (Repo.channels.isEmpty() && !loading) start()
        else if (Repo.channels.isNotEmpty()) {
            // Back from full screen: land on the channel just watched and preview it straight away.
            val last = Store.lastChannel
            if (Repo.channelsIn(current).none { it.id == last } || grid.current?.id != last) showCategory(current, last)
            else grid.refresh()
            grid.current?.let { if (previewing == null) preview(it) }
            if (menu.visibility != View.VISIBLE) grid.requestFocus()
        }
        tick.run()
        if (!updateChecked && Store.playlist != null) {
            updateChecked = true
            if (System.currentTimeMillis() - Store.updateCheckedAt > 12 * 3600_000L) Updater.check(this, manual = false)
        }
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
        stopPreview()
    }

    override fun onDestroy() {
        Ui.test("main-closed")
        Repo.removeListener(repoListener)
        scope.cancel()
        super.onDestroy()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (menu.visibility == View.VISIBLE) {
            if (keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) { closeMenu(); return true }
        } else if (Store.playlist == null && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
            startActivity(Intent(this, AddPlaylistActivity::class.java)); return true
        } else if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT && grid.current == null) {
            openMenu(); return true
        }
        return super.onKeyDown(keyCode, event)
    }

    /** BACK opens the menu; BACK again (from the menu) leaves the app. */
    override fun onBackPressed() {
        if (menu.visibility == View.VISIBLE) super.onBackPressed() else openMenu()
    }

    // ------------------------------------------------------------------ categories menu

    private inner class CategoryAdapter : RecyclerView.Adapter<CategoryAdapter.H>() {
        var items: List<Category> = emptyList()
        private var sel: String? = null
        private var pendingId: String? = null
        val pending = Runnable { pendingId?.let { if (it != current) showCategory(it, null) }; pendingId = null }

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
            // Moving onto a category shows its channels behind the menu after a short pause.
            v.setOnFocusChangeListener { _, has ->
                val p = h.bindingAdapterPosition
                if (has && p >= 0) {
                    pendingId = items[p].id
                    handler.removeCallbacks(pending)
                    handler.postDelayed(pending, 250)
                }
            }
            v.setOnClickListener {
                val p = h.bindingAdapterPosition
                if (p >= 0) { pendingId = items[p].id; closeMenu() }
            }
            return h
        }

        override fun onBindViewHolder(h: H, position: Int) {
            val c = items[position]
            h.v.text = c.name
            h.v.isActivated = c.id == sel
        }
    }
}
