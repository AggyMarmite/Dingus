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
import android.transition.ChangeBounds
import android.transition.TransitionManager
import android.view.animation.DecelerateInterpolator
import android.widget.FrameLayout
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

/**
 * The TV guide, with live TV: the preview window grows into full screen (and shrinks back) with the same stream,
 * so a channel never loads twice. Everything works with the remote.
 */
class MainActivity : BaseActivity(), GuideGrid.Listener {
    companion object {
        /** Open straight into full screen on this channel (from Home, Search, reminders). */
        const val PLAY = "play"
        const val PLAY_CATEGORY = "playCategory"
        /** Opened from the side menu: show the categories first. */
        const val CATEGORIES = "categories"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val handler = Handler(Looper.getMainLooper())

    private lateinit var grid: GuideGrid
    private lateinit var side: SideMenu
    private lateinit var categories: RecyclerView
    private lateinit var empty: TextView
    private lateinit var status: TextView
    private lateinit var hint: TextView
    private lateinit var previewText: TextView
    private lateinit var previewBox: View
    private lateinit var stage: FrameLayout
    private lateinit var root: ViewGroup
    private lateinit var guideArea: ViewGroup
    private lateinit var live: LiveTv
    private var pendingPlay: Pair<String, String>? = null
    private var resumePreview: String? = null
    private var lastAutoRefresh = 0L
    private val hintText by lazy { hint.text }

    private val catAdapter = CategoryAdapter()
    private var current: String = Repo.ALL
    private var loading = false
    private var askedForPlaylist = false
    private var loadAttempts = 0
    /** The guide has been filled in at least once (Home may have loaded the channels before this screen opened). */
    private var started = false
    private var showCategoriesFirst = false
    private var updateChecked = false
    private val repoListener: () -> Unit = { handler.post { onRepoChanged() } }

    private val startPreview = Runnable { grid.current?.let { if (live.current?.id != it.id) preview(it) } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)
        side = SideMenu(this, SideMenu.Page.LIVE, findViewById(R.id.guideScreen), leftOpens = false)
        // Live TV chosen again: the categories stay (or open). Anything else: back to the guide first.
        side.onNavigate = { p -> if (p == SideMenu.Page.LIVE) openCategories() else if (catPanel.visibility == View.VISIBLE) closeCategories() }
        grid = findViewById(R.id.grid)
        catPanel = findViewById(R.id.catPanel)
        categories = findViewById(R.id.categories)
        empty = findViewById(R.id.empty)
        status = findViewById(R.id.status)
        hint = findViewById(R.id.hint)
        previewText = findViewById(R.id.previewText)
        previewText.text = if (Store.preview) "Press OK on a channel\nto watch it here" else ""
        previewBox = findViewById(R.id.previewBox)
        stage = findViewById(R.id.stage)
        guideArea = findViewById(R.id.guideArea)
        root = findViewById(android.R.id.content)
        live = LiveTv(this, findViewById(R.id.video), findViewById(R.id.liveOverlay))
        // Changing channel in full screen moves the guide along with it.
        live.onChannel = { c -> if (gridIds.contains(c.id)) grid.focusChannel(c.id) }
        // The player sits exactly over the preview spot while browsing.
        previewBox.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> if (!live.fullScreen) placeStage() }
        hintText.length

        grid.listener = this
        categories.layoutManager = LinearLayoutManager(this)
        categories.adapter = catAdapter
        categories.itemAnimator = null
        current = Store.lastCategory ?: Repo.ALL
        Repo.onChanged(repoListener)
        handleTestExtras(intent)
        takePlayRequest(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleTestExtras(intent)
        takePlayRequest(intent)
        if (Repo.channels.isNotEmpty()) runPendingPlay()
    }

    private fun takePlayRequest(i: Intent) {
        if (i.getBooleanExtra(CATEGORIES, false)) {
            i.removeExtra(CATEGORIES)
            // Already showing the guide: straight to the categories. Otherwise once it's filled in (start()).
            // Live TV from the side menu starts at the top of the category (not the channel you last watched).
            if (started && Repo.channels.isNotEmpty() && !live.fullScreen) handler.post { showCategory(current, null); openCategories() } else showCategoriesFirst = true
        }
        val id = i.getStringExtra(PLAY) ?: return
        pendingPlay = id to (i.getStringExtra(PLAY_CATEGORY) ?: Repo.ALL)
        i.removeExtra(PLAY)
    }

