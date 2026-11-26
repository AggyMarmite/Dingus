package app.dingus.tv

import android.content.Context
import android.content.SharedPreferences
import android.util.JsonReader
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStreamReader

/** A film or a TV show in the provider's library. */
class VodItem(
    val id: String, val name: String, val poster: String?, val categoryId: String,
    val rating: Float, val year: Int, val ext: String, val added: Long,
    /** Its number on TMDB, when the provider's list gives it (used to match someone's films on their page). */
    val tmdb: String = "",
) {
    val key: String get() = id
}

class Episode(
    val id: String, val title: String, val season: Int, val number: Int, val ext: String,
    val plot: String?, val image: String?, val duration: String?,
)

class Details(
    val title: String?, val plot: String?, val cast: String?, val director: String?, val genre: String?,
    val year: Int, val duration: String?, val rating: String?, val poster: String?, val backdrop: String?,
    val episodes: List<Episode>,
    /** The film's or show's number on TMDB, when the provider gives it (used for title logos). */
    val tmdbId: String? = null,
)

/** Films and TV shows from an Xtream Codes login: loaded when first opened, kept on the device, refreshed twice a day. */
object Vod {
    const val MOVIES = "movies"
    const val SERIES = "series"
    const val CONTINUE = "\u0001continue"
    const val RECENT = "\u0001recent"
    const val ALL = "\u0001all"
    const val TOP = "\u0001top"
    const val TRENDING = "\u0001trending"
    /** Films/TV Shows: the rows view (not a category of its own). */
    const val FEATURED = "\u0001featured"
    /** Coming soon (TMDB's upcoming titles, with alerts): a category of its own. */
    const val COMING = "\u0001coming"

    class Library(val categories: List<Category>, val items: List<VodItem>, val loadedAt: Long) {
        val byCategory: Map<String, List<VodItem>> by lazy { items.groupBy { it.categoryId } }
        val byId: Map<String, VodItem> by lazy { items.associateBy { it.id } }
        val newest: List<VodItem> by lazy { items.sortedByDescending { it.added }.take(300) }
    }

    @Volatile private var libs = HashMap<String, Library>()
    private val locks = mapOf(MOVIES to Mutex(), SERIES to Mutex())
    private lateinit var app: Context
    /** Each playlist keeps its own saved copies and watch progress. */
    private val dir: File get() = File(app.filesDir, "p_${Store.activeId}").apply { mkdirs() }
    private var progressFor = ""
    private var progressPrefs: SharedPreferences? = null
    private val progress: SharedPreferences get() {
        val id = Store.activeId
        if (id != progressFor || progressPrefs == null) { progressPrefs = app.getSharedPreferences("progress_$id", Context.MODE_PRIVATE); progressFor = id }
        return progressPrefs!!
    }

    fun init(ctx: Context) { app = ctx.applicationContext }

    /** After switching playlist: forget what's in memory (saved copies stay). */
    fun reset() { libs = HashMap() }

    /**
     * An update: films and shows download alongside the channels and TV guide (not one after the other), each saved
     * as it arrives. Only when they've been loaded before (otherwise they load when Films or TV Shows first opens).
     */
    fun refreshInBackground() {
        if (!available) return
        for (k in listOf(MOVIES, SERIES)) if (libs.containsKey(k) || java.io.File(dir, "vod_$k.bin").exists())
            Background.scope.launch(Dispatchers.IO) { try { library(k, fresh = true) } catch (e: Exception) { Log.w("Dingus", "update $k", e) } }
    }

    /** Films and TV shows need an Xtream Codes login (M3U links only carry live channels here). */
    val available: Boolean get() = Store.playlist?.type == "xtream"

    fun cached(kind: String): Library? = libs[kind]

