package app.dingus.tv

import android.app.Activity
import android.content.Intent
import android.os.Bundle
import android.text.Editable
import android.text.TextWatcher
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputMethodManager
import android.widget.EditText
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.Normalizer

/** One search box for channels, what's on, films and TV shows. */
class SearchActivity : BaseActivity() {
    private lateinit var side: SideMenu

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var query: EditText
    private lateinit var status: TextView
    private lateinit var results: RecyclerView
    /** Where the highlight rests in the search panel: the box (OK on it brings up the Fire TV keyboard). */
    private val home: View get() = query
    private lateinit var adapter: RowsAdapter
    private var job: Job? = null
    /** The latest results, by kind (the filter chips pick which show). */
    private var found: Map<String, List<Card>> = emptyMap()
    private var filter = ALL
    private var top: Card? = null
    companion object {
        const val ALL = "All"; const val LIVE = "Live TV"; const val FILMS = "Films"; const val SHOWS = "TV Shows"; const val LATER = "On later"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_search)
        side = SideMenu.attach(this, SideMenu.Page.SEARCH)
        query = findViewById(R.id.query)
        status = findViewById(R.id.status)
        results = findViewById(R.id.results)
        // Results as picture rows (Live now, Films, TV Shows, On later), the same cards and motion as Home.
        adapter = RowsAdapter(this, onFocus = { c -> prefetch(c) }, onClick = { c -> open(c) })
        results.layoutManager = RowsLayoutManager(this)
        results.adapter = adapter
        results.itemAnimator = null
        findViewById<View>(R.id.topResult).apply {
            setOnClickListener { this@SearchActivity.top?.let { open(it) } }
            // Drawn above the results under it: lifted when highlighted, it reaches down over the first row, and the
            // results (drawn after it) covered its bottom edge. No shadow from this – it only decides what's on top.
            outlineProvider = null; translationZ = 2f
        }

