package app.dingus.tv

import android.content.Context
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Coming soon (Films and TV Shows, a category of its own): TMDB's upcoming films and new shows, grouped by month.
 * "Tell me when it's here" sets an alert; whenever the films or shows are read again (and once a day anyway), any
 * alert whose title has appeared in your provider's library – outside the categories you've said not to count
 * (CAM, pre-release…) – pops up a note, tags the title New for a week and clears itself.
 */
object Upcoming {
    class Item(val kind: String, val tmdbId: String, val title: String, val date: String, val plot: String?,
               val poster: String?, val backdrop: String?, val genre: String?) {
        val year: Int get() = date.take(4).toIntOrNull() ?: 0
        /** As a card in the rows (an id that can't clash with the provider's). */
        val vodItem: VodItem get() = VodItem("tmdb:$tmdbId", title, poster, Vod.COMING, 0f, year, "", 0L)
    }

    private lateinit var dir: File
    fun init(ctx: Context) { dir = ctx.filesDir }

    private val lists = HashMap<String, List<Item>>()

    // ------------------------------------------------------------------ the list (TMDB)

    /** The saved list (no network). */
    fun cached(kind: String): List<Item> = synchronized(lists) {
        lists[kind] ?: read(kind).also { lists[kind] = it }
    }

    /** Fetches it again when it's over 12 hours old. */
    suspend fun refresh(kind: String): List<Item> = withContext(Dispatchers.IO) {
        val f = File(dir, "upcoming_$kind.json")
        if (f.exists() && System.currentTimeMillis() - f.lastModified() < 12 * 3600_000L && cached(kind).isNotEmpty()) return@withContext cached(kind)
        try {
            val list = fetch(kind)
            if (list.isNotEmpty()) {
                f.writeText(JSONArray(list.map { it.toJson() }).toString())
                synchronized(lists) { lists[kind] = list }
            }
        } catch (e: Exception) { Log.w("Dingus", "coming soon", e) }
        cached(kind)
    }

