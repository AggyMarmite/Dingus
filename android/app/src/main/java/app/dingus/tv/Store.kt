package app.dingus.tv

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONObject

/** What Dingus remembers: your playlist, favourites, recently watched, and the last channel. Private to the app. */
object Store {
    private lateinit var prefs: SharedPreferences

    fun init(ctx: Context) {
        prefs = ctx.getSharedPreferences("dingus", Context.MODE_PRIVATE)
    }

    var playlist: Playlist?
        get() {
            val s = prefs.getString("playlist", null) ?: return null
            return try {
                val o = JSONObject(s)
                Playlist(
                    o.optString("name"), o.optString("type"), o.optString("server"), o.optString("user"),
                    o.optString("pass"), o.optString("m3uUrl"), o.optString("epgUrl"),
                )
            } catch (e: Exception) { null }
        }
        set(p) {
            if (p == null) { prefs.edit().remove("playlist").apply(); return }
            val o = JSONObject()
                .put("name", p.name).put("type", p.type).put("server", p.server).put("user", p.user)
                .put("pass", p.pass).put("m3uUrl", p.m3uUrl).put("epgUrl", p.epgUrl)
            prefs.edit().putString("playlist", o.toString()).apply()
        }

    var favourites: List<String>
        get() = prefs.getString("favourites", "")!!.split('\n').filter { it.isNotEmpty() }
        set(v) = prefs.edit().putString("favourites", v.joinToString("\n")).apply()

    fun isFavourite(id: String) = favourites.contains(id)

    /** Adds or removes a favourite; true when it's now a favourite. */
    fun toggleFavourite(id: String): Boolean {
        val f = favourites.toMutableList()
        val on = if (f.remove(id)) false else { f.add(id); true }
        favourites = f
        return on
    }

    var recent: List<String>
        get() = prefs.getString("recent", "")!!.split('\n').filter { it.isNotEmpty() }
        set(v) = prefs.edit().putString("recent", v.joinToString("\n")).apply()

    fun noteWatched(id: String) {
        recent = (listOf(id) + recent.filter { it != id }).take(30)
        prefs.edit().putString("last", id).apply()
    }

    val lastChannel: String? get() = prefs.getString("last", null)

    var lastCategory: String?
        get() = prefs.getString("lastCategory", null)
        set(v) = prefs.edit().putString("lastCategory", v).apply()

    var guideLoadedAt: Long
        get() = prefs.getLong("guideLoadedAt", 0)
        set(v) = prefs.edit().putLong("guideLoadedAt", v).apply()
}
