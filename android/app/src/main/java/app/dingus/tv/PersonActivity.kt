package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.view.View
import android.widget.ImageView
import android.widget.TextView
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** An actor: their photo and a little about them (TMDB), then the films and shows they're in that are in your playlist. */
class PersonActivity : BaseActivity() {
    companion object { const val ID = "id"; const val NAME = "name" }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_person)
        findViewById<ImageView>(R.id.photo).apply {
            clipToOutline = true
            outlineProvider = object : android.view.ViewOutlineProvider() {
                override fun getOutline(view: android.view.View, o: android.graphics.Outline) { o.setOval(0, 0, view.width, view.height) }
            }
        }
        val id = intent.getStringExtra(ID) ?: return finish()
        findViewById<TextView>(R.id.name).text = intent.getStringExtra(NAME) ?: ""
        val status = findViewById<TextView>(R.id.status)
        status.text = "Looking for what they're in…"
        val rows = findViewById<RecyclerView>(R.id.rows)
        val adapter = RowsAdapter(this, onFocus = {}, onClick = { c ->
            if (c is Card.Title) startActivity(Intent(this, DetailActivity::class.java).putExtra(DetailActivity.KIND, c.kind).putExtra(DetailActivity.ID, c.item.id))
        })
        rows.layoutManager = LinearLayoutManager(this)
        rows.adapter = adapter
        rows.itemAnimator = null

        scope.launch {
            val p = People.person(id)
            if (p == null) { status.text = "Couldn't get their details just now."; return@launch }
            findViewById<TextView>(R.id.name).text = p.name
            findViewById<TextView>(R.id.born).text = p.born
            findViewById<TextView>(R.id.bio).text = p.bio.ifEmpty { "No biography yet." }
            Posters.load(findViewById<ImageView>(R.id.photo), p.photo, Ui.dp(this@PersonActivity, 150))
            // (Usually already loaded; if not, both at once.)
            if (Vod.available) listOf(Vod.MOVIES, Vod.SERIES).filter { Vod.cached(it) == null }
                .map { k -> async { try { Vod.library(k) } catch (_: Exception) {} } }.forEach { it.await() }
            val (films, shows) = People.inPlaylist(p.works)
            val out = ArrayList<Row>()
            if (films.isNotEmpty()) out.add(Row("Films in your playlist", films.map { Card.Title(Vod.MOVIES, it) }))
            if (shows.isNotEmpty()) out.add(Row("TV shows in your playlist", shows.map { Card.Title(Vod.SERIES, it) }))
            adapter.set(out)
            status.text = if (out.isEmpty()) "Nothing else of theirs in your playlist." else ""
            status.visibility = if (out.isEmpty()) View.VISIBLE else View.GONE
            Ui.test("person ${p.name} films ${films.size} shows ${shows.size}")
            if (out.isNotEmpty()) rows.post { (rows.findViewHolderForAdapterPosition(0) as? RowsAdapter.H)?.list?.getChildAt(0)?.requestFocus() }
        }
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }
}