    /** The saved copy only (never the network): quick, for showing something straight away. */
    suspend fun loadSaved(kind: String): Library? = withContext(Dispatchers.IO) {
        // (No lock: the saved file is swapped in whole, so it can be read while an update is downloading.)
        val lib = libs[kind] ?: try { read(kind)?.also { r -> if (libs[kind] == null) libs = HashMap(libs).apply { put(kind, r) } } } catch (e: Exception) { null }
        lib?.also { warm(kind, it) }
    }

    /**
     * Everything Films / TV Shows works out from a library – grouping, sorting, and finding TMDB's Trending and Top
     * titles in it – done here in the background, so opening the screen after an update doesn't freeze while it's done.
     */
    private val warmed = java.util.Collections.newSetFromMap(java.util.WeakHashMap<Library, Boolean>())
    private fun warm(kind: String, lib: Library) {
        // Once per library (it was being done again every time a screen asked for the films – e.g. opening a film).
        if (!synchronized(warmed) { warmed.add(lib) }) return
        lib.byCategory; lib.byId; lib.newest
        try { Upcoming.check(kind, lib) } catch (e: Exception) { Log.w("Dingus", "coming soon check", e) }
        if (libs[kind] === lib) { Charts.cached(Charts.TRENDING, kind); Charts.cached(Charts.TOP, kind); topRated(lib, kind) }
    }

    /** The library, from memory, the saved copy, or the provider (in that order). [fresh] forces the provider. */
    suspend fun library(kind: String, fresh: Boolean = false): Library = withContext(Dispatchers.IO) {
        val lib = load(kind, fresh)
        // Sorting and grouping a big library happens here, off screen, not when a row is first drawn.
        warm(kind, lib)
        lib
    }

    private suspend fun load(kind: String, fresh: Boolean): Library {
        // Already in memory: straight back, without waiting behind an update that's downloading (it holds the lock).
        if (!fresh) {
            libs[kind]?.let { return it }
            try { read(kind)?.let { r -> libs[kind]?.let { return it }; libs = HashMap(libs).apply { put(kind, r) }; return r } }
            catch (e: Exception) { Log.w("Dingus", "saved $kind", e) }
        }
        return locks.getValue(kind).withLock {
            // Another screen asked for a fresh copy a moment ago: that one will do (no second download).
            if (fresh) libs[kind]?.takeIf { System.currentTimeMillis() - it.loadedAt < 120_000L }?.let { return@withLock it }
            if (!fresh) {
                libs[kind]?.let { return@withLock it }
                try { read(kind)?.let { libs = HashMap(libs).apply { put(kind, it) }; return@withLock it } }
                catch (e: Exception) { Log.w("Dingus", "saved $kind", e) }
            }
            val p = Store.playlist ?: throw IllegalStateException("No playlist")
            val lib = Progress.step(if (kind == MOVIES) Progress.FILMS else Progress.SHOWS) { fetch(p, kind) }
            libs = HashMap(libs).apply { put(kind, lib) }
            try { Progress.saving { write(kind, lib) } } catch (e: Exception) { Log.w("Dingus", "save $kind", e) }
            lib
        }
    }

    fun clear() {
        libs = HashMap()
        File(dir, "vod_$MOVIES.bin").delete()
        File(dir, "vod_$SERIES.bin").delete()
    }

    fun categoriesFor(kind: String, lib: Library): List<Category> {
        val out = ArrayList<Category>()
        out.add(Category(FEATURED, "Featured"))
        if (TitleLogos.enabled) out.add(Category(COMING, "Coming soon"))
        continueList(kind, lib).size.let { if (it > 0) out.add(Category(CONTINUE, "Continue watching", it)) }
        out.add(Category(RECENT, "Recently added"))
        trending(kind).size.let { if (it > 0) out.add(Category(TRENDING, "Trending", it)) }
        topRated(lib, kind).size.let { if (it > 0) out.add(Category(TOP, topName(kind), it)) }
        out.add(Category(ALL, if (kind == MOVIES) "All films" else "All shows", lib.items.size))
        for (c in Store.ordered(kind, lib.categories.filter { lib.byCategory[it.id]?.isNotEmpty() == true && !Store.isHidden("$kind:${it.id}") }) { it.id })
            out.add(Category(c.id, c.name, lib.byCategory[c.id]?.size ?: 0))
        return out
    }

