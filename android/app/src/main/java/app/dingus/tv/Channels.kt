package app.dingus.tv

import org.json.JSONObject

/**
 * Works out which playlist entries are the same channel ("UK: Channel One", "UK: Channel One HD",
 * "UK: Channel One FHD (Backup)") so the guide shows them as one row, and orders the versions best-first for
 * playing and for trying another when one fails. Same rules as the desktop app.
 */
object Merge {
    private val countryRx = Regex("""^\s*(?:\[\s*([a-z]{2,3})\s*]|\|\s*([a-z]{2,3})\s*\||([a-z]{2,3})\s*[:|]|([A-Z]{2,3})\s+-\s+)""", RegexOption.IGNORE_CASE)
    private val backupRx = Regex("""\b(?:backup|back\s*up|bkp|b/u|alt(?:ernative)?|reserve|mirror|feed\s*\d|server\s*\d|link\s*\d|\(\s*\d\s*\))""", RegexOption.IGNORE_CASE)
    private val uhdRx = Regex("""\b(?:uhd|4k|2160p?|ultra\s*hd)\b|ᵁᴴᴰ""", RegexOption.IGNORE_CASE)
    private val fhdRx = Regex("""\b(?:fhd|1080[pi]?|full\s*hd)\b|ᶠᴴᴰ""", RegexOption.IGNORE_CASE)
    private val sdRx = Regex("""\b(?:sd|576[pi]?|480p?|lq|low)\b""", RegexOption.IGNORE_CASE)

    private val keys = java.util.concurrent.ConcurrentHashMap<String, String>()

    /** Forget worked-out keys (the channel list changed). */
    fun clear() = keys.clear()

    /** Same key = same channel; empty = never merged. Worked out once per channel name. */
    fun key(c: Channel): String = keys.getOrPut(c.name) { computeKey(c) }

    private fun computeKey(c: Channel): String {
        val m = countryRx.find(c.name)
        val country = m?.groupValues?.drop(1)?.firstOrNull { it.isNotEmpty() }?.lowercase() ?: ""
        val core = Guide.norm(backupRx.replace(c.name, " "))
        return if (core.length < 2) "" else "$country|$core"
    }

    /** 4 = 4K, 3 = 1080p, 2 = HD/unknown, 1 = SD. */
    fun rank(c: Channel): Int = when {
        uhdRx.containsMatchIn(c.name) -> 4
        fhdRx.containsMatchIn(c.name) -> 3
        sdRx.containsMatchIn(c.name) -> 1
        else -> 2
    }

    fun isBackup(c: Channel) = backupRx.containsMatchIn(c.name)

    /** Versions in the order to try: main before backup, best quality first, otherwise playlist order. */
    fun order(versions: List<Channel>): List<Channel> =
        versions.withIndex().sortedWith(compareBy({ if (isBackup(it.value)) 1 else 0 }, { -rank(it.value) }, { it.index })).map { it.value }

    /** A list with each channel's versions folded into one entry (the best version), in the list's order. */
    fun fold(list: List<Channel>): List<Channel> {
        if (list.size < 2) return list
        val groups = LinkedHashMap<String, MutableList<Channel>>()
        var solo = 0
        for (c in list) {
            val k = key(c).ifEmpty { "\u0000${solo++}" }
            groups.getOrPut(k) { ArrayList(1) }.add(c)
        }
        if (groups.size == list.size) return list
        return groups.values.map { if (it.size == 1) it[0] else order(it)[0] }
    }
}

/** Colour-codes programmes by the guide's genre (sport, film, news, kids, factual, entertainment). */
object Genres {
    const val NONE: Byte = 0
    const val SPORT: Byte = 1
    const val FILM: Byte = 2
    const val NEWS: Byte = 3
    const val KIDS: Byte = 4
    const val FACTUAL: Byte = 5
    const val ENTERTAINMENT: Byte = 6

    fun of(category: String): Byte {
        val g = category.lowercase()
        fun has(vararg w: String) = w.any { g.contains(it) }
        return when {
            has("sport", "football", "soccer", "racing", "motor", "f1", "cricket", "rugby", "golf", "tennis", "boxing", "darts", "snooker", "nfl", "nba", "ufc", "wrestling") -> SPORT
            has("film", "movie", "cinema") -> FILM
            has("news", "current affairs", "weather", "politic", "business") -> NEWS
            has("kid", "child", "animation", "cartoon", "family") -> KIDS
            has("documentar", "factual", "nature", "history", "science", "educat", "travel") -> FACTUAL
            has("entertain", "drama", "comedy", "soap", "reality", "series", "show", "music", "talk", "game") -> ENTERTAINMENT
            else -> NONE
        }
    }

    fun color(g: Byte): Int = when (g) {
        SPORT -> 0xFF22C55E.toInt()
        FILM -> 0xFFA855F7.toInt()
        NEWS -> 0xFF3B82F6.toInt()
        KIDS -> 0xFFF59E0B.toInt()
        FACTUAL -> 0xFF14B8A6.toInt()
        ENTERTAINMENT -> 0xFFEC4899.toInt()
        else -> 0
    }

    val names = listOf(SPORT to "Sport", FILM to "Films", NEWS to "News", KIDS to "Kids", FACTUAL to "Factual", ENTERTAINMENT to "Entertainment")
}

/** Your own changes to channels: a new name, a different number, or hidden. Kept per playlist. */
object ChannelEdits {
    class Edit(val name: String?, val number: Int?, val hidden: Boolean)

    @Volatile private var cache: Map<String, Edit>? = null
    @Volatile private var cacheFor = ""

    fun all(): Map<String, Edit> {
        val id = Store.activeId
        cache?.let { if (cacheFor == id) return it }
        val m = HashMap<String, Edit>()
        try {
            val o = JSONObject(Store.channelEditsJson)
            for (k in o.keys()) {
                val e = o.getJSONObject(k)
                m[k] = Edit(e.optString("n").ifEmpty { null }, if (e.has("#")) e.getInt("#") else null, e.optBoolean("h"))
            }
        } catch (_: Exception) {}
        cache = m; cacheFor = id
        return m
    }

    fun set(channelId: String, e: Edit?) {
        val m = HashMap(all())
        if (e == null || (e.name == null && e.number == null && !e.hidden)) m.remove(channelId) else m[channelId] = e
        val o = JSONObject()
        for ((k, v) in m) o.put(k, JSONObject().apply {
            v.name?.let { put("n", it) }; v.number?.let { put("#", it) }; if (v.hidden) put("h", true)
        })
        Store.channelEditsJson = o.toString()
        cache = m
    }

    /** The playlist's channels with your edits applied (hidden ones left out unless [includeHidden]). */
    fun apply(list: List<Channel>, includeHidden: Boolean = false): List<Channel> {
        val edits = all()
        if (edits.isEmpty()) return list
        val out = ArrayList<Channel>(list.size)
        for (c in list) {
            val e = edits[c.id]
            if (e == null) { out.add(c); continue }
            if (e.hidden && !includeHidden) continue
            if (e.name == null && e.number == null) { out.add(c); continue }
            out.add(Channel(c.id, e.name ?: c.name, e.number ?: c.number, c.categoryId, c.logo, c.epgId, c.url, c.archiveDays).also { it.guideKey = c.guideKey })
        }
        return out
    }
}