    /** Opened to watch a channel: show its category in the guide and go straight to full screen. */
    private fun runPendingPlay() {
        val (id, cat) = pendingPlay ?: return
        val c = Repo.channel(id) ?: return
        pendingPlay = null
        if (catPanel.visibility == View.VISIBLE) { catPanel.visibility = View.GONE; findViewById<View>(R.id.catHead)?.visibility = View.VISIBLE; grid.isFocusable = true }
        side.close(restore = false)
        val list = Repo.channelsIn(cat).let { l -> if (l.any { it.id == id }) l else Repo.channelsIn(Repo.ALL) }
        val useCat = if (Repo.channelsIn(cat).any { it.id == id }) cat else Repo.ALL
        if (useCat != current || !gridIds.contains(id)) showCategory(useCat, id) else grid.focusChannel(id)
        goFullScreen(c, list, animate = false)
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
        i.getStringExtra("dingus_test_xtream_backup")?.let { url ->
            // The main server is down (nothing there); the backup is the real test server.
            Store.playlist = Playlist("Test Backup", "xtream", server = "http://10.0.2.2:9", user = "test", pass = "test", id = "test-failover", backups = url)
            Store.lastCategory = null
            current = Repo.ALL
            Repo.clear(); Vod.clear()
        }
        if (i.getBooleanExtra("dingus_test_add_playlist", false)) startActivity(Intent(this, AddPlaylistActivity::class.java).putExtra(AddPlaylistActivity.NEW, true))
        i.getStringExtra("dingus_test_settings")?.let { startActivity(Intent(this, SettingsActivity::class.java).putExtra("section", it)) }
        if (i.getBooleanExtra("dingus_test_vpn_phone", false)) startActivity(Intent(this, VpnActivity::class.java).putExtra(VpnActivity.PHONE, true))
        i.getStringExtra("dingus_test_vpn_connect")?.let { name ->
            Vpn.provider = Vpn.CONFIGS
            scope.launch { Vpn.connect(Vpn.locations().firstOrNull { it.country == name }) }
        }
        if (i.getBooleanExtra("dingus_test_vpn_quick", false)) { Vpn.provider = Vpn.CONFIGS; scope.launch { Vpn.quickConnect() } }
        if (i.getBooleanExtra("dingus_test_vpn_off", false)) scope.launch { Vpn.disconnect() }
        if (i.getBooleanExtra("dingus_test_reminder", false)) Reminders.testSoon()
        if (i.getBooleanExtra("dingus_test_numbers_off", false)) { Store.showNumbers = false; grid.showNumbers = false; Ui.test("numbers false") }
        i.getStringExtra("dingus_test_search")?.let { q ->
            startActivity(Intent(this, SearchActivity::class.java).putExtra("dingus_test_query", q))
        }
        i.getStringExtra("dingus_test_update_api")?.let { api ->
            Updater.api = api
            Updater.check(this, manual = true, auto = true)
        }
    }

    private fun start() {
        started = true
        val p = Store.playlist
        if (p == null) {
            grid.setChannels(emptyList(), null)
            showEmpty(Empty.text(this, Empty.TV, "Welcome to Dingus", "Press OK to add your playlist."))
            setInfo(null, null)
            if (!askedForPlaylist) { askedForPlaylist = true; startActivity(Intent(this, AddPlaylistActivity::class.java)) }
            return
        }
        scope.launch {
            // The saved channels first (quick) so the guide appears, then the saved guide fills it in.
            val readGuide = Repo.channels.isEmpty() && Repo.loadSavedChannels() || (Repo.channels.isNotEmpty() && !Repo.hasGuide)
            if (readGuide) launch { Repo.loadSavedGuide() }
            // The category may belong to another playlist (after switching).
            val cats = Repo.sideCategories()
            if (cats.none { it.id == current }) current = Store.lastCategory?.takeIf { id -> cats.any { it.id == id } } ?: Repo.ALL
            // From the side menu: the top of the category. (Otherwise back on the channel you last watched.)
            showCategory(current, if (showCategoriesFirst) null else Store.lastChannel)
            grid.requestFocus()
            if (showCategoriesFirst && pendingPlay == null && Repo.channels.isNotEmpty()) { showCategoriesFirst = false; openCategories() }
            runPendingPlay()
            Vpn.startup() // (before anything goes online)
            if (Repo.channels.isEmpty()) refresh(p, true)
            else if (Store.refreshDue) refresh(p, false)
            runPendingPlay()
            maybeAutoPlay()
        }
    }