    private fun fetch(kind: String): List<Item> {
        val today = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.UK).format(java.util.Date())
        val later = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.UK).format(java.util.Date(System.currentTimeMillis() + 120L * 86_400_000L))
        val genres = genreNames(kind)
        val out = ArrayList<Item>()
        for (page in 1..3) {
            val path = if (kind == Vod.MOVIES)
                "/discover/movie?region=GB&language=en-GB&with_release_type=2|3|4&release_date.gte=$today&release_date.lte=$later&sort_by=popularity.desc&include_adult=false&page=$page"
            else "/discover/tv?language=en-GB&first_air_date.gte=$today&first_air_date.lte=$later&sort_by=popularity.desc&include_adult=false&page=$page"
            val res = TitleLogos.get(path).optJSONArray("results") ?: break
            for (i in 0 until res.length()) {
                val o = res.getJSONObject(i)
                val date = o.optString(if (kind == Vod.MOVIES) "release_date" else "first_air_date")
                if (date.length < 10 || date < today) continue
                val g = o.optJSONArray("genre_ids")?.let { a -> (0 until minOf(2, a.length())).mapNotNull { genres[a.optInt(it)] }.joinToString(", ") }
                out.add(Item(kind, o.optLong("id").toString(), o.optString(if (kind == Vod.MOVIES) "title" else "name"), date,
                    o.optString("overview").ifBlank { null }, img(o.optString("poster_path"), "w342"), img(o.optString("backdrop_path"), "w1280"), g?.ifBlank { null }))
            }
        }
        return out.distinctBy { it.tmdbId }.sortedBy { it.date }
    }

    private fun img(p: String, size: String) = p.takeIf { it.startsWith("/") }?.let { "https://image.tmdb.org/t/p/$size$it" }

    private fun genreNames(kind: String): Map<Int, String> = try {
        val a = TitleLogos.get("/genre/${if (kind == Vod.MOVIES) "movie" else "tv"}/list?language=en-GB").optJSONArray("genres") ?: JSONArray()
        (0 until a.length()).associate { a.getJSONObject(it).let { g -> g.optInt("id") to g.optString("name") } }
    } catch (e: Exception) { emptyMap() }

    private fun Item.toJson() = JSONObject().put("k", kind).put("id", tmdbId).put("t", title).put("d", date)
        .put("p", plot ?: "").put("po", poster ?: "").put("b", backdrop ?: "").put("g", genre ?: "")

    private fun read(kind: String): List<Item> = try {
        val a = JSONArray(File(dir, "upcoming_$kind.json").readText())
        (0 until a.length()).map { a.getJSONObject(it) }.map { o ->
            Item(o.getString("k"), o.getString("id"), o.getString("t"), o.getString("d"), o.optString("p").ifBlank { null },
                o.optString("po").ifBlank { null }, o.optString("b").ifBlank { null }, o.optString("g").ifBlank { null })
        }.filter { it.date >= java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.UK).format(java.util.Date()) }
    } catch (e: Exception) { emptyList() }

    fun find(id: String): Item? = id.removePrefix("tmdb:").let { t -> (cached(Vod.MOVIES) + cached(Vod.SERIES)).firstOrNull { it.tmdbId == t } }

    /** Its details for the banner (so nothing's asked of the provider for a title it hasn't got). */
    fun details(it: Item) = Details(it.title, it.plot, null, null, it.genre, it.year, null, null, it.poster, it.backdrop, emptyList(), it.tmdbId)

    /** "12 Dec" */
    fun shortDate(it: Item): String = try {
        java.text.SimpleDateFormat("d MMM", java.util.Locale.UK).format(java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.UK).parse(it.date)!!)
    } catch (e: Exception) { it.date }

    /** "December" / "January 2027" for the rows. */
    fun month(it: Item): String = try {
        val d = java.text.SimpleDateFormat("yyyy-MM-dd", java.util.Locale.UK).parse(it.date)!!
        val thisYear = java.util.Calendar.getInstance().get(java.util.Calendar.YEAR).toString()
        java.text.SimpleDateFormat(if (it.date.startsWith(thisYear)) "MMMM" else "MMMM yyyy", java.util.Locale.UK).format(d)
    } catch (e: Exception) { "Later" }

    // ------------------------------------------------------------------ alerts

    private fun alertsJson(): JSONArray = try { JSONArray(Store.prefsString("comingAlerts", "[]")) } catch (e: Exception) { JSONArray() }

    fun alerts(): List<Item> { val a = alertsJson(); return (0 until a.length()).map { a.getJSONObject(it) }.map { o ->
        Item(o.getString("k"), o.getString("id"), o.getString("t"), o.optString("d"), null, o.optString("po").ifBlank { null }, null, null) } }

    fun hasAlert(id: String): Boolean = alerts().any { it.tmdbId == id.removePrefix("tmdb:") }

    fun setAlert(it: Item, on: Boolean) {
        val rest = alerts().filter { a -> a.tmdbId != it.tmdbId }
        val list = if (on) rest + it else rest
        Store.putString("comingAlerts", JSONArray(list.map { a -> JSONObject().put("k", a.kind).put("id", a.tmdbId).put("t", a.title).put("d", a.date).put("po", a.poster ?: "") }).toString())
        Ui.test("coming-alert ${it.title} $on")
    }

    // ------------------------------------------------------------------ categories that don't count

    private val notCountingRx = Regex("""\b(cam|camrip|ts|telesync|hdts|screener|scr|pre[- ]?release|pre[- ]?releases)\b""", RegexOption.IGNORE_CASE)

    /** The categories (ids, "movies:12") that don't count as having it – CAM, pre-release and the like at first. */
    fun ignored(kind: String, lib: Vod.Library?): Set<String> {
        val saved = Store.prefsString("comingIgnore_$kind", "\u0000")
        if (saved != "\u0000") return saved.split('\n').filter { it.isNotEmpty() }.toSet()
        return lib?.categories?.filter { notCountingRx.containsMatchIn(it.name) }?.map { it.id }?.toSet() ?: emptySet()
    }

    fun setIgnored(kind: String, ids: Set<String>) = Store.putString("comingIgnore_$kind", ids.joinToString("\n"))

    // ------------------------------------------------------------------ arrived

    private fun arrivedJson(): JSONObject = try { JSONObject(Store.prefsString("comingArrived", "{}")) } catch (e: Exception) { JSONObject() }

    /** Tagged New for a week after it arrives. */
    fun isArrived(id: String): Boolean = arrivedJson().optLong(id, 0L).let { it > 0 && System.currentTimeMillis() - it < 7 * 86_400_000L }

    private fun norm(s: String) = s.lowercase().replace(Regex("""\(\d{4}\)|\[.*?]|^[a-z]{2,4}\s*[|:-]\s*"""), "").filter { it.isLetterOrDigit() }

    /** Any alert now in the library (outside the categories that don't count)? Note it, tag it, clear the alert. */
    fun check(kind: String, lib: Vod.Library) {
        val mine = alerts().filter { it.kind == kind }
        if (mine.isEmpty()) return
        val skip = ignored(kind, lib)
        val byName = HashMap<String, MutableList<VodItem>>()
        for (m in lib.items) if (m.categoryId !in skip) byName.getOrPut(norm(m.name)) { ArrayList(1) }.add(m)
        val arrived = arrivedJson()
        for (a in mine) {
            val hit = byName[norm(a.title)]?.firstOrNull { m -> m.year <= 0 || a.year <= 0 || kotlin.math.abs(m.year - a.year) <= 1 } ?: continue
            setAlert(a, false)
            arrived.put(hit.id, System.currentTimeMillis())
            Ui.test("coming-arrived ${a.title}")
            Notice.done("🔔  ${a.title} is now in your ${if (kind == Vod.MOVIES) "Films" else "TV Shows"}", 8_000)
        }
        Store.putString("comingArrived", arrived.toString())
    }
}
