package app.dingus.tv

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

/** Who's in a film or show, and each person's page (from TMDB), matched against what's in your playlist. */
object People {
    class Actor(val id: String, val name: String, val role: String, val photo: String?)
    class Person(val name: String, val bio: String, val born: String, val photo: String?, val works: List<Work>)
    /** Something someone's been in. [also]: other names it goes by (its original title); [tmdb]: its TMDB number. */
    class Work(val title: String, val year: Int, val tv: Boolean, val also: List<String> = emptyList(), val tmdb: String = "")

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
            val cast = (0 until minOf(a.length(), 15)).map { i ->
                val c = a.getJSONObject(i)
                val role = c.optString("character").ifEmpty { c.optJSONArray("roles")?.optJSONObject(0)?.optString("character") ?: "" }
                Actor(c.optString("id"), c.optString("name"), role, c.optString("profile_path").takeIf { it.startsWith("/") }?.let { IMG + it })
            }
            // The director (or a show's creator) first, then the cast.
            val crew = o.optJSONArray("crew")
            val director = crew?.let { cr -> (0 until cr.length()).map { cr.getJSONObject(it) }.firstOrNull { c ->
                c.optString("job") == "Director" || c.optJSONArray("jobs")?.let { j -> (0 until j.length()).any { j.getJSONObject(it).optString("job") == "Director" } } == true
            } }
            listOfNotNull(director?.let { c -> Actor(c.optString("id"), c.optString("name"), "Director", c.optString("profile_path").takeIf { it.startsWith("/") }?.let { IMG + it }) }) + cast
        } catch (e: Exception) { return@withContext emptyList() }
        synchronized(castCache) { castCache[key] = list }
        Ui.test("cast ${item.name} ${list.size}")
        list
    }

    private val io = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val people = HashMap<String, Deferred<Person?>>()

    /** Start getting someone's page (e.g. when they're highlighted), so opening it is quick. */
    fun prefetch(id: String) { synchronized(people) { people.getOrPut(id) { io.async { fetch(id) } } } }

    /** Someone's page: from what's already been fetched (or is being), else fetched now. */
    suspend fun person(id: String): Person? {
        prefetch(id)
        val d = synchronized(people) { people[id]!! }
        val p = d.await()
        if (p == null) synchronized(people) { if (people[id] === d) people.remove(id) } // (try again next time)
        return p
    }

    private val crewJobs = setOf("Director", "Screenplay", "Writer", "Creator", "Executive Producer", "Novel")

    private fun fetch(id: String): Person? = try {
        // One request: the person and everything they've been in.
        val p = TitleLogos.get("/person/$id?append_to_response=combined_credits")
        // Everything they've been in: acting first, then anything they directed, wrote or created.
        val cc = p.optJSONObject("combined_credits")
        val works = ArrayList<Work>(); val seen = HashSet<String>()
        for (part in listOf("cast", "crew")) {
            val credits = cc?.optJSONArray(part) ?: continue
            for (i in 0 until credits.length()) {
                val c = credits.getJSONObject(i)
                val tv = c.optString("media_type") == "tv"
                if (part == "crew" && c.optString("job") !in crewJobs) continue
                val t = c.optString(if (tv) "name" else "title")
                if (t.isEmpty()) continue
                val id = c.optString("id")
                if (!seen.add("$tv:$id:$t")) continue
                val date = c.optString(if (tv) "first_air_date" else "release_date")
                val orig = c.optString(if (tv) "original_name" else "original_title").takeIf { it.isNotEmpty() && it != t }
                works.add(Work(t, date.take(4).toIntOrNull() ?: 0, tv, listOfNotNull(orig), id))
            }
        }
        val born = listOfNotNull(p.optString("birthday").takeIf { it.length >= 4 }?.let { "Born ${it.take(4)}" },
            p.optString("place_of_birth").ifEmpty { null }).joinToString("  ·  ")
        Person(p.optString("name"), p.optString("biography"), born, p.optString("profile_path").takeIf { it.startsWith("/") }?.let { "https://image.tmdb.org/t/p/w342$it" }, works)
    } catch (e: Exception) { null }

    // ------------------------------------------------------------------ matching against the playlist

    /** Each library's titles by their matching keys and by TMDB number, built once per library (so matching someone is instant). */
    private class Index(val lib: Vod.Library, val byKey: Map<String, List<VodItem>>, val byTmdb: Map<String, VodItem>) {
        fun isEmpty() = byKey.isEmpty()
    }
    private val indexes = HashMap<String, Index>()
    private val none = Index(Vod.Library(emptyList(), emptyList(), 0L), emptyMap(), emptyMap())

    private fun index(kind: String): Index {
        val lib = Vod.cached(kind) ?: return none
        synchronized(indexes) { indexes[kind]?.takeIf { it.lib === lib }?.let { return it } }
        val m = HashMap<String, ArrayList<VodItem>>(lib.items.size * 2)
        val t = HashMap<String, VodItem>()
        for (v in lib.items) {
            for (k in keys(Vod.clean(v.name), v.year)) m.getOrPut(k) { ArrayList(1) }.let { if (v !in it) it.add(v) }
            if (v.tmdb.isNotEmpty()) t.putIfAbsent(v.tmdb, v)
        }
        val ix = Index(lib, m, t)
        synchronized(indexes) { indexes[kind] = ix }
        return ix
    }

    /** Build the matching indexes ahead of time (a film's page does this when it shows the cast). */
    fun warm() { io.launch(Dispatchers.Default) { index(Vod.MOVIES); index(Vod.SERIES) } }

    private val notAlnum = Regex("[^a-z0-9]+")
    private val trailingYear = Regex("""\s*([\-–:|(\[]\s*)?((?:19|20)\d\d)[)\]]?\s*$""")
    private val tags = Regex("""\s*[\[(][^\])]*[\])]\s*$""")
    private fun key(s: String) = SearchIndex.norm(s).replace("&", " and ").replace(notAlnum, "")

    /**
     * The ways a title can be written: as is, without a bracketed tag or its year at the end, and without "The" at
     * the start. (A bare number at the end only counts as the year when it is the year: "Blade Runner 2049" stays.)
     */
    private fun keys(title: String, year: Int = 0): Set<String> {
        val out = LinkedHashSet<String>()
        var t = title.trim()
        repeat(3) {
            out.add(key(t))
            val y = trailingYear.find(t)
            val noYear = if (y != null && (y.groupValues[1].isNotEmpty() || (year > 0 && kotlin.math.abs(y.groupValues[2].toInt() - year) <= 1)))
                t.substring(0, y.range.first) else t
            val n = tags.replace(noYear, "").trim()
            if (n.isEmpty() || n == t) return@repeat
            t = n
        }
        for (k in out.toList()) if (k.startsWith("the") && k.length > 6) out.add(k.removePrefix("the"))
        out.remove("")
        return out
    }

    /**
     * The playlist's copy of [w]: by TMDB number when the provider gives one, else by name (and its other names).
     * The year settles between titles with the same name; a provider's year can be a little off, and a show's is
     * often its latest season's, so a near year (or, for a show, any year) is still accepted for a distinctive name.
     */
    private fun find(ix: Index, w: Work): VodItem? {
        if (w.tmdb.isNotEmpty()) ix.byTmdb[w.tmdb]?.let { return it }
        val cands = LinkedHashSet<VodItem>()
        for (name in listOf(w.title) + w.also) for (k in keys(name, w.year)) ix.byKey[k]?.let { cands.addAll(it) }
        if (cands.isEmpty()) return null
        val fits = cands.toList()
        if (w.year == 0) return fits.first()
        fits.firstOrNull { it.year <= 0 || kotlin.math.abs(w.year - it.year) <= 1 }?.let { return it }
        val distinctive = key(w.title).length >= 6
        if (!distinctive) return null
        val near = fits.minByOrNull { kotlin.math.abs(w.year - it.year) }!!
        return near.takeIf { w.tv || kotlin.math.abs(w.year - it.year) <= 3 }
    }

    /** What of [works] is in the playlist, in the same order (a chart). */
    fun matchInOrder(kind: String, works: List<Work>): List<VodItem> {
        val ix = index(kind)
        if (ix.isEmpty()) return emptyList()
        val out = ArrayList<VodItem>(); val seen = HashSet<String>()
        for (w in works) { val m = find(ix, w) ?: continue; if (seen.add(m.id)) out.add(m) }
        return out
    }

    /** What of [works] is in the playlist: their films and their shows, newest first. */
    suspend fun inPlaylist(works: List<Work>): Pair<List<VodItem>, List<VodItem>> = withContext(Dispatchers.Default) {
        fun match(kind: String, tv: Boolean): List<VodItem> {
            val ix = index(kind)
            if (ix.isEmpty()) return emptyList()
            val out = ArrayList<VodItem>(); val seen = HashSet<String>()
            for (w in works) if (w.tv == tv) { val m = find(ix, w) ?: continue; if (seen.add(m.id)) out.add(m) }
            return out.sortedByDescending { it.year }
        }
        match(Vod.MOVIES, false) to match(Vod.SERIES, true)
    }
}
