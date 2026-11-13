package app.dingus.tv

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

/**
 * Official title logos for films and shows, from TMDB (shown in place of the plain title where there is one).
 * Each title is looked up once and the answer kept on the device; the logo images come from TMDB's image server.
 * Film and show logos from TMDB – this product uses the TMDB API but is not endorsed or certified by TMDB.
 */
object TitleLogos {
    private lateinit var file: File
    private val known = HashMap<String, String>() // key → logo address ("" = none)
    @Volatile private var loaded = false

    fun init(ctx: Context) { file = File(ctx.filesDir, "title-logos.json") }

    /** Your own key from Settings, otherwise the one built in. */
    private val key: String get() = BuildConfig.TMDB_KEY
    val enabled: Boolean get() = key.isNotBlank() && Store.titleLogos

    private fun load() {
        if (loaded) return
        loaded = true
        try { val o = JSONObject(file.readText()); o.keys().forEach { known[it] = o.getString(it) } } catch (_: Exception) {}
    }

    private fun save() {
        try { file.writeText(JSONObject(known as Map<*, *>).toString()) } catch (_: Exception) {}
    }

    private fun keyFor(kind: String, item: VodItem) = "$kind:${item.id}"

    /** The logo if it's already known (no network). */
    fun cached(kind: String, item: VodItem): String? = synchronized(known) { load(); known[keyFor(kind, item)]?.ifEmpty { null } }

    /** Finds the logo: by the provider's TMDB number if it gave one, otherwise by title and year. */
    suspend fun find(kind: String, item: VodItem, tmdbId: String?): String? = withContext(Dispatchers.IO) {
        if (!enabled) return@withContext null
        val k = keyFor(kind, item)
        synchronized(known) { load(); known[k]?.let { return@withContext it.ifEmpty { null } } }
        val type = if (kind == Vod.MOVIES) "movie" else "tv"
        val url = try {
            val id = tmdbId?.takeIf { it.isNotBlank() && it != "0" } ?: search(type, item)
            id?.let { logo(type, it) }
        } catch (e: Exception) { return@withContext null } // not remembered: try again another time
        synchronized(known) { known[k] = url ?: ""; save() }
        Ui.test("title-logo ${item.name} ${if (url != null) "found" else "none"}")
        url
    }

    private fun get(path: String): JSONObject {
        val k = key
        // The short "API key" goes in the address; the long "read access token" in a header. Either works.
        val bearer = k.length > 60
        val u = "https://api.themoviedb.org/3$path" + if (bearer) "" else (if ('?' in path) "&" else "?") + "api_key=$k"
        val c = (URL(u).openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000; readTimeout = 15_000
            if (bearer) setRequestProperty("Authorization", "Bearer $k")
            setRequestProperty("Accept", "application/json")
        }
        try {
            if (c.responseCode != 200) throw java.io.IOException("TMDB ${c.responseCode}")
            return JSONObject(c.inputStream.use { it.readBytes().toString(Charsets.UTF_8) })
        } finally { c.disconnect() }
    }

    private fun search(type: String, item: VodItem): String? {
        val q = URLEncoder.encode(item.name, "UTF-8")
        val year = if (item.year > 0) (if (type == "movie") "&year=${item.year}" else "&first_air_date_year=${item.year}") else ""
        val results = get("/search/$type?query=$q$year&include_adult=false").optJSONArray("results") ?: return null
        return results.optJSONObject(0)?.optString("id")?.takeIf { it.isNotEmpty() }
    }

    /** English logo first, then one with no language, best rated; a wide logo works best. */
    private fun logo(type: String, id: String): String? {
        val logos = get("/$type/$id/images?include_image_language=en,null").optJSONArray("logos") ?: return null
        var best: JSONObject? = null
        var bestScore = -1.0
        for (i in 0 until logos.length()) {
            val l = logos.getJSONObject(i)
            val lang = l.optString("iso_639_1")
            val score = (if (lang == "en") 10.0 else if (lang.isEmpty() || lang == "null") 5.0 else 0.0) + l.optDouble("vote_average", 0.0) +
                (if (l.optDouble("aspect_ratio", 1.0) >= 1.5) 1.0 else 0.0)
            if (score > bestScore) { bestScore = score; best = l }
        }
        return best?.optString("file_path")?.takeIf { it.isNotEmpty() }?.let { "https://image.tmdb.org/t/p/w500$it" }
    }

    /** Shows the logo in [logo] instead of [title] (the title stays if there isn't one). Logos keep their transparency. */
    fun show(logo: android.widget.ImageView, title: android.view.View, url: String?) {
        if (url == null) { logo.visibility = android.view.View.GONE; logo.setImageDrawable(null); logo.tag = null; title.visibility = android.view.View.VISIBLE; return }
        if (logo.tag == url) return
        logo.visibility = android.view.View.VISIBLE
        Logos.load(logo, url, (96 * logo.resources.displayMetrics.density).toInt(), title)
    }
}
