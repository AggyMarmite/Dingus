package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject

/** Who's in a film or show, and each person's page (from TMDB), matched against what's in your playlist. */
object People {
    class Actor(val id: String, val name: String, val role: String, val photo: String?)
    class Person(val name: String, val bio: String, val born: String, val photo: String?, val works: List<Work>)
    class Work(val title: String, val year: Int, val tv: Boolean)

    private const val IMG = "https://image.tmdb.org/t/p/w185"
    private val castCache = HashMap<String, List<Actor>>()

    /** The main cast (up to 15), best known first. */
    suspend fun cast(kind: String, item: VodItem, tmdbId: String?): List<Actor> = withContext(Dispatchers.IO) {
        if (!TitleLogos.enabled) return@withContext emptyList()
        val key = "$kind:${item.id}"
        synchronized(castCache) { castCache[key] }?.let { return@withContext it }
        val list = try {
            val id = TitleLogos.tmdbIdFor(kind, item, tmdbId) ?: return@withContext emptyList()
            val o = if (kind == Vod.MOVIES) TitleLogos.get("/movie/$id/credits") else TitleLogos.get("/tv/$id/aggregate_credits")
            val a = o.optJSONArray("cast") ?: return@withContext emptyList()
            (0 until minOf(a.length(), 15)).map { i ->
                val c = a.getJSONObject(i)
                val role = c.optString("character").ifEmpty { c.optJSONArray("roles")?.optJSONObject(0)?.optString("character") ?: "" }
                Actor(c.optString("id"), c.optString("name"), role, c.optString("profile_path").takeIf { it.startsWith("/") }?.let { IMG + it })
            }
        } catch (e: Exception) { return@withContext emptyList() }
        synchronized(castCache) { castCache[key] = list }
        Ui.test("cast ${item.name} ${list.size}")
        list
    }

    suspend fun person(id: String): Person? = withContext(Dispatchers.IO) {
        try {
            val p = TitleLogos.get("/person/$id")
            val credits = TitleLogos.get("/person/$id/combined_credits").optJSONArray("cast")
            val works = ArrayList<Work>()
            if (credits != null) for (i in 0 until credits.length()) {
                val c = credits.getJSONObject(i)
                val tv = c.optString("media_type") == "tv"
                val t = c.optString(if (tv) "name" else "title")
                if (t.isEmpty()) continue
                val date = c.optString(if (tv) "first_air_date" else "release_date")
                works.add(Work(t, date.take(4).toIntOrNull() ?: 0, tv))
            }
            val born = listOfNotNull(p.optString("birthday").takeIf { it.length >= 4 }?.let { "Born ${it.take(4)}" },
                p.optString("place_of_birth").ifEmpty { null }).joinToString("  ·  ")
            Person(p.optString("name"), p.optString("biography"), born, p.optString("profile_path").takeIf { it.startsWith("/") }?.let { "https://image.tmdb.org/t/p/w342$it" }, works)
        } catch (e: Exception) { null }
    }

    private fun key(s: String) = SearchIndex.norm(s).replace(Regex("[^a-z0-9]+"), "")

    /** What of [works] is in the playlist: films and shows, matched by name (and year, when both are known). */
    suspend fun inPlaylist(works: List<Work>): Pair<List<VodItem>, List<VodItem>> = withContext(Dispatchers.Default) {
        fun match(kind: String, tv: Boolean): List<VodItem> {
            val lib = Vod.cached(kind) ?: return emptyList()
            val wanted = works.filter { it.tv == tv }.groupBy { key(it.title) }
            if (wanted.isEmpty()) return emptyList()
            val out = ArrayList<VodItem>(); val seen = HashSet<String>()
            for (m in lib.items) {
                val k = key(Vod.clean(m.name))
                val ws = wanted[k] ?: continue
                if (m.year > 0 && ws.none { it.year == 0 || kotlin.math.abs(it.year - m.year) <= 1 }) continue
                if (seen.add(k)) out.add(m)
            }
            return out.sortedByDescending { it.year }
        }
        match(Vod.MOVIES, false) to match(Vod.SERIES, true)
    }
}