        query.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) { search(s?.toString() ?: "", 250) }
        })
        query.setOnEditorActionListener { _, action, _ ->
            if (action == EditorInfo.IME_ACTION_SEARCH || action == EditorInfo.IME_ACTION_DONE) {
                endTyping()
                search(query.text.toString(), 0)
                Store.addRecentSearch(query.text.toString())
                results.post { firstResult()?.requestFocus() ?: home.requestFocus() }
                true
            } else false
        }
        // Speaking or typing into the box (Fire TV's keyboard): BACK or DOWN once it's closed comes back out.
        // (Off the box once it's closed – onto the results, or the letters: left on the box, some keyboards stay up
        // and keep the remote's buttons.)
        (query as? SearchBox)?.onBack = { endTyping(); afterTyping().requestFocus() }
        // The box is highlighted to start with (there's no "Voice search" any more): OK on it brings up Fire TV's keyboard
        // for speaking the search. Only then – highlighting it alone doesn't pop the keyboard up.
        query.showSoftInputOnFocus = false
        query.setOnClickListener { if (!typing) startTyping() }
        // (Highlighted but not typing: the cursor at the start, so ◀ still opens the side menu.)
        query.setOnFocusChangeListener { _, has -> if (has && !typing) query.setSelection(0) }
        query.setOnKeyListener { _, code, ev ->
            if (!typing && (code == KeyEvent.KEYCODE_DPAD_CENTER || code == KeyEvent.KEYCODE_ENTER)) {
                if (ev.action == KeyEvent.ACTION_UP) startTyping()
                return@setOnKeyListener true
            }
            if (!typing && code == KeyEvent.KEYCODE_DPAD_DOWN && ev.action == KeyEvent.ACTION_DOWN) { findViewById<android.view.ViewGroup>(R.id.keys).getChildAt(0)?.requestFocus(); return@setOnKeyListener true }
            // (Not typing: ◀ opens the side menu.)
            if (!typing && code == KeyEvent.KEYCODE_DPAD_LEFT) { if (ev.action == KeyEvent.ACTION_DOWN) side.open(); return@setOnKeyListener true }
            // (Not typing: ◀ ▶ don't wander through the text.)
            if (!typing && (code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_DPAD_RIGHT)) return@setOnKeyListener code == KeyEvent.KEYCODE_DPAD_RIGHT
            if (ev.action != KeyEvent.ACTION_DOWN) return@setOnKeyListener false
            when (code) {
                KeyEvent.KEYCODE_DPAD_DOWN -> { endTyping(); (firstResult() ?: findViewById<android.view.ViewGroup>(R.id.keys).getChildAt(0) ?: home).requestFocus(); true }
                KeyEvent.KEYCODE_BACK -> { endTyping(); afterTyping().requestFocus(); true }
                else -> false
            }
        }
        buildKeys()
        val accent = Themes.color(this, R.attr.dAccent)
        findViewById<View>(R.id.searchGlow).background = android.graphics.drawable.GradientDrawable().apply {
            gradientType = android.graphics.drawable.GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf((accent and 0x00FFFFFF) or (0x30 shl 24), (accent and 0x00FFFFFF) or (0x0C shl 24), 0)
            gradientRadius = resources.displayMetrics.widthPixels * 0.5f
            setGradientCenter(0.85f, -0.05f)
        }
        status.text = ""
        // Films and shows are loaded in the background so they can be searched too.
        // Get everything ready to search while you type (names worked out once, films and shows read in).
        scope.launch {
            withContext(Dispatchers.Default) { SearchIndex.channels(); SearchIndex.onTv() }
            if (Vod.available) for (k in listOf(Vod.MOVIES, Vod.SERIES)) try { Vod.library(k); withContext(Dispatchers.Default) { SearchIndex.vod(k) } } catch (_: Exception) {}
            if (query.text.isNotEmpty()) search(query.text.toString(), 0) else showRecent()
        }
        home.requestFocus()
        // (And again once the screen's laid out: asked for this early, the box didn't always take the highlight, so
        // nothing was highlighted – OK and ◀ did nothing.)
        query.post { if (currentFocus == null || currentFocus === window.decorView) { home.requestFocus(); Ui.test("search-focus box (again)") } }
        showRecent()
        if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_query")?.let { query.setText(it) }
    }

    private fun search(text: String, wait: Long) {
        job?.cancel()
        val words = SearchIndex.norm(text).split(' ').filter { it.isNotBlank() }
        if (words.isEmpty() || words.sumOf { it.length } < 2) { found = emptyMap(); top = null; showResults(); status.text = ""; showRecent(); return }
        findViewById<View>(R.id.recentBox).visibility = View.GONE
        job = scope.launch {
            if (wait > 0) delay(wait)
            val rows = withContext(Dispatchers.Default) {
                fun hit(n: String): Boolean { for (w in words) if (!n.contains(w)) return false; return true }
                val ci = SearchIndex.channels()
                val chans = ArrayList<Channel>()
                for (i in ci.names.indices) { if (hit(ci.names[i])) { chans.add(ci.items[i]); if (chans.size >= 40) break } }
                ensureActive()
                // What's on: programmes on now or later today whose title matches (one per channel).
                val tv = SearchIndex.onTv()
                val progs = ArrayList<Pair<Channel, Programme?>>()
                val seen = HashSet<String>()
                for (i in tv.titles.indices) {
                    if (progs.size >= 40) break
                    val c = tv.channels[i]
                    if (c.id !in seen && hit(tv.titles[i])) { seen.add(c.id); progs.add(c to tv.programmes[i]) }
                }
                ensureActive()
                val now = System.currentTimeMillis()
                val live = ArrayList<Card>(); val later = ArrayList<Card>()
                chans.forEach { live.add(Card.Live(it)) }
                progs.forEach { (ch, p) -> if (p != null && p.start > now) later.add(Card.Show(ch, p, Ui.time(p.start))) else live.add(Card.Show(ch, p)) }
                val films = ArrayList<Card>(); val shows = ArrayList<Card>()
                for (k in listOf(Vod.MOVIES, Vod.SERIES)) {
                    val vi = SearchIndex.vod(k) ?: continue
                    for (i in vi.names.indices) { if (hit(vi.names[i])) { (if (k == Vod.MOVIES) films else shows).add(Card.Title(k, vi.items[i])); if ((if (k == Vod.MOVIES) films else shows).size >= 60) break } }
                    ensureActive()
                }
                // The top result: a name that starts with what you typed (a film or show first, then a channel), else the first found.
                val q = words.joinToString(" ")
                fun nameOf(c: Card) = when (c) { is Card.Title -> c.item.name; is Card.Live -> c.channel.name; is Card.Show -> c.programme?.title ?: c.channel.name; else -> "" }
                val best = (films + shows + live).firstOrNull { SearchIndex.norm(nameOf(it)).startsWith(q) } ?: (films + shows + live + later).firstOrNull()
                linkedMapOf(LIVE to live as List<Card>, FILMS to films, SHOWS to shows, LATER to later).filterValues { it.isNotEmpty() } to best
            }
            found = rows.first; top = rows.second
            if (filter != ALL && filter !in found) filter = ALL
            showResults()
            val n = found.values.sumOf { it.size }
            status.text = if (n == 0) "Nothing found for “$text”." else ""
            Ui.test("search \"$text\" $n")
        }
    }

    /** The rows for the chosen filter, the filter chips, and the top result. */
    private fun showResults() {
        val title = mapOf(LIVE to "Live now", FILMS to "Films", SHOWS to "TV Shows", LATER to "On later")
        adapter.set(found.filterKeys { filter == ALL || it == filter }.map { (k, v) -> Row(title[k] ?: k, v) })
        val chipsRow = findViewById<android.widget.LinearLayout>(R.id.chips)
        chipsRow.removeAllViews()
        findViewById<View>(R.id.chipsBox).visibility = if (found.isEmpty()) View.GONE else View.VISIBLE
        for (k in listOf(ALL) + found.keys) {
            val chip = TextView(android.view.ContextThemeWrapper(this, R.style.Choice), null, 0, R.style.Choice)
            chip.text = if (k == ALL) ALL else "$k  ·  ${found[k]?.size}"
            chip.textSize = 13f
            chip.setPadding(Ui.dp(this, 16), Ui.dp(this, 7), Ui.dp(this, 16), Ui.dp(this, 7))
            chip.layoutParams = android.widget.LinearLayout.LayoutParams(-2, -2).apply { marginEnd = Ui.dp(this@SearchActivity, 8) }
            chip.isActivated = k == filter
            chip.setOnClickListener { filter = k; showResults(); Ui.test("search-filter $k") }
            chipsRow.addView(chip)
        }
        showTop(if (filter == ALL) top else null)
    }

    private fun showTop(c: Card?) {
        val box = findViewById<View>(R.id.topResult)
        if (c == null) { box.visibility = View.GONE; return }
        box.visibility = View.VISIBLE
        val img = findViewById<ImageView>(R.id.topImage); val ini = findViewById<TextView>(R.id.topInitials)
        val k = findViewById<TextView>(R.id.topKicker); val t = findViewById<TextView>(R.id.topTitle); val m = findViewById<TextView>(R.id.topMeta)
        img.clipToOutline = true
        when (c) {
            is Card.Title -> {
                k.text = "TOP RESULT  ·  " + if (c.kind == Vod.MOVIES) "FILM" else "TV SHOW"
                t.text = c.item.name; ini.text = Ui.initials(c.item.name)
                val det = Vod.cachedDetails(c.kind, c.item.id)
                m.text = listOfNotNull(c.item.year.takeIf { it > 0 }?.toString(), det?.genre?.split(',')?.firstOrNull()?.trim(), c.item.rating.takeIf { it > 0 }?.let { "★ %.1f".format(it) }).joinToString("  ·  ")
                val bg = TitleLogos.cachedBackdrop(c.kind, c.item) ?: det?.backdrop
                Posters.load(img, bg ?: c.item.poster, Ui.dp(this, 112), ini, wide = bg != null)
                prefetch(c)
            }
            is Card.Live -> {
                k.text = "TOP RESULT  ·  CHANNEL"; t.text = c.channel.name; ini.text = Ui.initials(c.channel.name)
                m.text = Repo.nowNext(c.channel).first?.let { "Now: ${it.title}" } ?: ""
                img.scaleType = ImageView.ScaleType.FIT_CENTER
                Logos.load(img, c.channel.logo, Ui.dp(this, 80), ini)
            }
            is Card.Show -> {
                val p = c.programme
                k.text = "TOP RESULT  ·  " + c.channel.name.uppercase(); t.text = p?.title ?: c.channel.name; ini.text = Ui.initials(c.channel.name)
                m.text = p?.let { Ui.span(it) + "  ·  " + Ui.minutesLeft(it) } ?: ""
                img.scaleType = ImageView.ScaleType.FIT_CENTER
                Logos.load(img, c.channel.logo, Ui.dp(this, 80), ini)
            }
            else -> box.visibility = View.GONE
        }
    }

    /** Resting on a film or show fetches its details, so its page opens with them. */
    private fun prefetch(c: Card) {
        val t = c as? Card.Title ?: return
        if (Vod.cachedDetails(t.kind, t.item.id) != null) return
        scope.launch { delay(250); try { Vod.details(t.kind, t.item) } catch (_: Exception) {} }
    }

    private fun open(c: Card) {
        Store.addRecentSearch(query.text.toString())
        when (c) {
            is Card.Title -> startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, c.kind).putExtra(DetailActivity.ID, c.item.id), Motion.from(currentFocus))
            is Card.Live -> startActivity(Intent(this, MainActivity::class.java).putExtra(MainActivity.PLAY, c.channel.id).putExtra(MainActivity.PLAY_CATEGORY, Repo.ALL))
            is Card.Show -> {
                val p = c.programme
                if (p != null && p.start > System.currentTimeMillis()) Reminders.ask(this, c.channel, p, watch = { open(Card.Live(c.channel)) }) { msg -> Notice.done(msg, 2_500) }
                else open(Card.Live(c.channel))
            }
            else -> {}
        }
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    /** Where the highlight goes once the keyboard's closed: the first result, else the first letter. */
    private fun afterTyping(): View = firstResult() ?: findViewById<android.view.ViewGroup>(R.id.keys).getChildAt(0) ?: home

    private fun firstResult(): View? = findViewById<View>(R.id.topResult).takeIf { it.visibility == View.VISIBLE }
        ?: (results.findViewHolderForAdapterPosition(0) as? RowsAdapter.H)?.list?.getChildAt(0)

    // ------------------------------------------------------------------ the keyboard and voice

    /** Typing: the box takes the Fire TV keyboard. */
    private var typing = false

    private fun startTyping() {
        typing = true
        query.showSoftInputOnFocus = true
        query.isFocusable = true; query.isFocusableInTouchMode = true
        query.requestFocus()
        query.setSelection(query.text.length)
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(query, InputMethodManager.SHOW_FORCED)
        Ui.test("search-typing")
    }

    private fun endTyping() {
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(query.windowToken, 0)
        typing = false
        query.showSoftInputOnFocus = false
        query.isFocusableInTouchMode = false
        if (query.text.isNotBlank()) Store.addRecentSearch(query.text.toString())
        Ui.test("search-typing-done")
    }

    private fun buildKeys() {
        val grid = findViewById<android.widget.GridLayout>(R.id.keys)
        for (ch in "abcdefghijklmnopqrstuvwxyz1234567890") {
            val k = android.view.ContextThemeWrapper(this, R.style.SearchKey).let { TextView(it, null, 0, R.style.SearchKey) }
            k.text = ch.toString()
            k.layoutParams = android.widget.GridLayout.LayoutParams().apply {
                width = Ui.dp(this@SearchActivity, 38); height = Ui.dp(this@SearchActivity, 38)
                setMargins(0, 0, Ui.dp(this@SearchActivity, 3), Ui.dp(this@SearchActivity, 4))
            }
            k.setOnClickListener { type(ch.toString()) }
            grid.addView(k)
        }
        findViewById<View>(R.id.keySpace).setOnClickListener { if (query.text.isNotEmpty() && !query.text.endsWith(" ")) type(" ") }
        findViewById<View>(R.id.keyDelete).setOnClickListener { val t = query.text; if (t.isNotEmpty()) query.setText(t.subSequence(0, t.length - 1)) }
        findViewById<View>(R.id.keyClear).setOnClickListener { query.setText("") }
    }

    private fun type(s: String) { query.append(s); Ui.test("search-key $s") }

    /** Before you type: what you searched for lately, as chips, and what's popular right now. */
    private fun showRecent() {
        if (found.isEmpty()) scope.launch {
            // Worked out in the background: the first time, matching the chart against thousands of films and shows
            // took long enough on the screen's own thread that Search opened blank for a while.
            val pop = withContext(Dispatchers.Default) {
                Vod.trending(Vod.MOVIES).take(8).map { Card.Title(Vod.MOVIES, it) } + Vod.trending(Vod.SERIES).take(8).map { Card.Title(Vod.SERIES, it) }
            }
            if (found.isEmpty()) adapter.set(if (pop.size >= 3) listOf(Row("Popular right now", pop)) else emptyList())
        }
        val box = findViewById<View>(R.id.recentBox)
        val row = findViewById<android.widget.LinearLayout>(R.id.recent)
        val list = Store.recentSearches
        row.removeAllViews()
        box.visibility = if (list.isEmpty()) View.GONE else View.VISIBLE
        for (q in list) {
            val chip = TextView(android.view.ContextThemeWrapper(this, R.style.Choice), null, 0, R.style.Choice)
            chip.text = q; chip.textSize = 13f
            chip.setPadding(Ui.dp(this, 16), Ui.dp(this, 7), Ui.dp(this, 16), Ui.dp(this, 7))
            chip.layoutParams = android.widget.LinearLayout.LayoutParams(-2, -2).apply { marginEnd = Ui.dp(this@SearchActivity, 8) }
            chip.setOnClickListener { query.setText(q); query.setSelection(q.length) }
            row.addView(chip)
        }
    }

    /** The time, top right. */
    private val tick = object : Runnable {
        override fun run() {
            val c = findViewById<TextView>(R.id.clock) ?: return
            c.text = Ui.time(System.currentTimeMillis())
            c.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L + 50)
        }
    }
    override fun onStart() { super.onStart(); findViewById<View>(R.id.clock)?.removeCallbacks(tick); tick.run() }
    override fun onStop() { super.onStop(); findViewById<View>(R.id.clock)?.removeCallbacks(tick) }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // Nothing highlighted (on opening, or back from the keyboard): the box takes it.
        if (hasFocus && currentFocus == null && !side.isOpen) home.requestFocus()
        if (hasFocus) Ui.test("search-focus ${currentFocus?.let { resources.getResourceEntryName(it.id.takeIf { i -> i > 0 } ?: return@let it.javaClass.simpleName) }}")
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        if (BuildConfig.DEBUG && event.action == KeyEvent.ACTION_DOWN && event.keyCode == KeyEvent.KEYCODE_DPAD_LEFT)
            Ui.test("search-key left focus=${currentFocus?.javaClass?.simpleName}/${currentFocus?.id?.takeIf { it > 0 }?.let { resources.getResourceEntryName(it) }} typing=$typing menu=${side.isOpen}")
        // The remote's search / voice button, when Fire TV passes it on: straight to the keyboard and its mic.
        if (!typing && event.action == KeyEvent.ACTION_UP && (event.keyCode == KeyEvent.KEYCODE_SEARCH || event.keyCode == KeyEvent.KEYCODE_VOICE_ASSIST)) { startTyping(); return true }
        return backHold(event) || side.onKey(event) || super.dispatchKeyEvent(event)
    }

}
