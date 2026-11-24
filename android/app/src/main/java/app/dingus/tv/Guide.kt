package app.dingus.tv

import android.util.Xml
import org.xmlpull.v1.XmlPullParser
import java.io.InputStream

/**
 * Reads an XMLTV guide as it downloads (guides can be 100 MB+, so it's never all in memory), keeping only the
 * programmes for your channels from an hour ago to [hoursAhead] hours ahead. Channels are matched by the playlist's
 * guide id, or else by name.
 */
object Guide {
    class Parsed(val programmes: HashMap<String, Array<Programme>>, val keysByChannelId: Map<String, String>, val logosByChannelId: Map<String, String> = emptyMap())

    /** "BBC One HD", "UK: BBC One FHD" → "bbcone". */
    fun norm(name: String): String {
        var s = name.lowercase()
        s = s.replace(prefixRx, "")
        s = s.replace(qualityRx, "")
        return s.filter { it.isLetterOrDigit() }
    }

    // (Made once, not for every name in the guide.)
    private val prefixRx = Regex("""^\s*([a-z]{2,4})\s*[|:\-]\s*""")
    private val qualityRx = Regex("""\b(uhd|fhd|hd|sd|4k|hevc|h265|1080p|720p|50fps|backup|\+1)\b""")

    fun parse(input: InputStream, channels: List<Channel>, hoursAhead: Int = 14, hoursBack: Int = 1): Parsed {
        val now = System.currentTimeMillis()
        val from = now - hoursBack * 60 * 60_000L
        val until = now + hoursAhead * 60 * 60_000L
        val descFrom = now - 3 * 60 * 60_000L
        val descUntil = now + 12 * 60 * 60_000L

        // What we're looking for: guide ids from the playlist, and names for channels without one.
        val byId = HashMap<String, MutableList<Channel>>()
        val byName = HashMap<String, MutableList<Channel>>()
        for (c in channels) {
            c.epgId?.lowercase()?.let { byId.getOrPut(it) { ArrayList() }.add(c) }
            byName.getOrPut(norm(c.name)) { ArrayList() }.add(c)
        }
        // guide channel id (lower case) → the key its programmes are kept under
        val keyOf = HashMap<String, String>()
        val keysByChannel = HashMap<String, String>()
        val progs = HashMap<String, ArrayList<Programme>>()
        val logos = HashMap<String, String>() // channel id → the guide's logo for it

        val p = Xml.newPullParser()
        p.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, false)
        p.setInput(input, null)
        var ev = p.eventType
        while (ev != XmlPullParser.END_DOCUMENT) {
            if (ev == XmlPullParser.START_TAG) {
                when (p.name) {
                    "channel" -> {
                        val id = p.getAttributeValue(null, "id")?.lowercase() ?: ""
                        val names = ArrayList<String>()
                        var icon: String? = null
                        while (inside(p, "channel")) {
                            if (p.eventType == XmlPullParser.START_TAG && p.name == "display-name") names.add(p.nextText())
                            else if (p.eventType == XmlPullParser.START_TAG && p.name == "icon" && icon == null) icon = p.getAttributeValue(null, "src")?.trim()?.takeIf { it.startsWith("http") }
                        }
                        if (id.isEmpty()) { ev = p.next(); continue }
                        val matched = byId[id] ?: names.firstNotNullOfOrNull { n ->
                            byName[norm(n)]?.filter { it.epgId == null || !byId.containsKey(it.epgId.lowercase()) }?.ifEmpty { null }
                        } ?: byName[norm(id)]
                        if (matched != null) {
                            keyOf[id] = id
                            for (c in matched) if (!keysByChannel.containsKey(c.id)) { keysByChannel[c.id] = id; icon?.let { logos[c.id] = it } }
                        }
                    }
                    "programme" -> {
                        val ch = p.getAttributeValue(null, "channel")?.lowercase()
                        // Guides that skip <channel> entries: match by the programme's own channel id.
                        if (ch != null && !keyOf.containsKey(ch) && byId.containsKey(ch)) {
                            keyOf[ch] = ch
                            for (c in byId[ch]!!) if (!keysByChannel.containsKey(c.id)) keysByChannel[c.id] = ch
                        }
                        val key = ch?.let { keyOf[it] }
                        val start = time(p.getAttributeValue(null, "start"))
                        val stop = time(p.getAttributeValue(null, "stop"))
                        if (key == null || start == 0L || stop <= from || start >= until) {
                            skip(p)
                        } else {
                            var title = ""; var desc: String? = null; var genre: Byte = 0
                            while (inside(p, "programme")) {
                                if (p.eventType == XmlPullParser.START_TAG) when (p.name) {
                                    "title" -> if (title.isEmpty()) title = p.nextText().trim()
                                    // Descriptions are most of a guide's size: kept only near "now", and kept short.
                                    "desc" -> if (desc == null) { val t = p.nextText(); if (start < descUntil && stop > descFrom) desc = t.trim().take(240) }
                                    "category" -> if (genre == Genres.NONE) genre = Genres.of(p.nextText())
                                }
                            }
                            progs.getOrPut(key) { ArrayList() }.add(Programme(start, if (stop > start) stop else start + 1800_000, title.intern(), desc, genre))
                        }
                    }
                }
            }
            ev = p.next()
        }
        val out = HashMap<String, Array<Programme>>(progs.size * 2)
        for ((k, v) in progs) {
            v.sortBy { it.start }
            out[k] = v.toTypedArray()
        }
        return Parsed(out, keysByChannel, logos)
    }

    /** Moves on one step inside an element; false at its end (or the end of the file). */
    private fun inside(p: XmlPullParser, tag: String): Boolean {
        val e = p.next()
        return !(e == XmlPullParser.END_DOCUMENT || (e == XmlPullParser.END_TAG && p.name == tag))
    }

    private fun skip(p: XmlPullParser) {
        var depth = 1
        while (depth > 0) when (p.next()) {
            XmlPullParser.START_TAG -> depth++
            XmlPullParser.END_TAG -> depth--
            XmlPullParser.END_DOCUMENT -> return
        }
    }

    /**
     * "20261112210000 +0000" → milliseconds (0 if it can't be read). Done with plain arithmetic: a guide has a
     * time on every programme, and building a calendar for each one was most of the parse time.
     */
    fun time(s: String?): Long {
        if (s == null || s.length < 14) return 0
        return try {
            fun num(from: Int, to: Int): Int {
                var v = 0
                for (i in from until to) { val d = s[i] - '0'; if (d !in 0..9) throw NumberFormatException(); v = v * 10 + d }
                return v
            }
            val days = daysFromCivil(num(0, 4), num(4, 6), num(6, 8))
            var t = (days * 86_400L + num(8, 10) * 3600L + num(10, 12) * 60L + num(12, 14)) * 1000L
            val tz = s.substring(14).trim()
            if (tz.length >= 5 && (tz[0] == '+' || tz[0] == '-')) {
                val mins = tz.substring(1, 3).toInt() * 60 + tz.substring(3, 5).toInt()
                t -= (if (tz[0] == '+') 1 else -1) * mins * 60_000L
            }
            t
        } catch (e: Exception) { 0 }
    }

    /** Days since 1970-01-01 for a date in the Gregorian calendar (the standard civil-date formula). */
    private fun daysFromCivil(y0: Int, m: Int, d: Int): Long {
        val y = if (m <= 2) y0 - 1 else y0
        val era = (if (y >= 0) y else y - 399) / 400
        val yoe = y - era * 400
        val doy = (153 * (if (m > 2) m - 3 else m + 9) + 2) / 5 + d - 1
        val doe = yoe * 365 + yoe / 4 - yoe / 100 + doy
        return era * 146097L + doe - 719468L
    }
}
