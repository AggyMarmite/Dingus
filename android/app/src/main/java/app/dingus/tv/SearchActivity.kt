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
    private sealed class Row {
        class Heading(val text: String) : Row()
        class Live(val c: Channel, val p: Programme?) : Row()
        class Item(val kind: String, val m: VodItem) : Row()
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var query: EditText
    private lateinit var status: TextView
    private lateinit var results: RecyclerView
    private val adapter = ResultAdapter()
    private var job: Job? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_search)
        query = findViewById(R.id.query)
        status = findViewById(R.id.status)
        results = findViewById(R.id.results)
        results.layoutManager = LinearLayoutManager(this)
        results.adapter = adapter
        results.itemAnimator = null

        query.addTextChangedListener(object : TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun onTextChanged(s: CharSequence?, a: Int, b: Int, c: Int) {}
            override fun afterTextChanged(s: Editable?) { search(s?.toString() ?: "", 350) }
        })
        query.setOnEditorActionListener { _, action, _ ->
            if (action == EditorInfo.IME_ACTION_SEARCH || action == EditorInfo.IME_ACTION_DONE) {
                (getSystemService(INPUT_METHOD_SERVICE) as InputMethodManager).hideSoftInputFromWindow(query.windowToken, 0)
                search(query.text.toString(), 0)
                results.post { results.getChildAt(1)?.requestFocus() ?: results.getChildAt(0)?.requestFocus() }
                true
            } else false
        }
        query.setOnKeyListener { _, code, ev ->
            if (code == KeyEvent.KEYCODE_DPAD_DOWN && ev.action == KeyEvent.ACTION_DOWN && adapter.itemCount > 0) {
                results.post { (results.getChildAt(1) ?: results.getChildAt(0))?.requestFocus() }; true
            } else false
        }
        status.text = "Type to search."
        // Films and shows are loaded in the background so they can be searched too.
        if (Vod.available) scope.launch {
            for (k in listOf(Vod.MOVIES, Vod.SERIES)) try { Vod.library(k) } catch (_: Exception) {}
            if (query.text.isNotEmpty()) search(query.text.toString(), 0)
        }
        query.requestFocus()
        if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_query")?.let { query.setText(it) }
    }

    private fun norm(s: String) = Normalizer.normalize(s.lowercase(), Normalizer.Form.NFD).replace(Regex("\\p{Mn}+"), "")

    private fun search(text: String, wait: Long) {
        job?.cancel()
        val words = norm(text).split(' ').filter { it.isNotBlank() }
        if (words.isEmpty() || words.sumOf { it.length } < 2) { adapter.set(emptyList()); status.text = "Type to search."; return }
        job = scope.launch {
            if (wait > 0) delay(wait)
            val rows = withContext(Dispatchers.Default) {
                fun hit(name: String) = norm(name).let { n -> words.all { n.contains(it) } }
                val out = ArrayList<Row>()
                val chans = Repo.channels.asSequence().filter { hit(it.name) }.take(40).toList()
                ensureActive()
                // What's on: programmes on now or later today whose title matches.
                val now = System.currentTimeMillis()
                val progs = ArrayList<Row.Live>()
                for (c in Repo.channels) {
                    if (progs.size >= 40) break
                    for (p in Repo.programmes(c)) {
                        if (p.stop > now && p.start < now + 12 * 3600_000L && hit(p.title)) { progs.add(Row.Live(c, p)); break }
                    }
                }
                ensureActive()
                if (chans.isNotEmpty()) { out.add(Row.Heading("CHANNELS")); chans.forEach { out.add(Row.Live(it, Repo.nowNext(it).first)) } }
                if (progs.isNotEmpty()) { out.add(Row.Heading("ON TV")); out.addAll(progs) }
                for (k in listOf(Vod.MOVIES, Vod.SERIES)) {
                    val lib = Vod.cached(k) ?: continue
                    val hits = lib.items.asSequence().filter { hit(it.name) }.take(60).toList()
                    ensureActive()
                    if (hits.isNotEmpty()) { out.add(Row.Heading(if (k == Vod.MOVIES) "FILMS" else "TV SHOWS")); hits.forEach { out.add(Row.Item(k, it)) } }
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
            if (viewType == 0) return Head(inf.inflate(R.layout.item_heading, parent, false) as TextView)
            val v = inf.inflate(R.layout.item_result, parent, false)
            val h = H(v)
            v.setOnClickListener {
                when (val r = rows.getOrNull(h.bindingAdapterPosition)) {
                    is Row.Live -> startActivity(Intent(this@SearchActivity, PlayerActivity::class.java)
                        .putExtra(PlayerActivity.CHANNEL, r.c.id).putExtra(PlayerActivity.CATEGORY, Repo.ALL))
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
                    h.title.text = (if (r.c.number > 0) "${r.c.number}   " else "") + r.c.name
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
}
