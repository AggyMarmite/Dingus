package app.dingus.tv

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * TMDB's charts – the top rated films and shows of all time, and what's trending today – matched to the titles in
 * your playlist (only what you can actually watch is shown, in TMDB's order). Kept on the device: trending is looked
 * up again every few hours, the top lists once a day.
 * This product uses the TMDB API but is not endorsed or certified by TMDB.
 */
object Charts {
    const val TOP = "top"
    const val TRENDING = "trending"

    private lateinit var dir: File
    private class Saved(val works: List<People.Work>, val at: Long)
    private val saved = HashMap<String, Saved>()
    private val matched = HashMap<String, Pair<Vod.Library, List<VodItem>>>()

    fun init(ctx: Context) { dir = File(ctx.filesDir, "charts").apply { mkdirs() } }

    /** Needs TMDB (a key in the build, and title logos not switched off doesn't matter here). */
    val enabled: Boolean get() = BuildConfig.TMDB_KEY.isNotBlank()

    private fun name(list: String, kind: String) = "$list-$kind"
    private fun maxAge(list: String) = if (list == TRENDING) 4 * 3_600_000L else 24 * 3_600_000L

    private fun read(n: String): Saved? = synchronized(saved) {
        saved[n] ?: try {
            val o = JSONObject(File(dir, "$n.json").readText())
            val a = o.getJSONArray("w")
            Saved((0 until a.length()).map { a.getJSONObject(it) }.map { People.Work(it.getString("t"), it.optInt("y"), it.optBoolean("tv")) }, o.getLong("at"))
                .also { saved[n] = it }
        } catch (e: Exception) { null }
    }

    /** The chart's titles in your playlist, from what's kept (no network): empty until it's been looked up once. */
    fun cached(list: String, kind: String): List<VodItem> {
        val lib = Vod.cached(kind) ?: return emptyList()
        val n = name(list, kind)
        synchronized(matched) { matched[n]?.takeIf { it.first === lib }?.let { return it.second } }
        val s = read(n) ?: return emptyList()
        val items = People.matchInOrder(kind, s.works)
        synchronized(matched) { matched[n] = lib to items }
        return items
    }

    /** Whether the kept chart is old enough to look up again. */
    fun stale(list: String, kind: String): Boolean = read(name(list, kind))?.let { System.currentTimeMillis() - it.at > maxAge(list) } ?: true

    /** Looks the chart up on TMDB (if it's due) and returns what's in your playlist. */
    suspend fun load(list: String, kind: String): List<VodItem> = withContext(Dispatchers.IO) {
        if (enabled && stale(list, kind)) try { fetch(list, kind) } catch (e: Exception) { android.util.Log.w("Dingus", "chart $list $kind", e) }
        cached(list, kind)
    }

    private fun fetch(list: String, kind: String) {
        val type = if (kind == Vod.MOVIES) "movie" else "tv"
        val path = if (list == TRENDING) "/trending/$type/day" else "/$type/top_rated"
        val pages = if (list == TRENDING) 3 else 10 // (20 a page: plenty to find the ones your playlist has)
        val works = ArrayList<People.Work>()
        for (page in 1..pages) {
            val r = TitleLogos.get("$path?page=$page&language=en-GB").optJSONArray("results") ?: break
            for (i in 0 until r.length()) {
                val o = r.getJSONObject(i)
                val title = o.optString(if (type == "movie") "title" else "name")
                if (title.isBlank()) continue
                val date = o.optString(if (type == "movie") "release_date" else "first_air_date")
                works.add(People.Work(title, date.take(4).toIntOrNull() ?: 0, type == "tv"))
            }
        }
        if (works.isEmpty()) return
        val now = System.currentTimeMillis()
        val a = JSONArray(); works.forEach { a.put(JSONObject().put("t", it.title).put("y", it.year).put("tv", it.tv)) }
        val n = name(list, kind)
        File(dir, "$n.json").writeText(JSONObject().put("at", now).put("w", a).toString())
        synchronized(saved) { saved[n] = Saved(works, now) }
        synchronized(matched) { matched.remove(n) }
        Ui.test("chart $list $kind ${works.size}")
    }
}