    private var topFor: Library? = null
    private var top: List<VodItem> = emptyList()
    /** TMDB's top rated that are in your playlist (Charts); the provider's own ratings until that's been looked up. */
    fun topRated(lib: Library, kind: String): List<VodItem> {
        Charts.cached(Charts.TOP, kind).let { if (it.size >= 5) return it }
        if (topFor !== lib) { top = lib.items.filter { it.rating > 0 }.sortedByDescending { it.rating }.take(200); topFor = lib }
        return top
    }
    fun topName(kind: String) = if (Charts.cached(Charts.TOP, kind).size >= 5) (if (kind == MOVIES) "Top films" else "Top TV shows") else "Top rated"

    /** What's trending on TMDB today that's in your playlist (Charts). */
    fun trending(kind: String): List<VodItem> = Charts.cached(Charts.TRENDING, kind)

    fun itemsIn(kind: String, lib: Library, categoryId: String): List<VodItem> = when (categoryId) {
        CONTINUE -> continueList(kind, lib)
        RECENT -> lib.newest
        ALL -> lib.items
        TOP -> topRated(lib, kind)
        TRENDING -> trending(kind)
        else -> lib.byCategory[categoryId] ?: emptyList()
    }

    // ------------------------------------------------------------------ addresses

    private fun base(p: Playlist) = Failover.base(p)
    fun movieUrl(m: VodItem): String { val p = Store.playlist!!; return "${base(p)}/movie/${Sources.enc(p.user)}/${Sources.enc(p.pass)}/${m.id}.${m.ext}" }
    fun episodeUrl(e: Episode): String { val p = Store.playlist!!; return "${base(p)}/series/${Sources.enc(p.user)}/${Sources.enc(p.pass)}/${e.id}.${e.ext}" }

    // ------------------------------------------------------------------ watch progress ("continue watching")

    /** Where you got to in a film or episode: position and length in ms, or null. */
    fun position(id: String): Pair<Long, Long>? {
        val s = progress.getString("p:$id", null) ?: return null
        val parts = s.split('|')
        return (parts.getOrNull(0)?.toLongOrNull() ?: return null) to (parts.getOrNull(1)?.toLongOrNull() ?: 0L)
    }

    /** Remember the position; near the end counts as watched. [showId] links an episode to its show. */
    fun savePosition(id: String, pos: Long, length: Long, kind: String, showId: String? = null, label: String? = null) {
        val e = progress.edit()
        val finished = length > 0 && (pos >= length * 0.95 || length >= 20 * 60_000L && length - pos < 120_000)
        if (finished || pos < 30_000) e.remove("p:$id") else e.putString("p:$id", "$pos|$length")
        if (finished && length > 0) e.putBoolean("w:$id", true)
        val owner = showId ?: id
        val list = (progress.getString("c:$kind", "") ?: "").split('\n').filter { it.isNotEmpty() && it != owner }
        val keep = kind == SERIES || !finished && pos >= 30_000
        e.putString("c:$kind", ((if (keep) listOf(owner) else emptyList()) + list).take(40).joinToString("\n"))
        if (showId != null) { e.putString("last:$showId", id); if (label != null) e.putString("lastLabel:$showId", label) }
        // Watched past halfway: Home's "Because you watched …" (the last two titles).
        if (length > 0 && pos >= length / 2) {
            val k = "$kind:$owner"
            e.putString("seen", (listOf(k) + (progress.getString("seen", "") ?: "").split('\n').filter { it.isNotEmpty() && it != k }).take(5).joinToString("\n"))
        }
        e.apply()
    }