    private suspend fun refresh(p: Playlist, firstTime: Boolean) {
        if (loading) return
        loading = true
        try {
            setStatus("Updating channels…")
            Notice.working("Updating playlist & TV guide…")
            Vod.refreshInBackground() // films and shows download alongside, not afterwards
            if (firstTime) showEmpty("Loading your channels…")
            try {
                Repo.refreshChannels(p)
                loadAttempts = 0
            } catch (e: Exception) {
                Ui.test("load-failed ${e.message}")
                // Often the network isn't up yet (e.g. just after the stick wakes): try again a few times by itself.
                if (Repo.channels.isEmpty() && loadAttempts < 4) {
                    loadAttempts++
                    showEmpty("Waiting for the network…")
                    handler.postDelayed({ if (!isFinishing) scope.launch { refresh(p, true) } }, 3_000L * loadAttempts)
                    return
                }
                if (Repo.channels.isEmpty()) showEmpty(Empty.text(this@MainActivity, Empty.OFFLINE, "Couldn't load your playlist", "${Net.friendly(e)}\n\nPress ◀ for settings."))
                setStatus(Net.friendly(e))
                Notice.updateDone("Couldn't update the playlist: ${Net.friendly(e)}", 6_000)
                return
            }
            Ui.test("channels ${Repo.channels.size} categories ${Repo.categories.size}")
            setStatus("Updating the TV guide…")
            try {
                Repo.refreshGuide()
                setStatus("")
                Notice.updateDone("Playlist & TV guide updated")
                Ui.test("guide ${Repo.guideChannels}")
            } catch (e: Exception) {
                setStatus("TV guide: ${Net.friendly(e)}")
                Notice.updateDone("Playlist updated  ·  TV guide: ${Net.friendly(e)}", 6_000)
                Ui.test("guide-failed ${e.message}")
            }
        } finally {
            loading = false
        }
    }

    private fun setStatus(s: String) {
        status.text = s
        status.visibility = if (s.isEmpty()) View.GONE else View.VISIBLE
        hint.text = s
        hint.visibility = if (s.isEmpty()) View.GONE else View.VISIBLE
    }

    private fun showEmpty(s: CharSequence?) {
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
        // Rebuilding the list would move the highlight while you're scrolling through it, so only when it changed.
        if (cats.map { it.id to it.name } != catAdapter.items.map { it.id to it.name }) catAdapter.set(cats)
        catAdapter.selected(id)
        val list = Repo.channelsIn(id)
        gridIds = list.map { it.id }
        grid.title = ""
        showCatHead(cats.firstOrNull { it.id == id }?.name ?: "All channels", list.size)
        grid.setChannels(list, focusId)
        Ui.test("guide-filled ${list.size}")
        when {
            list.isNotEmpty() -> showEmpty(null)
            Repo.channels.isNotEmpty() -> {
                showEmpty(if (id == Repo.FAV) Empty.text(this, Empty.STAR, "No favourites yet", "In the guide, press ≡ on a channel (or hold OK) and choose Add to Favourites.")
                    else Empty.text(this, Empty.FOLDER, "Nothing in this category", "Its channels may be hidden: Settings → Live TV → Edit channels."))
                setInfo(null, null)
            }
        }
    }

    // ------------------------------------------------------------------ the info panel and preview

    override fun onSelected(c: Channel, p: Programme?) {
        setInfo(c, p)
        // Moving around the guide doesn't start anything: OK plays a channel in the window, OK again goes full screen.
    }

    /** Choose source… for a channel in the guide: a version (remembered) or the same channel elsewhere; it plays. */
    private fun chooseSourceFor(c: Channel) {
        val here = live.current?.id == c.id
        Repo.chooseSource(this, c, if (here) live.playing else null,
            pickVersion = { if (here) live.replayVersion() else if (Store.preview) preview(c) else goFullScreen(c) },
            pickOther = { src -> if (!here) { if (Store.preview) preview(c) else goFullScreen(c) }; live.playSource(src) })
    }

    private var infoLogoFor: String? = null

    /** The channel's logo, top right of the info area (its initials if it has none). */
    private fun showInfoLogo(c: Channel?) {
        if (c?.id == infoLogoFor) return
        infoLogoFor = c?.id
        val v = findViewById<android.widget.ImageView>(R.id.infoLogo) ?: return
        val ini = findViewById<TextView>(R.id.infoLogoInitials)
        (v.parent as View).visibility = if (c == null) View.INVISIBLE else View.VISIBLE
        if (c == null) return
        ini.text = Ui.initials(c.name)
        Logos.load(v, c.logo, Ui.dp(this, 58), ini)
    }

