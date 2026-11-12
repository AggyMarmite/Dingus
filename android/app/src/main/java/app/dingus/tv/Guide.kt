package app.dingus.tv

import android.util.Xml
import org.xmlpull.v1.XmlPullParser
import java.io.InputStream
import java.util.Calendar
import java.util.TimeZone

/**
 * Reads an XMLTV guide as it downloads (guides can be 100 MB+, so it's never all in memory), keeping only the
 * programmes for your channels from an hour ago to [hoursAhead] hours ahead. Channels are matched by the playlist's
 * guide id, or else by name.
 */
object Guide {
    class Parsed(val programmes: HashMap<String, Array<Programme>>, val keysByChannelId: Map<String, String>)

    /** "BBC One HD", "UK: BBC One FHD" → "bbcone". */
    fun norm(name: String): String {
        var s = name.lowercase()
        s = s.replace(Regex("""^\s*([a-z]{2,4})\s*[|:\-]\s*"""), "")
        s = s.replace(Regex("""\b(uhd|fhd|hd|sd|4k|hevc|h265|1080p|720p|50fps|backup|\+1)\b"""), "")
        return s.filter { it.isLetterOrDigit() }
    }

    fun parse(input: InputStream, channels: List<Channel>, hoursAhead: Int = 14, hoursBack: Int = 1): Parsed {
        val now = System.currentTimeMillis()
        val from = now - hoursBack * 60 * 60_000L
        val until = now + hoursAhead * 60 * 60_000L

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
                        while (inside(p, "channel")) {
                            if (p.eventType == XmlPullParser.START_TAG && p.name == "display-name") names.add(p.nextText())
                        }
                        if (id.isEmpty()) { ev = p.next(); continue }
                        val matched = byId[id] ?: names.firstNotNullOfOrNull { n ->
                            byName[norm(n)]?.filter { it.epgId == null || !byId.containsKey(it.epgId.lowercase()) }?.ifEmpty { null }
                        } ?: byName[norm(id)]
                        if (matched != null) {
                            keyOf[id] = id
                            for (c in matched) if (!keysByChannel.containsKey(c.id)) keysByChannel[c.id] = id
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
                                    "desc" -> if (desc == null) desc = p.nextText().trim().take(400)
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
        return Parsed(out, keysByChannel)
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

    private val utc = TimeZone.getTimeZone("UTC")

    /** "20261112210000 +0000" → milliseconds (0 if it can't be read). */
    fun time(s: String?): Long {
        if (s == null || s.length < 14) return 0
        return try {
            val c = Calendar.getInstance(utc)
            c.clear()
            c.set(s.substring(0, 4).toInt(), s.substring(4, 6).toInt() - 1, s.substring(6, 8).toInt(),
                s.substring(8, 10).toInt(), s.substring(10, 12).toInt(), s.substring(12, 14).toInt())
            var t = c.timeInMillis
            val tz = s.substring(14).trim()
            if (tz.length >= 5 && (tz[0] == '+' || tz[0] == '-')) {
                val mins = tz.substring(1, 3).toInt() * 60 + tz.substring(3, 5).toInt()
                t -= (if (tz[0] == '+') 1 else -1) * mins * 60_000L
            }
            t
        } catch (e: Exception) { 0 }
    }
}
