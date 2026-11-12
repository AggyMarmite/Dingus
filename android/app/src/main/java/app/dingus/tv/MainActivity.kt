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
class MainActivity : BaseActivity(), GuideGrid.Listener {
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
        catPanel = findViewById(R.id.catPanel)
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
        // Going to another screen closes the menu, so you come back to the guide.
        fun go(i: Intent) { closeMenu(toGuide = true); startActivity(i) }
        findViewById<View>(R.id.navGuide).setOnClickListener { closeMenu(toGuide = true) }
        findViewById<View>(R.id.navPlaylist).setOnClickListener { switchPlaylist() }
        findViewById<View>(R.id.settings).setOnClickListener { go(Intent(this, SettingsActivity::class.java)) }
        findViewById<View>(R.id.navSearch).setOnClickListener { go(Intent(this, SearchActivity::class.java)) }
        findViewById<View>(R.id.navFilms).setOnClickListener {
            go(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, Vod.MOVIES))
        }
        findViewById<View>(R.id.navShows).setOnClickListener {
            go(Intent(this, LibraryActivity::class.java).putExtra(LibraryActivity.KIND, Vod.SERIES))
        }

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
            Store.playlist = Playlist("Test M3U", "m3u", m3uUrl = url, id = "test-m3u")
            Store.lastCategory = null
            current = Repo.ALL
            Repo.clear(); Vod.clear()
        }
        i.getStringExtra("dingus_test_xtream")?.let { url ->
            Store.playlist = Playlist("Test Xtream", "xtream", server = url, user = "test", pass = "test", id = "test-xtream")
            Store.lastCategory = null
            current = Repo.ALL
            Repo.clear(); Vod.clear()
        }
        i.getStringExtra("dingus_test_search")?.let { q ->
            startActivity(Intent(this, SearchActivity::class.java).putExtra("dingus_test_query", q))
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
            // The category may belong to another playlist (after switching).
            val cats = Repo.sideCategories()
            if (cats.none { it.id == current }) current = Store.lastCategory?.takeIf { id -> cats.any { it.id == id } } ?: Repo.ALL
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
            p.stop <= now && c != null && Repo.canCatchUp(c, p) -> "${Ui.day(p.start)}${Ui.span(p)}   ·   ↺ Catch up: press OK to watch"
            p.stop <= now -> "${Ui.day(p.start)}${Ui.span(p)}   ·   Ended"
            else -> "${Ui.day(p.start)}${Ui.span(p)}   ·   $mins min   ·   " + if (c != null && Reminders.has(c, p)) "● Reminder set" else "OK to set a reminder"
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

    override fun onPlay(c: Channel, p: Programme?) {
        // A programme still to come: set a reminder (or watch the channel now).
        if (p != null && p.start > System.currentTimeMillis()) {
            val set = Reminders.has(c, p)
            Dialogs.builder(this).setTitle(p.title).setMessage("${c.name}  ·  ${Ui.day(p.start)}${Ui.span(p)}")
                .setPositiveButton(if (set) "Cancel reminder" else "Remind me") { _, _ ->
                    val on = Reminders.toggle(c, p)
                    Ui.test("reminder-set $on ${p.title}")
                    grid.invalidate()
                    setStatus(if (on) "Reminder set for ${p.title}" else "Reminder cancelled")
                    handler.postDelayed({ if (!loading) setStatus("") }, 2_500)
                }
                .setNegativeButton("Watch ${c.name} now") { _, _ -> onPlay(c, null) }
                .show().getButton(android.app.AlertDialog.BUTTON_POSITIVE)?.requestFocus()
            return
        }
        // A programme that's finished, on a channel with catch-up: watch it from the start, with pause and skip.
        if (p != null && Repo.canCatchUp(c, p)) {
            val url = Repo.catchUpUrl(c, p) ?: return
            stopPreview() // one connection at a time: the full-screen player takes over
            Ui.test("catchup ${c.name} ${p.title}")
            startActivity(VodPlayerActivity.intent(this, VodPlayerActivity.CATCHUP, null,
                listOf(VodPlayerActivity.Entry("cu:${c.id}:${p.start}", "${c.name}  ·  ${p.title}", url)), 0, true))
            return
        }
        stopPreview()
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

    override fun onLeftEdge() = openCategories()

    private lateinit var catPanel: View

    /** LEFT from the guide: the categories appear over the channel names, with the current one highlighted. */
    private fun openCategories() {
        if (catPanel.visibility == View.VISIBLE) return
        catPanel.visibility = View.VISIBLE
        catPanel.translationX = -catPanel.width.toFloat().coerceAtLeast(200f)
        catPanel.animate().translationX(0f).setDuration(140).start()
        grid.isFocusable = false // the guide can't take the remote's presses while the list is open
        val i = catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0)
        categories.scrollToPosition(i)
        focusCategory(i, 0)
        Ui.test("categories-open")
    }

    /** The list may not be laid out yet the moment it appears: try again on the next frames. */
    private fun focusCategory(i: Int, attempt: Int) {
        categories.post {
            if (catPanel.visibility != View.VISIBLE || menu.visibility == View.VISIBLE) return@post
            val v = categories.findViewHolderForAdapterPosition(i)?.itemView
            if (v != null) v.requestFocus() else if (attempt < 10) handler.postDelayed({ focusCategory(i, attempt + 1) }, 30)
        }
    }

    private fun closeCategories() {
        if (catPanel.visibility != View.VISIBLE) return
        handler.removeCallbacks(catAdapter.pending)
        catAdapter.pending.run()
        catPanel.animate().translationX(-catPanel.width.toFloat()).setDuration(120).withEndAction {
            catPanel.visibility = View.GONE
        }.start()
        grid.isFocusable = true
        grid.requestFocus()
    }

    /** LEFT again (from the categories): the main menu – TV guide, search, films, TV shows, settings. */
    private fun openMenu() {
        if (menu.visibility == View.VISIBLE) return
        val vod = if (Vod.available) View.VISIBLE else View.GONE
        findViewById<View>(R.id.navFilms).visibility = vod
        findViewById<View>(R.id.navShows).visibility = vod
        findViewById<View>(R.id.navGuide).isActivated = true
        findViewById<TextView>(R.id.navPlaylist).apply {
            visibility = if (Store.playlists.size > 1) View.VISIBLE else View.GONE
            text = "Playlist: ${Store.playlist?.name ?: ""}"
        }
        menu.visibility = View.VISIBLE
        scrim.visibility = View.VISIBLE
        menu.translationX = -menu.width.toFloat().coerceAtLeast(300f)
        menu.animate().translationX(0f).setDuration(160).start()
        grid.isFocusable = false
        categories.descendantFocusability = ViewGroup.FOCUS_BLOCK_DESCENDANTS
        menu.post { findViewById<View>(R.id.navGuide).requestFocus() }
        Ui.test("menu-open")
    }

    /** Close the main menu, back to the categories ([toGuide] = straight to the guide). */
    private fun closeMenu(toGuide: Boolean = false) {
        if (menu.visibility != View.VISIBLE) return
        menu.animate().translationX(-menu.width.toFloat()).setDuration(140).withEndAction {
            menu.visibility = View.GONE
        }.start()
        scrim.visibility = View.GONE
        categories.descendantFocusability = ViewGroup.FOCUS_AFTER_DESCENDANTS
        if (toGuide || catPanel.visibility != View.VISIBLE) { catPanel.visibility = View.GONE; grid.isFocusable = true; grid.requestFocus() }
        else focusCategory(catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0), 0)
    }

    private fun switchPlaylist() {
        val all = Store.playlists
        Dialogs.builder(this).setTitle("Switch playlist")
            .setSingleChoiceItems(all.map { it.name }.toTypedArray(), all.indexOfFirst { it.id == Store.activeId }) { d, i ->
                d.dismiss()
                if (all[i].id != Store.activeId) {
                    closeMenu(toGuide = true)
                    stopPreview()
                    Playlists.switchTo(all[i].id)
                    current = Store.lastCategory ?: Repo.ALL
                    start()
                }
            }
            .show()
    }

    private fun refreshGuideOnly() {
        if (loading) return
        loading = true
        scope.launch {
            try { setStatus("Updating the TV guide…"); Repo.refreshGuide(); setStatus(""); Ui.test("guide ${Repo.guideChannels}") }
            catch (e: Exception) { setStatus("TV guide: ${Net.friendly(e)}") }
            finally { loading = false }
        }
    }

    private fun maybeAutoPlay() {
        if (!BuildConfig.DEBUG || !intent.getBooleanExtra("dingus_test_play", false)) return
        intent.removeExtra("dingus_test_play")
        grid.current?.let { onPlay(it, null) }
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
        grid.genreColours = Store.genreColours
        if (Repo.channels.isEmpty() && !loading) start()
        else if (Repo.channels.isNotEmpty()) {
            // Back from full screen: land on the channel just watched and preview it straight away.
            val last = Store.lastChannel
            if (Repo.channelsIn(current).none { it.id == last } || grid.current?.id != last) showCategory(current, last)
            else grid.refresh()
            grid.current?.let { if (previewing == null) preview(it) }
            if (menu.visibility != View.VISIBLE && catPanel.visibility != View.VISIBLE) grid.requestFocus()
        }
        if (Repo.channels.isNotEmpty() && Store.guideLoadedAt == 0L) refreshGuideOnly()
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

    private fun inside(v: View?, parent: View): Boolean {
        var x = v
        while (x != null) { if (x === parent) return true; x = x.parent as? View }
        return false
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (BuildConfig.DEBUG && event.action == KeyEvent.ACTION_DOWN)
            Ui.test("key ${KeyEvent.keyCodeToString(event.keyCode)} on ${currentFocus?.let { it.javaClass.simpleName + "/" + ((it as? TextView)?.text ?: "") }}")
        if (event.action == KeyEvent.ACTION_DOWN && event.keyCode != KeyEvent.KEYCODE_BACK) {
            val f = currentFocus
            when {
                // The main menu keeps the remote's presses while it's open.
                menu.visibility == View.VISIBLE -> {
                    if (event.keyCode == KeyEvent.KEYCODE_DPAD_RIGHT) { closeMenu(); return true }
                    if (!inside(f, menu)) { findViewById<View>(R.id.navGuide).requestFocus(); return true }
                }
                catPanel.visibility == View.VISIBLE -> {
                    when (event.keyCode) {
                        KeyEvent.KEYCODE_DPAD_RIGHT -> { closeCategories(); return true }
                        KeyEvent.KEYCODE_DPAD_LEFT -> { openMenu(); return true }
                    }
                    if (!inside(f, catPanel)) { focusCategory(catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0), 0); return true }
                }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        if (menu.visibility != View.VISIBLE && catPanel.visibility != View.VISIBLE) {
            if (Store.playlist == null && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
                startActivity(Intent(this, AddPlaylistActivity::class.java)); return true
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT && grid.current == null) { openMenu(); return true }
        }
        return super.onKeyDown(keyCode, event)
    }

    /** BACK steps out: guide → categories → menu → leave the app. */
    override fun onBackPressed() {
        when {
            menu.visibility == View.VISIBLE -> super.onBackPressed()
            catPanel.visibility == View.VISIBLE -> openMenu()
            grid.current == null -> openMenu()
            else -> openCategories()
        }
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
                if (p >= 0) { pendingId = items[p].id; closeCategories() }
            }
            // MENU on a category offers to hide it.
            v.setOnKeyListener { _, code, ev ->
                if (code != KeyEvent.KEYCODE_MENU) return@setOnKeyListener false
                val p = h.bindingAdapterPosition
                if (ev.action == KeyEvent.ACTION_UP && p >= 0 && !items[p].id.startsWith("\u0001")) {
                    val c = items[p]
                    Dialogs.hideCategory(this@MainActivity, "live:${c.id}", c.name) {
                        if (current == c.id) showCategory(Repo.ALL, null)
                        set(Repo.sideCategories()); selected(current)
                        categories.post { categories.getChildAt(0)?.requestFocus() }
                    }
                }
                true
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