    private fun setInfo(c: Channel?, p: Programme?) {
        val ch = findViewById<TextView>(R.id.infoChannel)
        val title = findViewById<TextView>(R.id.infoTitle)
        val time = findViewById<TextView>(R.id.infoTime)
        val prog = findViewById<ProgressBar>(R.id.infoProgress)
        val live = findViewById<View>(R.id.infoLive)
        val desc = findViewById<TextView>(R.id.infoDesc)
        ch.text = c?.let { (if (Store.showNumbers && it.number > 0) "${it.number}   " else "") + it.name } ?: ""
        showInfoLogo(c)
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
            else -> "${Ui.day(p.start)}${Ui.span(p)}   ·   $mins min   ·   " + if (c != null && Reminders.has(c, p)) (if (Reminders.isSeries(c, p)) "⟳ Series reminder set" else "● Reminder set") else "OK to set a reminder"
        }
        prog.visibility = if (airing) View.VISIBLE else View.GONE
        live.visibility = if (airing) View.VISIBLE else View.GONE
        prog.progress = Ui.progress(p)
        desc.text = p.desc ?: ""
        showArt(p)
    }

    // ------------------------------------------------------------------ the picture behind the top (like Home)

    private val guideArt by lazy {
        val bg = Themes.color(this, R.attr.dBg); val clear = bg and 0x00FFFFFF
        val accent = Themes.color(this, R.attr.dAccent)
        findViewById<View>(R.id.guideGlow).background = android.graphics.drawable.GradientDrawable().apply {
            gradientType = android.graphics.drawable.GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf((accent and 0x00FFFFFF) or (0x44 shl 24), (accent and 0x00FFFFFF) or (0x12 shl 24), 0)
            gradientRadius = resources.displayMetrics.widthPixels * 0.4f
            setGradientCenter(0.75f, 0.2f)
        }
        findViewById<View>(R.id.guideShade).background = android.graphics.drawable.LayerDrawable(arrayOf(
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
                intArrayOf((bg and 0x00FFFFFF) or (0xEB shl 24), (bg and 0x00FFFFFF) or (0xB3 shl 24), (bg and 0x00FFFFFF) or (0x26 shl 24))),
            android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(bg, (bg and 0x00FFFFFF) or (0x33 shl 24), clear)),
        ))
        findViewById<ImageView>(R.id.guideArt)
    }
    private var artFor: Programme? = null
    private val artLookup = Runnable { lookUpArt() }

    /** The programme's picture (from TMDB, when it's matched), faded in behind the top; the glow when there's none. */
    private fun showArt(p: Programme) {
        if (artFor === p) return
        artFor = p
        handler.removeCallbacks(artLookup)
        val t = TitleLogos.forProgramme(p)
        val url = t?.let { TitleLogos.cachedBackdrop(it.first, it.second) }
        when {
            url != null -> setArt(url)
            t != null && TitleLogos.enabled && !TitleLogos.knowsBackdrop(t.first, t.second) -> { setArt(null); handler.postDelayed(artLookup, 450) }
            else -> setArt(null)
        }
    }

    /** (After a moment on a programme, not for every one scrolled past.) */
    private fun lookUpArt() {
        val p = artFor ?: return
        val t = TitleLogos.forProgramme(p) ?: return
        scope.launch {
            try { TitleLogos.find(t.first, t.second, null) } catch (_: Exception) {}
            if (artFor === p) TitleLogos.cachedBackdrop(t.first, t.second)?.let { setArt(it) }
        }
    }

    private fun setArt(url: String?) {
        val v = guideArt
        if (url == null) { if (v.tag != null) { v.animate().cancel(); v.setImageDrawable(null); v.tag = null }; return }
        if (v.tag == url) return
        v.alpha = 0f
        Posters.load(v, url, resources.displayMetrics.heightPixels / 2, wide = true)
        v.animate().alpha(1f).setDuration(if (Ui.lite) 0 else 350).start()
    }

    /** Plays [c] in the preview window (the stream full screen will carry on with). */
    private fun preview(c: Channel) {
        if (live.fullScreen) return
        if (!Store.preview) { live.release(); stage.visibility = View.INVISIBLE; previewText.text = c.name; return }
        previewText.text = ""
        placeStage()
        stage.visibility = View.VISIBLE
        live.play(Repo.channelsIn(current), c)
    }

    private fun stopPreview() {
        handler.removeCallbacks(startPreview)
        live.release()
    }

    /** Puts the player exactly over the preview spot. */
    private fun placeStage() {
        if (previewBox.width == 0) return
        val a = IntArray(2); val r = IntArray(2)
        previewBox.getLocationInWindow(a); root.getLocationInWindow(r)
        val lp = FrameLayout.LayoutParams(previewBox.width, previewBox.height)
        lp.leftMargin = a[0] - r[0]; lp.topMargin = a[1] - r[1]
        stage.layoutParams = lp
    }

    private fun smoothly(target: ViewGroup, ms: Long) {
        if (Ui.lite) return // lite mode: things just move, no animation
        TransitionManager.beginDelayedTransition(target, ChangeBounds().setDuration(ms).setInterpolator(DecelerateInterpolator()))
    }

    /** Only the picture moves (the rest of the screen isn't animated along with it, which is what made it janky). */
    private fun animateStage(ms: Long, delay: Long = 0) {
        TransitionManager.beginDelayedTransition(root, ChangeBounds().setDuration(ms).setStartDelay(delay)
            .setInterpolator(DecelerateInterpolator()).addTarget(stage))
    }

    /** The whole guide (picture area, categories, grid): fades away behind the full-screen picture and back. */
    private val guideScreen: View by lazy { findViewById(R.id.guideScreen) }
    private val hideSide = Runnable { if (live.fullScreen) side.setHidden(true) }

    /** The preview grows to fill the screen while the guide fades out, then the info bar fades in. */
    private fun goFullScreen(c: Channel, list: List<Channel> = Repo.channelsIn(current), animate: Boolean = true) {
        handler.removeCallbacks(startPreview)
        handler.removeCallbacks(hideSide)
        if (live.current?.id != c.id) placeStage()
        live.play(list, c)
        stage.visibility = View.VISIBLE
        val grow = animate && live.current != null && !Ui.lite
        live.animatedEnter = grow
        guideScreen.animate().cancel()
        if (grow) {
            animateStage(320)
            guideScreen.animate().alpha(0f).setStartDelay(0).setDuration(220).withLayer().start()
        } else guideScreen.alpha = 0f
        stage.layoutParams = FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        stage.setBackgroundColor(0xFF000000.toInt())
        if (grow) handler.postDelayed(hideSide, 320) else side.setHidden(true)
        live.enterFullScreen()
    }

    /** BACK from full screen: the info bar fades, the picture shrinks back into the preview and the guide fades in around it, on the channel you were watching. */
    private fun leaveFullScreen() {
        val c = live.current
        val infoWas = live.infoShowing
        live.leaveFullScreen()
        handler.removeCallbacks(hideSide)
        side.setHidden(false)
        stage.background = null
        guideScreen.animate().cancel()
        if (Ui.lite) guideScreen.alpha = 1f
        else {
            animateStage(300, if (infoWas) 100 else 0)
            guideScreen.animate().alpha(1f).setStartDelay(if (infoWas) 120 else 40).setDuration(280).withLayer().start()
        }
        placeStage()
        if (c != null) { if (gridIds.contains(c.id)) grid.focusChannel(c.id) else showCategory(current, c.id) }
        if (!Store.preview) { live.release(); stage.visibility = View.INVISIBLE }
        grid.isFocusable = true
        grid.requestFocus()
        Ui.test("guide-shown ${c?.name}")
    }

    // ------------------------------------------------------------------ actions

    override fun onPlay(c: Channel, p: Programme?) {
        Ui.test("ok ${c.name} programme=${p?.title} playing=${live.current?.name} full=${live.fullScreen}")
        // A programme still to come: set a reminder (or watch the channel now).
        if (p != null && p.start > System.currentTimeMillis()) {
            Reminders.ask(this, c, p, watch = { onPlay(c, null) }) { msg ->
                grid.invalidate()
                setInfo(c, p) // ("● Reminder set" straight away)
                setStatus(msg)
                handler.postDelayed({ if (!loading) setStatus("") }, 2_500)
            }
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
        // First OK plays the channel in the guide's window; OK again on the same channel goes full screen.
        if (Store.preview && live.current?.id != c.id) preview(c) else goFullScreen(c)
    }

    /** ≡ on a channel in the guide: everything you can do with it. */
    override fun onMenu(c: Channel, p: Programme?) {
        val now = System.currentTimeMillis()
        val items = ArrayList<Pair<String, () -> Unit>>()
        if (p != null && p.start > now) items.add((if (Reminders.has(c, p)) "Cancel reminder" else "Remind me") to { onPlay(c, p) })
        if (p != null && p.stop <= now && Repo.canCatchUp(c, p)) items.add("Watch from the start (catch-up)" to { onPlay(c, p) })
        if (p != null && Repo.canStartOver(c, p, now)) items.add(0, "↺  Start over  ·  from ${Ui.time(p.start)}" to { stopPreview(); LiveTv.startOver(this, c, p) })
        val past = Repo.programmes(c).filter { Repo.canCatchUp(c, it, now) }.asReversed().take(60)
        if (past.isNotEmpty()) items.add(0, "Catch up…" to {
            Dialogs.side(this).setTitle("Catch up · ${c.name}")
                .setItems(past.map { q -> "${Ui.day(q.start)}  ${Ui.time(q.start)}   ${q.title}" }.toTypedArray()) { _, i -> onPlay(c, past[i]) }
                .show()
            Ui.test("catchup-list ${c.name} ${past.size}")
        })
        if (Store.preview && live.current?.id != c.id) items.add("Watch here" to { preview(c) })
        items.add("Watch full screen" to { goFullScreen(c) })
        items.add((if (Store.isFavourite(c.id)) "Remove from Favourites" else "Add to Favourites") to { onFavourite(c) })
        Repo.sourceCount(c).takeIf { it > 1 }?.let { n -> items.add("Choose source…  ·  $n" to { chooseSourceFor(c) }) }
        items.add("Rename channel" to {
            Dialogs.input(this, "New name", c.name, "Channel name") { n ->
                val e = ChannelEdits.all()[c.id]
                ChannelEdits.set(c.id, ChannelEdits.Edit(n.ifEmpty { null }, e?.number, e?.hidden == true))
                Repo.reapply()
            }
        })
        items.add("Hide channel" to { hideChannel(c) })
        Dialogs.builder(this).setTitle(c.name)
            .setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }
            .show()
        Ui.test("channel-menu ${c.name}")
    }

    private fun hideChannel(c: Channel) {
        Dialogs.builder(this)
            .setTitle("Hide “${c.name}”?")
            .setMessage("It won't show in the guide any more. You can bring it back in Settings → Edit channels.")
            .setPositiveButton("Hide") { _, _ ->
                val e = ChannelEdits.all()[c.id]
                ChannelEdits.set(c.id, ChannelEdits.Edit(e?.name, e?.number, true))
                Repo.reapply()
                setStatus("${c.name} hidden  ·  Settings → Edit channels to show it again")
                handler.postDelayed({ if (!loading) setStatus("") }, 3_500)
            }
            .setNegativeButton("Cancel", null)
            .show()
            .getButton(android.app.AlertDialog.BUTTON_POSITIVE)?.requestFocus()
    }

    /** Hold OK on a programme: a short menu of the things you'd want for it. */
    override fun onHold(c: Channel, p: Programme?) {
        val now = System.currentTimeMillis()
        val items = ArrayList<Pair<String, () -> Unit>>()
        val future = p != null && p.start > now
        val ended = p != null && p.stop <= now
        when {
            future -> {
                items.add((if (Reminders.has(c, p!!)) "🔔  Reminder options…" else "🔔  Remind me…") to { onPlay(c, p) })
            }
            ended && Repo.canCatchUp(c, p!!) -> items.add("↺  Watch from the start (catch-up)" to { onPlay(c, p) })
            else -> {
                items.add("▶  Watch full screen" to { goFullScreen(c) })
                if (p != null && Repo.canStartOver(c, p, now)) items.add("↺  Start over  ·  from ${Ui.time(p.start)}" to { stopPreview(); LiveTv.startOver(this, c, p) })
            }
        }
        Repo.sourceCount(c).takeIf { it > 1 }?.let { n -> items.add("⇄  Choose source…  ·  $n" to { chooseSourceFor(c) }) }
        items.add((if (Store.isFavourite(c.id)) "★  Remove from Favourites" else "★  Add to Favourites") to { onFavourite(c) })
        items.add("⊘  Hide channel" to { hideChannel(c) })
        val title = p?.let { "${it.title}  ·  ${c.name}  ·  ${Ui.span(it)}" } ?: c.name
        Dialogs.builder(this).setTitle(title)
            .setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }
            .show()
        Ui.test("hold-menu ${c.name}")
    }

    private fun onFavourite(c: Channel) {
        grid.invalidate()
        val on = Store.toggleFavourite(c.id)
        catAdapter.set(Repo.sideCategories())
        catAdapter.selected(current)
        setStatus(if (on) "Added ${c.name} to Favourites" else "Removed ${c.name} from Favourites")
        handler.postDelayed({ if (!loading) setStatus("") }, 2_500)
        if (current == Repo.FAV) showCategory(Repo.FAV, c.id)
    }

    override fun onLeftEdge() = openCategories()

    /** Above the guide while the column's folded away: "Sport · 96 channels · ◀ categories". */
    private fun showCatHead(name: String, n: Int) {
        val v = findViewById<TextView>(R.id.catHead) ?: return
        val muted = Themes.color(this, R.attr.dMuted)
        v.text = android.text.SpannableStringBuilder().apply {
            append(name); setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), 0, length, 0)
            val st = length
            append("  ·  ${Ui.count(n)} channel${if (n == 1) "" else "s"}  ·  ◀ categories")
            setSpan(android.text.style.ForegroundColorSpan(muted), st, length, 0)
            setSpan(android.text.style.RelativeSizeSpan(0.82f), st, length, 0)
        }
    }

    private lateinit var catPanel: View

    /** LEFT from the guide: the categories open to its left (the guide moves across), on the current one. */
    private fun openCategories() {
        if (catPanel.visibility == View.VISIBLE) return
        smoothly(guideArea, 180)
        catPanel.visibility = View.VISIBLE
        findViewById<View>(R.id.catHead)?.visibility = View.INVISIBLE
        grid.isFocusable = false // the guide can't take the remote's presses while the list is open
        val i = catAdapter.items.indexOfFirst { it.id == current }.coerceAtLeast(0)
        categories.scrollToPosition(i)
        focusCategory(i, 0)
        Ui.test("categories-open")
    }

    /** The list may not be laid out yet the moment it appears: try again on the next frames. */
    private fun focusCategory(i: Int, attempt: Int) {
        categories.post {
            if (catPanel.visibility != View.VISIBLE || side.isOpen) return@post
            val v = categories.findViewHolderForAdapterPosition(i)?.itemView
            if (v != null) v.requestFocus() else if (attempt < 10) handler.postDelayed({ focusCategory(i, attempt + 1) }, 30)
        }
    }

    private fun closeCategories() {
        if (catPanel.visibility != View.VISIBLE) return
        handler.removeCallbacks(catAdapter.pending)
        catAdapter.pending.run()
        smoothly(guideArea, 160)
        catPanel.visibility = View.GONE
        findViewById<View>(R.id.catHead)?.visibility = View.VISIBLE
        grid.isFocusable = true
        grid.requestFocus()
    }

    /** LEFT again (from the categories): the side menu opens, over the categories. */
    private fun openMenu() = side.open()

    private fun refreshGuideOnly() {
        if (loading) return
        loading = true
        scope.launch {
            try {
                setStatus("Updating the TV guide…"); Notice.working("Updating TV guide…")
                Repo.refreshGuide(); setStatus(""); Notice.done("TV guide updated"); Ui.test("guide ${Repo.guideChannels}")
            }
            catch (e: Exception) { setStatus("TV guide: ${Net.friendly(e)}"); Notice.done("TV guide: ${Net.friendly(e)}", 6_000) }
            finally { loading = false }
        }
    }

    private fun maybeAutoPlay() {
        if (!BuildConfig.DEBUG || !intent.getBooleanExtra("dingus_test_play", false)) return
        intent.removeExtra("dingus_test_play")
        grid.current?.let { goFullScreen(it) }
    }

    private val tick = object : Runnable {
        override fun run() {
            grid.refresh()
            if (!loading) Repo.keepGuideFresh()
            // Left open for a long time: update when it's due (your choice in Settings).
            val p = Store.playlist
            if (p != null && !loading && Repo.channels.isNotEmpty() && Store.refreshDue && System.currentTimeMillis() - lastAutoRefresh > 15 * 60_000L) {
                lastAutoRefresh = System.currentTimeMillis() // (if it fails, try again in a while rather than every minute)
                scope.launch { refresh(p, false) }
            }
            handler.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L)
        }
    }

    override fun onResume() {
        super.onResume()
        Ui.test("main-shown")
        if (started) Ui.test("guide-showing ${gridIds.size}")
        grid.genreColours = Store.genreColours
        grid.showNumbers = Store.showNumbers
        if ((!started || Repo.channels.isEmpty()) && !loading) start()
        else if (Repo.channels.isNotEmpty()) {
            grid.refresh()
            when {
                pendingPlay != null -> runPendingPlay()
                live.fullScreen -> live.resume() // back to the app while watching: carry on
                // Back from another screen: carry on with the channel that was playing in the window (if one was).
                // (A moment later, so the guide's on screen and answering the remote first.)
                else -> resumePreview?.let { id ->
                    handler.postDelayed({ if (!isFinishing && live.current == null && !live.fullScreen) Repo.channel(id)?.let { preview(it) } }, 600)
                }
            }
            if (!live.fullScreen && !side.isOpen && catPanel.visibility != View.VISIBLE) grid.requestFocus()
        }
        if (Repo.channels.isNotEmpty() && Store.guideLoadedAt == 0L) refreshGuideOnly() else Repo.keepGuideFresh()
        tick.run()
        if (!updateChecked && Store.playlist != null) {
            updateChecked = true
            if (System.currentTimeMillis() - Store.updateCheckedAt > 12 * 3600_000L) Updater.check(this, manual = false)
        }
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(tick)
        resumePreview = live.current?.id
        stopPreview() // (in full screen too: it picks up again when you come back)
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
        if (backHold(event)) return true
        if (BuildConfig.DEBUG && event.action == KeyEvent.ACTION_DOWN)
            Ui.test("key ${KeyEvent.keyCodeToString(event.keyCode)} on ${if (live.fullScreen) "full screen" else currentFocus?.let { it.javaClass.simpleName + "/" + ((it as? TextView)?.text ?: "") }}")
        // Full screen: the remote controls live TV.
        if (live.fullScreen && event.keyCode != KeyEvent.KEYCODE_BACK && live.onKey(event.keyCode, event)) return true
        if (!live.fullScreen && side.onKey(event)) return true
        if (event.action == KeyEvent.ACTION_DOWN && event.keyCode != KeyEvent.KEYCODE_BACK) {
            val f = currentFocus
            when {
                // The side menu keeps the remote's presses while it's open (see side.onKey above).
                side.isOpen -> {}
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
        if (!side.isOpen && catPanel.visibility != View.VISIBLE) {
            if (Store.playlist == null && (keyCode == KeyEvent.KEYCODE_DPAD_CENTER || keyCode == KeyEvent.KEYCODE_ENTER)) {
                startActivity(Intent(this, AddPlaylistActivity::class.java)); return true
            }
            if (keyCode == KeyEvent.KEYCODE_DPAD_LEFT && grid.current == null) { openMenu(); return true }
        }
        return super.onKeyDown(keyCode, event)
    }

    /** BACK steps out: guide → categories → menu → leave the app. */
    override fun onBackPressed() {
        if (live.fullScreen) { if (!live.onBack()) leaveFullScreen(); return }
        when {
            side.isOpen -> super.onBackPressed()
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
                CatPill.focus(v)
                val p = h.bindingAdapterPosition
                if (has && p >= 0) {
                    pendingId = items[p].id
                    handler.removeCallbacks(pending)
                    handler.postDelayed(pending, 250)
                }
            }
            v.setOnClickListener {
                val p = h.bindingAdapterPosition
                // Choosing a category starts at its first channel, even the one you came from. (▶ or BACK without
                // choosing goes back to where you were.)
                if (p >= 0) { val id = items[p].id; pendingId = id; closeCategories(); if (id == current) showCategory(id, null) }
            }
            // MENU on a category offers to hide it.
            v.setOnKeyListener { _, code, ev ->
                if (code != KeyEvent.KEYCODE_MENU) return@setOnKeyListener false
                val p = h.bindingAdapterPosition
                if (ev.action == KeyEvent.ACTION_UP && p >= 0 && !items[p].id.startsWith("\u0001")) {
                    val c = items[p]
                    Dialogs.categoryOptions(this@MainActivity, "live", items.map { it.id }.filter { !it.startsWith("\u0001") }, c.id, c.name) {
                        if (Store.isHidden("live:${c.id}") && current == c.id) showCategory(Repo.ALL, null)
                        set(Repo.sideCategories()); selected(current)
                        val i = items.indexOfFirst { it.id == c.id }.takeIf { it >= 0 } ?: 0
                        categories.scrollToPosition(i)
                        categories.post { categories.findViewHolderForAdapterPosition(i)?.itemView?.requestFocus() }
                    }
                }
                true
            }
            return h
        }

        override fun onBindViewHolder(h: H, position: Int) {
            val c = items[position]
            CatPill.bind(h.v, c.name, Repo.channelsIn(c.id).size, c.id == sel)
        }
    }
}
