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
    private sealed class Row {
        class Heading(val text: String) : Row()
        class Live(val c: Channel, val p: Programme?) : Row()
        class Item(val kind: String, val m: VodItem) : Row()
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var query: EditText
    private lateinit var status: TextView
    private lateinit var results: RecyclerView
    private lateinit var voice: TextView
    private val adapter = ResultAdapter()
    private var job: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_search)
        side = SideMenu.attach(this, SideMenu.Page.SEARCH)
        query = findViewById(R.id.query)
        status = findViewById(R.id.status)
        results = findViewById(R.id.results)
        results.layoutManager = LinearLayoutManager(this)
        results.adapter = adapter
        results.itemAnimator = null

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
                results.post { firstResult()?.requestFocus() ?: voice.requestFocus() }
                true
            } else false
        }
        // Speaking or typing into the box (Fire TV's keyboard): BACK or DOWN once it's closed comes back out.
        query.setOnKeyListener { _, code, ev ->
            if (ev.action != KeyEvent.ACTION_DOWN) return@setOnKeyListener false
            when (code) {
                KeyEvent.KEYCODE_DPAD_DOWN -> { endTyping(); (firstResult() ?: voice).requestFocus(); true }
                KeyEvent.KEYCODE_BACK -> { endTyping(); voice.requestFocus(); true }
                else -> false
            }
        }
        voice = findViewById(R.id.voice)
        voice.setOnClickListener { startTyping() }
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
            if (query.text.isNotEmpty()) search(query.text.toString(), 0)
        }
        voice.requestFocus()
        showRecent()
        if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_query")?.let { query.setText(it) }
    }

    private fun search(text: String, wait: Long) {
        job?.cancel()
        val words = SearchIndex.norm(text).split(' ').filter { it.isNotBlank() }
        if (words.isEmpty() || words.sumOf { it.length } < 2) { adapter.set(emptyList()); status.text = ""; showRecent(); return }
        findViewById<View>(R.id.recentBox).visibility = View.GONE
        job = scope.launch {
            if (wait > 0) delay(wait)
            val rows = withContext(Dispatchers.Default) {
                fun hit(n: String): Boolean { for (w in words) if (!n.contains(w)) return false; return true }
                val out = ArrayList<Row>()
                val ci = SearchIndex.channels()
                val chans = ArrayList<Channel>()
                for (i in ci.names.indices) { if (hit(ci.names[i])) { chans.add(ci.items[i]); if (chans.size >= 40) break } }
                ensureActive()
                // What's on: programmes on now or later today whose title matches (one per channel).
                val tv = SearchIndex.onTv()
                val progs = ArrayList<Row.Live>()
                val seen = HashSet<String>()
                for (i in tv.titles.indices) {
                    if (progs.size >= 40) break
                    val c = tv.channels[i]
                    if (c.id !in seen && hit(tv.titles[i])) { seen.add(c.id); progs.add(Row.Live(c, tv.programmes[i])) }
                }
                ensureActive()
                if (chans.isNotEmpty()) { out.add(Row.Heading("Channels  ·  ${chans.size}")); chans.forEach { out.add(Row.Live(it, Repo.nowNext(it).first)) } }
                if (progs.isNotEmpty()) { out.add(Row.Heading("On TV today  ·  ${progs.size}")); out.addAll(progs) }
                for (k in listOf(Vod.MOVIES, Vod.SERIES)) {
                    val vi = SearchIndex.vod(k) ?: continue
                    val hits = ArrayList<VodItem>()
                    for (i in vi.names.indices) { if (hit(vi.names[i])) { hits.add(vi.items[i]); if (hits.size >= 60) break } }
                    ensureActive()
                    if (hits.isNotEmpty()) { out.add(Row.Heading((if (k == Vod.MOVIES) "Films" else "TV Shows") + "  ·  ${hits.size}")); hits.forEach { out.add(Row.Item(k, it)) } }
                }
                out
            }
            adapter.set(rows)
            val n = rows.count { it !is Row.Heading }
            status.text = if (n == 0) "Nothing found for “$text”." else "$n results"
            Ui.test("search \"$text\" $n")
        }
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun firstResult(): View? = (0 until results.childCount).map { results.getChildAt(it) }.firstOrNull { it.isFocusable }

    // ------------------------------------------------------------------ the keyboard and voice

    /** Voice search: the box takes the Fire TV keyboard (hold the remote's mic to speak, or type). */
    private fun startTyping() {
        query.isFocusable = true; query.isFocusableInTouchMode = true
        query.requestFocus()
        query.setSelection(query.text.length)
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).showSoftInput(query, InputMethodManager.SHOW_FORCED)
        Ui.test("search-voice")
    }

    private fun endTyping() {
        (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(query.windowToken, 0)
        query.isFocusable = false; query.isFocusableInTouchMode = false
        if (query.text.isNotBlank()) Store.addRecentSearch(query.text.toString())
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

    /** Before you type: what you searched for lately, as chips. */
    private fun showRecent() {
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

    private inner class ResultAdapter : RecyclerView.Adapter<RecyclerView.ViewHolder>() {
        var rows: List<Row> = emptyList()
        fun set(list: List<Row>) { rows = list; notifyDataSetChanged() }
        private val logoH by lazy { (50 * resources.displayMetrics.density).toInt() }

        inner class H(v: View) : RecyclerView.ViewHolder(v) {
            val image: ImageView = v.findViewById(R.id.image)
            val initials: TextView = v.findViewById(R.id.initials)
            val title: TextView = v.findViewById(R.id.title)
            val subtitle: TextView = v.findViewById(R.id.subtitle)
        }
        inner class Head(val v: TextView) : RecyclerView.ViewHolder(v)

        override fun getItemCount() = rows.size
        override fun getItemViewType(position: Int) = if (rows[position] is Row.Heading) 0 else 1

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): RecyclerView.ViewHolder {
            val inf = LayoutInflater.from(parent.context)
            if (viewType == 0) return Head(inf.inflate(R.layout.item_search_heading, parent, false) as TextView)
            val v = inf.inflate(R.layout.item_result, parent, false)
            val h = H(v)
            v.setOnClickListener {
                Store.addRecentSearch(query.text.toString())
                when (val r = rows.getOrNull(h.bindingAdapterPosition)) {
                    is Row.Live -> startActivity(Intent(this@SearchActivity, MainActivity::class.java)
                        .putExtra(MainActivity.PLAY, r.c.id).putExtra(MainActivity.PLAY_CATEGORY, Repo.ALL))
                    is Row.Item -> startActivity(Intent(this@SearchActivity, DetailActivity::class.java)
                        .putExtra(DetailActivity.KIND, r.kind).putExtra(DetailActivity.ID, r.m.id))
                    else -> {}
                }
            }
            return h
        }

        override fun onBindViewHolder(holder: RecyclerView.ViewHolder, position: Int) {
            when (val r = rows[position]) {
                is Row.Heading -> (holder as Head).v.text = r.text
                is Row.Live -> {
                    val h = holder as H
                    h.title.text = (if (Store.showNumbers && r.c.number > 0) "${r.c.number}   " else "") + r.c.name
                    val p = r.p
                    h.subtitle.text = when {
                        p == null -> ""
                        p.start <= System.currentTimeMillis() -> "Now: ${p.title}"
                        else -> "${Ui.time(p.start)}  ${p.title}"
                    }
                    h.initials.text = Ui.initials(r.c.name)
                    h.image.scaleType = ImageView.ScaleType.FIT_CENTER
                    Logos.load(h.image, r.c.logo, logoH, h.initials)
                }
                is Row.Item -> {
                    val h = holder as H
                    h.title.text = r.m.name
                    h.subtitle.text = listOfNotNull(if (r.kind == Vod.MOVIES) "Film" else "TV show", r.m.year.takeIf { it > 0 }?.toString()).joinToString("  ·  ")
                    h.initials.text = Ui.initials(r.m.name)
                    h.image.scaleType = ImageView.ScaleType.CENTER_CROP
                    Posters.load(h.image, r.m.poster, logoH, h.initials)
                }
            }
        }
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean = side.onKey(event) || super.dispatchKeyEvent(event)

}