    /** The titles watched past halfway, newest first, as (kind, id). */
    fun recentlySeen(): List<Pair<String, String>> = (progress.getString("seen", "") ?: "").split('\n').filter { it.contains(':') }
        .map { it.substringBefore(':') to it.substringAfter(':') }

    // ------------------------------------------------------------------ new episodes (shows in My list or being watched)

    /** Shows with a new episode since last checked: show id → "S2 E5". */
    fun newEpisodes(): Map<String, String> = (progress.getString("newEps", "") ?: "").split('\n').filter { '|' in it }
        .associate { it.substringBefore('|') to it.substringAfter('|') }

    /** The show's page was opened: it's not "new" any more, and this is how many episodes it has now. */
    fun clearNewEpisode(showId: String, episodes: Int) {
        val m = newEpisodes() - showId
        progress.edit().putString("newEps", m.entries.joinToString("\n") { "${it.key}|${it.value}" }).putInt("eps:$showId", episodes).apply()
    }

    /**
     * Looks at the shows in My list and Continue watching (at most every 6 hours, 20 shows): one with more episodes
     * than last time is marked "new episode". The first look at a show only remembers how many it has.
     */
    suspend fun checkNewEpisodes(): Boolean = withContext(Dispatchers.IO) {
        if (!available) return@withContext false
        val now = System.currentTimeMillis()
        if (now - progress.getLong("newEpsAt", 0) < 6 * 3_600_000L) return@withContext false
        val lib = try { library(SERIES) } catch (e: Exception) { return@withContext false }
        val shows = (continueList(SERIES, lib) + myList(mapOf(SERIES to lib)).map { it.second }).distinctBy { it.id }.take(20)
        val found = HashMap(newEpisodes())
        val e = progress.edit()
        for (show in shows) {
            val d = try { details(SERIES, show) } catch (_: Exception) { continue }
            val count = d.episodes.size
            val before = progress.getInt("eps:${show.id}", -1)
            if (before in 0 until count) d.episodes.maxWithOrNull(compareBy({ it.season }, { it.number }))?.let { found[show.id] = "S${it.season} E${it.number}" }
            e.putInt("eps:${show.id}", count)
        }
        e.putString("newEps", found.entries.joinToString("\n") { "${it.key}|${it.value}" }).putLong("newEpsAt", now).apply()
        Ui.test("new-episodes ${found.size}")
        found.isNotEmpty()
    }

    fun watched(id: String) = progress.getBoolean("w:$id", false)

    /** Mark a film or episode as watched or not, by hand. */
    fun setWatched(id: String, on: Boolean, kind: String, showId: String? = null) {
        val e = progress.edit().remove("p:$id")
        if (on) e.putBoolean("w:$id", true) else e.remove("w:$id")
        if (kind == MOVIES && on) {
            val list = (progress.getString("c:$kind", "") ?: "").split('\n').filter { it.isNotEmpty() && it != id }
            e.putString("c:$kind", list.joinToString("\n"))
        }
        if (showId != null && on) e.putString("last:$showId", id)
        e.apply()
    }
    fun lastEpisode(showId: String): String? = progress.getString("last:$showId", null)

    /** The episode you're on in a show, e.g. "S02 E01" (for Continue watching). */
    fun lastEpisodeLabel(showId: String): String? = progress.getString("lastLabel:$showId", null)

    /** Continue watching's line under a show: "S02 E01 · 18 min left" (or just the episode). */
    fun continueLine(showId: String): String? {
        val l = lastEpisodeLabel(showId) ?: return null
        val left = lastEpisode(showId)?.let { position(it) }?.let { (p, len) -> ((len - p) / 60_000).takeIf { it > 0 } }
        return if (left != null) "$l  ·  $left min left" else l
    }

    // ------------------------------------------------------------------ My list

    /** Films and shows saved by hand, newest first, as "movies:12" / "series:34" lines. */
    private fun myListKeys(): List<String> = (progress.getString("mylist", "") ?: "").split('\n').filter { it.isNotEmpty() }

