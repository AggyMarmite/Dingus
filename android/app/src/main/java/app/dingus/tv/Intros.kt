package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject

/**
 * Where an episode's intro, recap and end credits are, from TheIntroDB (a free community database, looked up by the
 * show's TMDB number, season and episode). Kept for the session; an episode it doesn't know just has no Skip button.
 */
object Intros {
    /** A part of the episode, in ms ([end] = -1: to the end). */
    class Part(val start: Long, val end: Long)
    class Segments(val intro: Part?, val recap: Part?, val credits: Part?)

    private val cache = HashMap<String, Segments?>()

    suspend fun find(showTmdbId: String, season: Int, episode: Int): Segments? = withContext(Dispatchers.IO) {
        val k = "$showTmdbId:$season:$episode"
        synchronized(cache) { if (cache.containsKey(k)) return@withContext cache[k] }
        val s = try {
            val o = JSONObject(Net.open("https://api.theintrodb.org/v4/media?tmdb_id=$showTmdbId&season=$season&episode=$episode", 8_000)
                .use { it.readBytes().toString(Charsets.UTF_8) })
            fun part(name: String): Part? {
                val a = o.optJSONArray(name) ?: return null
                // The most agreed-on one (the first is the best match).
                val p = (0 until a.length()).map { a.getJSONObject(it) }.firstOrNull() ?: return null
                val start = if (p.isNull("start_ms")) 0L else p.optLong("start_ms")
                val end = if (p.isNull("end_ms")) -1L else p.optLong("end_ms")
                return if (end != -1L && end <= start + 3_000) null else Part(start, end)
            }
            Segments(part("intro"), part("recap"), part("credits"))
        } catch (e: java.io.FileNotFoundException) { null // (an episode it doesn't know)
        } catch (e: Exception) { return@withContext null } // (not remembered: tried again next time)
        synchronized(cache) { cache[k] = s }
        Ui.test("intros $k ${s?.intro != null}/${s?.recap != null}/${s?.credits != null}")
        s
    }
}
