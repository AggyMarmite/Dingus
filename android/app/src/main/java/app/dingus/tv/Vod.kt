package app.dingus.tv

import android.content.Context
import android.content.SharedPreferences
import android.util.JsonReader
import android.util.Log
import kotlinx.coroutines.Dispatchers
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
)

/** Films and TV shows from an Xtream Codes login: loaded when first opened, kept on the device, refreshed twice a day. */
object Vod {
    const val MOVIES = "movies"
    const val SERIES = "series"
    const val CONTINUE = "\u0001continue"
    const val RECENT = "\u0001recent"
    const val ALL = "\u0001all"

    class Library(val categories: List<Category>, val items: List<VodItem>, val loadedAt: Long) {
        val byCategory: Map<String, List<VodItem>> by lazy { items.groupBy { it.categoryId } }
        val byId: Map<String, VodItem> by lazy { items.associateBy { it.id } }
        val newest: List<VodItem> by lazy { items.sortedByDescending { it.added }.take(300) }
    }

    @Volatile private var libs = HashMap<String, Library>()
    private val locks = mapOf(MOVIES to Mutex(), SERIES to Mutex())
    private lateinit var dir: File
    private lateinit var progress: SharedPreferences

    fun init(ctx: Context) {
        dir = ctx.filesDir
        progress = ctx.getSharedPreferences("progress", Context.MODE_PRIVATE)
    }

    /** Films and TV shows need an Xtream Codes login (M3U links only carry live channels here). */
    val available: Boolean get() = Store.playlist?.type == "xtream"

    fun cached(kind: String): Library? = libs[kind]

    /** The library, from memory, the saved copy, or the provider (in that order). [fresh] forces the provider. */
    suspend fun library(kind: String, fresh: Boolean = false): Library = withContext(Dispatchers.IO) {
        locks.getValue(kind).withLock {
            if (!fresh) {
                libs[kind]?.let { return@withLock it }
                try { read(kind)?.let { libs = HashMap(libs).apply { put(kind, it) }; return@withLock it } }
                catch (e: Exception) { Log.w("Dingus", "saved $kind", e) }
            }
            val p = Store.playlist ?: throw IllegalStateException("No playlist")
            val lib = fetch(p, kind)
            libs = HashMap(libs).apply { put(kind, lib) }
            try { write(kind, lib) } catch (e: Exception) { Log.w("Dingus", "save $kind", e) }
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
        if (continueList(kind, lib).isNotEmpty()) out.add(Category(CONTINUE, "Continue watching"))
        out.add(Category(RECENT, "Recently added"))
        out.add(Category(ALL, if (kind == MOVIES) "All films" else "All shows"))
        out.addAll(lib.categories.filter { lib.byCategory[it.id]?.isNotEmpty() == true && !Store.isHidden("$kind:${it.id}") })
        return out
    }

    fun itemsIn(kind: String, lib: Library, categoryId: String): List<VodItem> = when (categoryId) {
        CONTINUE -> continueList(kind, lib)
        RECENT -> lib.newest
        ALL -> lib.items
        else -> lib.byCategory[categoryId] ?: emptyList()
    }

    // ------------------------------------------------------------------ addresses

    private fun base(p: Playlist) = Sources.xtreamBase(p.server)
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
    fun savePosition(id: String, pos: Long, length: Long, kind: String, showId: String? = null) {
        val e = progress.edit()
        val finished = length > 0 && (pos >= length * 0.95 || length >= 20 * 60_000L && length - pos < 120_000)
        if (finished || pos < 30_000) e.remove("p:$id") else e.putString("p:$id", "$pos|$length")
        if (finished && length > 0) e.putBoolean("w:$id", true)
        val owner = showId ?: id
        val list = (progress.getString("c:$kind", "") ?: "").split('\n').filter { it.isNotEmpty() && it != owner }
        val keep = kind == SERIES || !finished && pos >= 30_000
        e.putString("c:$kind", ((if (keep) listOf(owner) else emptyList()) + list).take(40).joinToString("\n"))
        if (showId != null) e.putString("last:$showId", id)
        e.apply()
    }

    fun watched(id: String) = progress.getBoolean("w:$id", false)
    fun lastEpisode(showId: String): String? = progress.getString("last:$showId", null)

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
                var id = ""; var name = ""; var poster = ""; var cat = ""; var rating = 0f; var year = 0; var ext = "mp4"; var added = 0L
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
                    else -> r.skipValue()
                }
                r.endObject()
                if (id.isEmpty()) continue
                val clean = name.trim().ifEmpty { "Untitled" }
                if (year == 0) year = yearOf(clean)
                items.add(VodItem(id, clean, poster.ifBlank { null }, cat, rating, year, ext, added))
            }
            r.endArray()
        }
        return Library(cats, items, System.currentTimeMillis())
    }

    private val yearRx = Regex("""(19|20)\d\d""")
    private fun yearOf(s: String) = yearRx.find(s)?.value?.toIntOrNull() ?: 0

    /** Plot, cast and (for shows) the episodes. */
    suspend fun details(kind: String, item: VodItem): Details = withContext(Dispatchers.IO) {
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
        Details(
            title = s("name", "title"), plot = s("plot", "description"), cast = s("cast", "actors"),
            director = s("director"), genre = s("genre"),
            year = yearOf(s("releasedate", "releaseDate", "release_date") ?: "").takeIf { it > 0 } ?: item.year,
            duration = s("duration"), rating = s("rating"),
            poster = s("movie_image", "cover_big", "cover") ?: item.poster, backdrop = backdrop, episodes = eps,
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
            e.optString("container_extension").ifBlank { "mp4" }, i("plot"), i("movie_image"), i("duration"),
        ))
    }

    // ------------------------------------------------------------------ saved copy

    private fun write(kind: String, lib: Library) {
        val tmp = File(dir, "vod_$kind.bin.tmp")
        DataOutputStream(BufferedOutputStream(FileOutputStream(tmp), 1 shl 16)).use { o ->
            o.writeInt(1); o.writeLong(lib.loadedAt)
            o.writeInt(lib.categories.size)
            for (c in lib.categories) { o.writeUTF(c.id); o.writeUTF(c.name) }
            o.writeInt(lib.items.size)
            for (m in lib.items) {
                o.writeUTF(m.id); o.writeUTF(m.name.take(300)); o.writeUTF(m.poster ?: ""); o.writeUTF(m.categoryId)
                o.writeFloat(m.rating); o.writeShort(m.year); o.writeUTF(m.ext); o.writeLong(m.added)
            }
        }
        tmp.renameTo(File(dir, "vod_$kind.bin"))
    }

    private fun read(kind: String): Library? {
        val f = File(dir, "vod_$kind.bin")
        if (!f.exists()) return null
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            if (i.readInt() != 1) return null
            val at = i.readLong()
            val cats = List(i.readInt()) { Category(i.readUTF(), i.readUTF()) }
            val items = List(i.readInt()) {
                VodItem(i.readUTF(), i.readUTF(), i.readUTF().ifEmpty { null }, i.readUTF(), i.readFloat(),
                    i.readShort().toInt(), i.readUTF(), i.readLong())
            }
            return Library(cats, items, at)
        }
    }
}