    fun inMyList(kind: String, id: String): Boolean = myListKeys().contains("$kind:$id")

    fun setInMyList(kind: String, id: String, on: Boolean) {
        val key = "$kind:$id"
        val list = myListKeys().filter { it != key }
        progress.edit().putString("mylist", (if (on) listOf(key) + list else list).take(300).joinToString("\n")).apply()
    }

    /** My list items that are in the library, in the order they were added. */
    fun myList(lib: Map<String, Library>): List<Pair<String, VodItem>> = myListKeys().mapNotNull { k ->
        val kind = k.substringBefore(':'); val id = k.substringAfter(':')
        lib[kind]?.byId?.get(id)?.let { kind to it }
    }

    fun continueList(kind: String, lib: Library): List<VodItem> =
        (progress.getString("c:$kind", "") ?: "").split('\n').filter { it.isNotEmpty() }.mapNotNull { lib.byId[it] }

    // ------------------------------------------------------------------ loading from the provider

    private fun fetch(p: Playlist, kind: String): Library {
        val auth = "username=${Sources.enc(p.user)}&password=${Sources.enc(p.pass)}"
        val api = "${base(p)}/player_api.php?$auth&action="
        val cats = ArrayList<Category>()
        Net.open(api + if (kind == MOVIES) "get_vod_categories" else "get_series_categories").use { s ->
            val r = JsonReader(InputStreamReader(s, Charsets.UTF_8))
            r.beginArray()
            while (r.hasNext()) {
                var id = ""; var name = ""
                r.beginObject()
                while (r.hasNext()) when (r.nextName()) {
                    "category_id" -> id = Sources.str(r)
                    "category_name" -> name = Sources.str(r)
                    else -> r.skipValue()
                }
                r.endObject()
                if (id.isNotEmpty()) cats.add(Category(id, name.ifEmpty { "Other" }))
            }
            r.endArray()
        }
        val items = ArrayList<VodItem>()
        Net.open(api + if (kind == MOVIES) "get_vod_streams" else "get_series", 60_000).use { s ->
            val r = JsonReader(InputStreamReader(s, Charsets.UTF_8))
            r.beginArray()
            while (r.hasNext()) {
                var id = ""; var name = ""; var poster = ""; var cat = ""; var rating = 0f; var year = 0; var ext = "mp4"; var added = 0L; var tmdb = ""
                r.beginObject()
                while (r.hasNext()) when (r.nextName()) {
                    "stream_id", "series_id" -> id = Sources.str(r)
                    "name" -> name = Sources.str(r)
                    "stream_icon", "cover" -> poster = Sources.str(r)
                    "category_id" -> cat = Sources.str(r)
                    "rating" -> rating = Sources.str(r).toFloatOrNull() ?: 0f
                    "year", "releaseDate", "release_date" -> { val y = yearOf(Sources.str(r)); if (year == 0) year = y }
                    "container_extension" -> ext = Sources.str(r).ifBlank { "mp4" }
                    "added", "last_modified" -> added = Sources.str(r).toLongOrNull() ?: 0L
                    "tmdb", "tmdb_id" -> tmdb = Sources.str(r).trim().takeIf { t -> t.isNotEmpty() && t != "0" && t.all { it.isDigit() } } ?: tmdb
                    else -> r.skipValue()
                }
                r.endObject()
                if (id.isEmpty()) continue
                if (Store.isHidden("$kind:$cat")) continue // (hidden categories aren't kept: less to save and sort)
                if (year == 0) year = yearOf(name)
                items.add(VodItem(id, clean(name).ifEmpty { "Untitled" }, poster.ifBlank { null }, cat, rating, year, ext, added, tmdb))
            }
            r.endArray()
        }
        return Library(cats, items, System.currentTimeMillis())
    }

    private val yearRx = Regex("""(19|20)\d\d""")

