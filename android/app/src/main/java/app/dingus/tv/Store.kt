package app.dingus.tv

import android.content.Context
import android.content.SharedPreferences
import org.json.JSONArray
import org.json.JSONObject

/** What Dingus remembers: your playlists, favourites, recently watched, settings. Private to the app. */
object Store {
    private lateinit var prefs: SharedPreferences

    fun init(ctx: Context) {
        prefs = ctx.getSharedPreferences("dingus", Context.MODE_PRIVATE)
        // From the one-playlist version: carry favourites etc. over to the first playlist.
        if (prefs.contains("playlist") && !prefs.contains("playlists")) {
            val e = prefs.edit()
            for (n in listOf("favourites", "recent", "last", "lastCategory")) prefs.getString(n, null)?.let { e.putString("${n}_p1", it) }
            prefs.getStringSet("hidden", null)?.let { e.putStringSet("hidden_p1", it) }
            e.apply()
            playlists = playlists
        }
    }

    // ------------------------------------------------------------------ playlists (several; one is in use)

    private fun toJson(p: Playlist) = JSONObject()
        .put("id", p.id).put("name", p.name).put("type", p.type).put("server", p.server).put("user", p.user)
        .put("pass", p.pass).put("m3uUrl", p.m3uUrl).put("epgUrl", p.epgUrl).put("providerGuide", p.useProviderGuide)

    private fun fromJson(o: JSONObject) = Playlist(
        o.optString("name"), o.optString("type"), o.optString("server"), o.optString("user"),
        o.optString("pass"), o.optString("m3uUrl"), o.optString("epgUrl"), o.optString("id").ifEmpty { "p1" },
        o.optBoolean("providerGuide", true),
    )

    @Volatile private var cachedPlaylists: List<Playlist>? = null

    var playlists: List<Playlist>
        get() {
            cachedPlaylists?.let { return it }
            val list = try {
                val raw = prefs.getString("playlists", null)
                if (raw != null) JSONArray(raw).let { a -> (0 until a.length()).map { fromJson(a.getJSONObject(it)) } }
                else prefs.getString("playlist", null)?.let { listOf(fromJson(JSONObject(it)).copy(id = "p1")) } ?: emptyList()
            } catch (e: Exception) { emptyList() }
            cachedPlaylists = list
            return list
        }
        set(v) {
            val a = JSONArray(); v.forEach { a.put(toJson(it)) }
            prefs.edit().putString("playlists", a.toString()).remove("playlist").apply()
            cachedPlaylists = v
        }

    /** The playlist in use. */
    var activeId: String
        get() = prefs.getString("active", null) ?: playlists.firstOrNull()?.id ?: "p1"
        set(v) = prefs.edit().putString("active", v).apply()

    fun newId(): String = "p" + (System.currentTimeMillis() % 100_000_000).toString(36)

    /** The playlist in use. Setting it adds or replaces it (by id); null removes it and moves to another one. */
    var playlist: Playlist?
        get() = playlists.firstOrNull { it.id == activeId } ?: playlists.firstOrNull()
        set(p) {
            if (p == null) {
                val rest = playlists.filter { it.id != activeId }
                playlists = rest
                rest.firstOrNull()?.let { activeId = it.id }
                return
            }
            @Suppress("NAME_SHADOWING") val p = if (p.id.isEmpty()) p.copy(id = newId()) else p
            playlists = if (playlists.any { it.id == p.id }) playlists.map { if (it.id == p.id) p else it } else playlists + p
            activeId = p.id
        }

    /** Settings that belong to one playlist (favourites, history…) are stored under its id. */
    private fun k(name: String) = "${name}_$activeId"

    // Read often (the guide draws a star per row), so kept in memory.
    @Volatile private var favCache: List<String>? = null
    @Volatile private var favSet: Set<String> = emptySet()
    @Volatile private var favFor = ""

    var favourites: List<String>
        get() {
            val id = activeId
            favCache?.let { if (favFor == id) return it }
            val list = prefs.getString(k("favourites"), "")!!.split('\n').filter { it.isNotEmpty() }
            favCache = list; favSet = list.toHashSet(); favFor = id
            return list
        }
        set(v) {
            prefs.edit().putString(k("favourites"), v.joinToString("\n")).apply()
            favCache = v; favSet = v.toHashSet(); favFor = activeId
        }

    fun isFavourite(id: String): Boolean { favourites; return favSet.contains(id) }

