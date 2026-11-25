package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** A film or show's trailer and similar titles, from TMDB (kept for the session). */
object Tmdb {
    private val trailers = HashMap<String, String?>()
    private val similar = HashMap<String, List<VodItem>>()

    private fun type(kind: String) = if (kind == Vod.MOVIES) "movie" else "tv"

    /** The YouTube key of the official trailer (English first), or null. */
    suspend fun trailer(kind: String, item: VodItem, tmdbId: String?): String? = withContext(Dispatchers.IO) {
        val k = "$kind:${item.id}"
        synchronized(trailers) { if (trailers.containsKey(k)) return@withContext trailers[k] }
        val key = try {
            val id = TitleLogos.tmdbIdFor(kind, item, tmdbId) ?: return@withContext null
            val r = TitleLogos.get("/${type(kind)}/$id/videos?include_video_language=en,null").optJSONArray("results")
            val vids = (0 until (r?.length() ?: 0)).map { r!!.getJSONObject(it) }.filter { it.optString("site") == "YouTube" }
            (vids.firstOrNull { it.optString("type") == "Trailer" && it.optBoolean("official") }
                ?: vids.firstOrNull { it.optString("type") == "Trailer" } ?: vids.firstOrNull { it.optString("type") == "Teaser" })?.optString("key")
        } catch (e: Exception) { return@withContext null }
        synchronized(trailers) { trailers[k] = key }
        Ui.test("trailer ${item.name} ${key != null}")
        key
    }

    /** TMDB's recommendations for a title that are in the playlist (same kind), up to [max]. */
    suspend fun similar(kind: String, item: VodItem, tmdbId: String?, max: Int = 10): List<VodItem> = withContext(Dispatchers.IO) {
        val k = "$kind:${item.id}"
        synchronized(similar) { similar[k]?.let { return@withContext it } }
        val list = try {
            val id = TitleLogos.tmdbIdFor(kind, item, tmdbId) ?: return@withContext emptyList()
            val works = ArrayList<People.Work>()
            for (path in listOf("recommendations", "similar")) {
                val r = TitleLogos.get("/${type(kind)}/$id/$path").optJSONArray("results") ?: continue
                for (i in 0 until r.length()) {
                    val o = r.getJSONObject(i)
                    val title = o.optString(if (kind == Vod.MOVIES) "title" else "name")
                    if (title.isBlank()) continue
                    works.add(People.Work(title, o.optString(if (kind == Vod.MOVIES) "release_date" else "first_air_date").take(4).toIntOrNull() ?: 0, kind != Vod.MOVIES))
                }
                if (works.size >= 30) break
            }
            People.matchInOrder(kind, works).filter { it.id != item.id }.take(max)
        } catch (e: Exception) { return@withContext emptyList() }
        synchronized(similar) { similar[k] = list }
        Ui.test("similar ${item.name} ${list.size}")
        list
    }

    private val youTubeApps = listOf("com.amazon.firetv.youtube", "com.amazon.firetv.youtube.tv", "com.google.android.youtube.tv", "com.google.android.youtube")

    /**
     * The trailer in the Fire TV's YouTube app, when it's installed (smoothest); BACK there comes back to the film's
     * page. Never a web browser. False when there's no YouTube app (then Dingus plays it itself).
     */
    fun openInYouTubeApp(a: android.app.Activity, key: String): Boolean {
        val pm = a.packageManager
        for (pkg in youTubeApps) {
            try { pm.getPackageInfo(pkg, 0) } catch (e: Exception) { continue }
            for (u in listOf("https://www.youtube.com/watch?v=$key", "vnd.youtube:$key")) {
                val i = android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(u)).setPackage(pkg)
                    .putExtra("force_fullscreen", true).putExtra("finish_on_ended", true)
                if (i.resolveActivity(pm) != null) {
                    try { a.startActivity(i); Ui.test("trailer-youtube-app $pkg"); return true } catch (e: Exception) {}
                }
            }
        }
        return false
    }

    /** Opens the trailer in the YouTube app; false when there's no app to play it. */
    fun playTrailer(a: android.app.Activity, key: String): Boolean {
        val i = android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse("vnd.youtube:$key"))
        val web = android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse("https://www.youtube.com/watch?v=$key"))
        for (x in listOf(i, web)) if (x.resolveActivity(a.packageManager) != null) { a.startActivity(x); return true }
        return false
    }

    fun canPlayTrailers(a: android.app.Activity) =
        android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse("vnd.youtube:x")).resolveActivity(a.packageManager) != null ||
        android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse("https://www.youtube.com/watch?v=x")).resolveActivity(a.packageManager) != null
}