    // "EN - ", "UK| ", "[EN] ", "|EN| " at the start; quality tags and "(2021)" at the end (the year is shown separately).
    private val prefixRx = Regex("""^\s*(?:\[[A-Za-z]{2,5}]\s*[|:\-–]?\s*|\|[A-Za-z]{2,5}\|\s*|[A-Z]{2,5}\s*[|:]\s*|[A-Z]{2,5}\s+[-–]\s+)""")
    private val suffixRx = Regex("""\s*[\[(]?(?:4k|uhd|2160p|1080p|720p|fhd|hd|multi|sub|dub)[\])]?\s*$|\s*\(\d{4}\)\s*$""", RegexOption.IGNORE_CASE)

    /** "EN - The Long Journey (2021) 4K" → "The Long Journey". Same idea as the desktop app. */
    fun clean(name: String): String {
        var s = name.trim()
        repeat(4) {
            val t = suffixRx.replace(prefixRx.replace(s, ""), "").trim()
            if (t == s) return@repeat
            s = t
        }
        return s.ifEmpty { name.trim() }
    }
    private fun yearOf(s: String) = yearRx.find(s)?.value?.toIntOrNull() ?: 0

    private val detailsCache = android.util.LruCache<String, Details>(250)
    /** When each show's details were fetched: a show's episode list is fetched again after a while, a film's never. */
    private val detailsAt = HashMap<String, Long>()
    /** Details being fetched right now (the banner, a search result and the page can all ask for the same one). */
    private val fetching = HashMap<String, kotlinx.coroutines.Deferred<Details>>()
    private val detailsScope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + Dispatchers.IO)
    private const val SHOW_FRESH_MS = 15 * 60_000L

    /** Details already fetched (for the banner on Home and in Films / TV Shows). */
    fun cachedDetails(kind: String, id: String): Details? = detailsCache.get("$kind:$id")
        ?: if (id.startsWith("tmdb:")) Upcoming.find(id)?.let { Upcoming.details(it) } else null

    /**
     * Plot, cast and (for shows) the episodes. Fetched once and shared: asking again while it's on its way waits for
     * that one rather than starting another, and a show's episodes fetched in the last few minutes are used as they are.
     */
    suspend fun details(kind: String, item: VodItem): Details {
        val key = "$kind:${item.id}"
        detailsCache.get(key)?.takeIf { kind == MOVIES || System.currentTimeMillis() - (synchronized(detailsAt) { detailsAt[key] } ?: 0L) < SHOW_FRESH_MS }?.let { return it }
        val d = synchronized(fetching) {
            fetching[key] ?: detailsScope.async {
                try { fetchDetails(kind, item).also { detailsCache.put(key, it); synchronized(detailsAt) { detailsAt[key] = System.currentTimeMillis() } } }
                finally { synchronized(fetching) { fetching.remove(key) } }
            }.also { fetching[key] = it }
        }
        return d.await()
    }

    private fun fetchDetails(kind: String, item: VodItem): Details {
        val p = Store.playlist ?: throw IllegalStateException("No playlist")
        val auth = "username=${Sources.enc(p.user)}&password=${Sources.enc(p.pass)}"
        val url = "${base(p)}/player_api.php?$auth&action=" +
            if (kind == MOVIES) "get_vod_info&vod_id=${item.id}" else "get_series_info&series_id=${item.id}"
        val text = Net.open(url, 30_000).use { it.readBytes().toString(Charsets.UTF_8) }
        val root = try { JSONObject(text) } catch (e: Exception) { JSONObject() }
        val info = root.optJSONObject("info") ?: JSONObject()
        fun s(vararg k: String) = k.firstNotNullOfOrNull { info.optString(it).takeIf { v -> v.isNotBlank() && v != "null" } }
        val backdrop = when (val b = info.opt("backdrop_path")) {
            is JSONArray -> b.optString(0).takeIf { it.isNotBlank() }
            is String -> b.takeIf { it.isNotBlank() }
            else -> null
        }
        val eps = ArrayList<Episode>()
        when (val e = root.opt("episodes")) {
            is JSONObject -> e.keys().forEach { season -> (e.opt(season) as? JSONArray)?.let { addEpisodes(eps, it, season.toIntOrNull() ?: 0) } }
            is JSONArray -> for (i in 0 until e.length()) when (val g = e.opt(i)) {
                is JSONArray -> addEpisodes(eps, g, 0)
                is JSONObject -> addEpisode(eps, g, 0)
            }
        }
        eps.sortWith(compareBy({ it.season }, { it.number }))
        return Details(
            title = s("name", "title")?.let { clean(it) }, plot = s("plot", "description"), cast = s("cast", "actors"),
            director = s("director"), genre = s("genre"),
            year = yearOf(s("releasedate", "releaseDate", "release_date") ?: "").takeIf { it > 0 } ?: item.year,
            duration = s("duration"), rating = s("rating"),
            poster = s("movie_image", "cover_big", "cover") ?: item.poster, backdrop = backdrop, episodes = eps,
            tmdbId = s("tmdb_id", "tmdb"),
        )
    }

    private fun addEpisodes(out: MutableList<Episode>, a: JSONArray, season: Int) {
        for (i in 0 until a.length()) (a.opt(i) as? JSONObject)?.let { addEpisode(out, it, season) }
    }

    private fun addEpisode(out: MutableList<Episode>, e: JSONObject, seasonHint: Int) {
        val id = e.optString("id").ifBlank { return }
        val info = e.optJSONObject("info")
        val season = e.optString("season").toIntOrNull()?.takeIf { it > 0 } ?: seasonHint
        fun i(k: String) = info?.optString(k)?.takeIf { it.isNotBlank() && it != "null" }
        out.add(Episode(
            id, e.optString("title").ifBlank { "Episode" }, season, e.optString("episode_num").toIntOrNull() ?: 0,
            e.optString("container_extension").ifBlank { "mp4" },
            // (Providers put an episode's description in different places.)
            i("plot") ?: i("overview") ?: i("description") ?: e.optString("plot").takeIf { it.isNotBlank() && it != "null" },
            i("movie_image") ?: i("cover_big"), i("duration"),
        ))
    }

    // ------------------------------------------------------------------ saved copy

    private fun write(kind: String, lib: Library) {
        val tmp = File(dir, "vod_$kind.bin.tmp")
        DataOutputStream(BufferedOutputStream(FileOutputStream(tmp), 1 shl 16)).use { o ->
            o.writeInt(2); o.writeLong(lib.loadedAt)
            o.writeInt(lib.categories.size)
            for (c in lib.categories) { o.writeUTF(c.id); o.writeUTF(c.name) }
            o.writeInt(lib.items.size)
            for (m in lib.items) {
                o.writeUTF(m.id); o.writeUTF(m.name.take(300)); o.writeUTF(m.poster ?: ""); o.writeUTF(m.categoryId)
                o.writeFloat(m.rating); o.writeShort(m.year); o.writeUTF(m.ext); o.writeLong(m.added); o.writeUTF(m.tmdb)
            }
        }
        tmp.renameTo(File(dir, "vod_$kind.bin"))
    }

    private fun read(kind: String): Library? {
        val f = File(dir, "vod_$kind.bin")
        if (!f.exists()) return null
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            val v = i.readInt()
            if (v != 1 && v != 2) return null
            val at = i.readLong()
            val cats = List(i.readInt()) { Category(i.readUTF(), i.readUTF()) }
            val items = List(i.readInt()) {
                // (Names are saved already tidied: no tidying again here, which made a big library slow to open.)
                VodItem(i.readUTF(), i.readUTF(), i.readUTF().ifEmpty { null }, i.readUTF(), i.readFloat(),
                    i.readShort().toInt(), i.readUTF(), i.readLong(), if (v >= 2) i.readUTF() else "")
            }
            return Library(cats, items, at)
        }
    }
}