    /** Adds or removes a favourite; true when it's now a favourite. */
    fun toggleFavourite(id: String): Boolean {
        val f = favourites.toMutableList()
        val on = if (f.remove(id)) false else { f.add(id); true }
        favourites = f
        return on
    }

    var recent: List<String>
        get() = prefs.getString(k("recent"), "")!!.split('\n').filter { it.isNotEmpty() }
        set(v) = prefs.edit().putString(k("recent"), v.joinToString("\n")).apply()

    fun noteWatched(id: String) {
        recent = (listOf(id) + recent.filter { it != id }).take(30)
        prefs.edit().putString(k("last"), id).apply()
    }

    val lastChannel: String? get() = prefs.getString(k("last"), null)

    var lastCategory: String?
        get() = prefs.getString(k("lastCategory"), null)
        set(v) = prefs.edit().putString(k("lastCategory"), v).apply()

    var guideLoadedAt: Long
        get() = prefs.getLong(k("guideLoadedAt"), 0)
        set(v) = prefs.edit().putLong(k("guideLoadedAt"), v).apply()

    /** The provider's time zone (Xtream catch-up addresses use it). */
    var serverTimeZone: String?
        get() = prefs.getString(k("serverTz"), null)
        set(v) = prefs.edit().putString(k("serverTz"), v).apply()

    var background: String
        get() = prefs.getString("background", "midnight")!!
        set(v) = prefs.edit().putString("background", v).apply()
    var accent: String
        get() = prefs.getString("accent", "blue")!!
        set(v) = prefs.edit().putString("accent", v).apply()

    /** Switch the TV's refresh rate to suit the video. */
    /** Stream stats kept on screen while watching (≡ → Show stream stats). */
    var statsPinned: Boolean
        get() = prefs.getBoolean("statsPinned", false)
        set(v) = prefs.edit().putBoolean("statsPinned", v).apply()

    var matchFrameRate: Boolean
        get() = prefs.getBoolean("matchFrameRate", true)
        set(v) = prefs.edit().putBoolean("matchFrameRate", v).apply()

    /** Live channels from an Xtream login as "ts" (MPEG-TS) or "hls". */
    var streamFormat: String
        get() = prefs.getString("streamFormat", "ts")!!
        set(v) = prefs.edit().putString("streamFormat", v).apply()

    /** Hours of TV guide to keep ahead (less = lighter on memory). */
    var guideHours: Int
        get() = prefs.getInt("guideHours", 24)
        set(v) = prefs.edit().putInt("guideHours", v).apply()

    /** Keep the last 15 minutes of live TV, so it can be paused and rewound. */
    var timeshift: Boolean
        get() = prefs.getBoolean("timeshift", true)
        set(v) = prefs.edit().putBoolean("timeshift", v).apply()

    var remindersJson: String
        get() = prefs.getString("reminders", "[]")!!
        set(v) = prefs.edit().putString("reminders", v).apply()

    /** Show a channel's HD/FHD/backup versions as one row. */
    var mergeChannels: Boolean
        get() = prefs.getBoolean("mergeChannels", false)
        set(v) = prefs.edit().putBoolean("mergeChannels", v).apply()

    /** A coloured stripe on programmes by genre (sport, films, news…). */
    var genreColours: Boolean
        get() = prefs.getBoolean("genreColours", true)
        set(v) = prefs.edit().putBoolean("genreColours", v).apply()

    var channelEditsJson: String
        get() = prefs.getString(k("channelEdits"), "{}")!!
        set(v) = prefs.edit().putString(k("channelEdits"), v).apply()

    /** Your order for categories ("live", "movies", "series"): ids, first first. */
    fun categoryOrder(kind: String): List<String> =
        prefs.getString(k("order_$kind"), "")!!.split('\n').filter { it.isNotEmpty() }
    fun setCategoryOrder(kind: String, ids: List<String>) = prefs.edit().putString(k("order_$kind"), ids.joinToString("\n")).apply()

    /** Categories in your order (ones you haven't moved keep the playlist's order, after the moved ones' places). */
    fun <T> ordered(kind: String, list: List<T>, id: (T) -> String): List<T> {
        val order = categoryOrder(kind)
        if (order.isEmpty()) return list
        val rank = HashMap<String, Int>(order.size * 2).apply { order.forEachIndexed { i, s -> put(s, i) } }
        return list.withIndex().sortedWith(compareBy({ rank[id(it.value)] ?: (order.size + it.index) }, { it.index })).map { it.value }
    }

