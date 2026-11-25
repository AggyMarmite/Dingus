package app.dingus.tv

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File

/**
 * TMDB's ratings for a film or show: the UK age rating (U, PG, 12A, 12, 15, 18) and TMDB's score with its number of
 * votes. Looked up once per title and kept (a title TMDB doesn't know is remembered too, so it isn't asked again).
 */
object Ratings {
    class Info(val age: String?, val score: Float, val votes: Int) {
        /** "15  ·  TMDB 7.4 (12.3k votes)" */
        fun line(): String = listOfNotNull(age, score.takeIf { it > 0 && votes > 0 }?.let { "TMDB %.1f (%s votes)".format(it, short(votes)) }).joinToString("  ·  ")
        private fun short(n: Int) = if (n >= 1000) "%.1fk".format(n / 1000f) else n.toString()
    }

    private lateinit var file: File
    private val known = HashMap<String, Info?>()
    private var loaded = false

    fun init(ctx: Context) { file = File(ctx.filesDir, "ratings.json") }

    private fun key(kind: String, item: VodItem) = "$kind|${item.id}"

    private fun load() {
        if (loaded) return
        loaded = true
        try {
            val o = JSONObject(file.readText())
            for (k in o.keys()) o.optJSONObject(k)?.let { known[k] = Info(it.optString("a").ifEmpty { null }, it.optDouble("s").toFloat(), it.optInt("v")) } ?: run { known[k] = null }
        } catch (_: Exception) {}
    }

    /** What's kept (no network). */
    fun cached(kind: String, item: VodItem): Info? = synchronized(known) { load(); known[key(kind, item)] }

    /** Looks the ratings up on TMDB if they aren't kept yet. */
    suspend fun find(kind: String, item: VodItem, tmdbId: String?): Info? = withContext(Dispatchers.IO) {
        if (!TitleLogos.enabled && BuildConfig.TMDB_KEY.isBlank()) return@withContext null
        val k = key(kind, item)
        synchronized(known) { load(); if (known.containsKey(k)) return@withContext known[k] }
        val type = if (kind == Vod.MOVIES) "movie" else "tv"
        val info = try {
            val id = tmdbId?.takeIf { it.isNotBlank() && it != "0" } ?: TitleLogos.search(type, item)
            id?.let {
                val o = TitleLogos.get("/$type/$it?append_to_response=" + if (type == "movie") "release_dates" else "content_ratings")
                Info(age(o, type), o.optDouble("vote_average").toFloat(), o.optInt("vote_count"))
            }
        } catch (e: Exception) { return@withContext null } // (not remembered: tried again another time)
        val copy = synchronized(known) { known[k] = info; HashMap(known) }
        try {
            val out = JSONObject()
            for ((kk, v) in copy) out.put(kk, v?.let { JSONObject().put("a", it.age ?: "").put("s", it.score.toDouble()).put("v", it.votes) } ?: JSONObject.NULL)
            synchronized(this@Ratings) { file.writeText(out.toString()) }
        } catch (_: Exception) {}
        Ui.test("ratings ${item.name} ${info?.line() ?: "none"}")
        info
    }

    /** The UK certificate (or none). */
    private fun age(o: JSONObject, type: String): String? {
        val list = o.optJSONObject(if (type == "movie") "release_dates" else "content_ratings")?.optJSONArray("results") ?: return null
        for (i in 0 until list.length()) {
            val r = list.getJSONObject(i)
            if (r.optString("iso_3166_1") != "GB") continue
            if (type == "tv") return r.optString("rating").ifBlank { null }
            val dates = r.optJSONArray("release_dates") ?: return null
            for (j in 0 until dates.length()) dates.getJSONObject(j).optString("certification").takeIf { it.isNotBlank() }?.let { return it }
        }
        return null
    }
}