    /** Move a category one place up or down among [visible] (the list as shown). */
    fun moveCategory(kind: String, visible: List<String>, id: String, by: Int) {
        val l = visible.toMutableList()
        val i = l.indexOf(id)
        val j = i + by
        if (i < 0 || j < 0 || j >= l.size) return
        l.add(j, l.removeAt(i))
        setCategoryOrder(kind, l)
    }

    /** Plain settings shared by every playlist (e.g. the VPN). */
    fun prefsString(key: String, def: String): String = prefs.getString(key, def) ?: def
    fun putString(key: String, v: String) = prefs.edit().putString(key, v).apply()

    /** Show channel numbers in the guide and elsewhere. */
    /** Reminders: switch to the channel by itself when the programme starts (if nothing's pressed). Off unless chosen. */
    /** How long before a reminded programme the reminder comes up, in seconds (1 minute unless changed). */
    var reminderLead: Int
        get() = prefs.getInt("reminderLead", 60)
        set(v) = prefs.edit().putInt("reminderLead", v).apply()

    var reminderSwitch: Boolean
        get() = prefs.getBoolean("reminderSwitch", false)
        set(v) = prefs.edit().putBoolean("reminderSwitch", v).apply()

    var showNumbers: Boolean
        get() = prefs.getBoolean("showNumbers", false)
        set(v) = prefs.edit().putBoolean("showNumbers", v).apply()

    /** How often channels and the guide update by themselves, in hours. */
    var refreshHours: Int
        get() = prefs.getInt("refreshHours", 24)
        set(v) = prefs.edit().putInt("refreshHours", v).apply()
    val refreshDue: Boolean get() = System.currentTimeMillis() - guideLoadedAt > refreshHours * 3600_000L

    /** The simpler Home page (for devices that struggle with the full one). */
    var homeLite: Boolean
        get() = prefs.getBoolean("homeLite", false)
        set(v) = prefs.edit().putBoolean("homeLite", v).apply()

    /** The screen Dingus opens on: "home" or "live". */
    var startScreen: String
        get() = prefs.getString("startScreen", "home")!!
        set(v) = prefs.edit().putString("startScreen", v).apply()

    /** Turn subtitles on automatically (in [subtitleLanguage]) when there are some; otherwise they start off. */
    var autoSubtitles: Boolean
        get() = prefs.getBoolean("autoSubtitles", false)
        set(v) = prefs.edit().putBoolean("autoSubtitles", v).apply()
    /** Two-letter language code, e.g. "en". */
    var subtitleLanguage: String
        get() = prefs.getString("subtitleLanguage", java.util.Locale.getDefault().language.ifEmpty { "en" })!!
        set(v) = prefs.edit().putString("subtitleLanguage", v).apply()

    var accountJson: String
        get() = prefs.getString(k("account"), "")!!
        set(v) = prefs.edit().putString(k("account"), v).apply()

    /** Show official title logos for films and shows (when TMDB has one). */
    var titleLogos: Boolean
        get() = prefs.getBoolean("titleLogos", true)
        set(v) = prefs.edit().putBoolean("titleLogos", v).apply()

    /** Play the highlighted channel in the small window above the guide. */
    var preview: Boolean
        get() = prefs.getBoolean("preview", true)
        set(v) = prefs.edit().putBoolean("preview", v).apply()

    var updateCheckedAt: Long
        get() = prefs.getLong("updateCheckedAt", 0)
        set(v) = prefs.edit().putLong("updateCheckedAt", v).apply()

    /** Categories you've hidden, as "live:<id>", "movies:<id>" or "series:<id>". */
    var hidden: Set<String>
        get() = prefs.getStringSet(k("hidden"), emptySet())!!.toSet()
        set(v) { prefs.edit().putStringSet(k("hidden"), HashSet(v)).apply(); cachedHidden = null }
    /** Forget per-playlist caches (after switching playlist). */
    fun switched() { cachedHidden = null; favCache = null }
    fun isHidden(key: String) = hiddenCache().contains(key)
    fun setHidden(key: String, on: Boolean) { hidden = if (on) hidden + key else hidden - key; cachedHidden = null }
    @Volatile private var cachedHidden: Set<String>? = null
    private fun hiddenCache(): Set<String> = cachedHidden ?: hidden.also { cachedHidden = it }
}
