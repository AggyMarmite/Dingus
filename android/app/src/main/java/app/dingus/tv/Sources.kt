package app.dingus.tv

import android.util.JsonReader
import android.util.JsonToken
import java.io.IOException
import java.io.InputStreamReader
import java.net.URLEncoder

/** Reads a playlist's categories and live channels (Xtream Codes login, or an M3U link). */
object Sources {
    /** The account details from the last Xtream login (saved by whoever stores the playlist). */
    @Volatile var accountInfo: org.json.JSONObject? = null

    class Result(val categories: List<Category>, val channels: List<Channel>, val guideUrl: String?)

    fun load(p: Playlist): Result = if (p.type == "xtream") xtream(p) else m3u(p)

    // ------------------------------------------------------------------ Xtream Codes

    fun xtreamBase(server: String): String {
        var s = server.trim().trimEnd('/')
        if (!s.startsWith("http://", true) && !s.startsWith("https://", true)) s = "http://$s"
        return s
    }

    internal fun enc(s: String) = URLEncoder.encode(s, "UTF-8")

    private fun xtream(p: Playlist): Result {
        val base = xtreamBase(p.server)
        val auth = "username=${enc(p.user)}&password=${enc(p.pass)}"
        // The login itself: a wrong one answers with "auth": 0.
        Net.open("$base/player_api.php?$auth").use { s ->
            val r = JsonReader(InputStreamReader(s, Charsets.UTF_8))
            var ok = true
            if (r.peek() == JsonToken.BEGIN_OBJECT) {
                r.beginObject()
                while (r.hasNext()) {
                    when (r.nextName()) {
                        "server_info" -> {
                            r.beginObject()
                            while (r.hasNext()) {
                                if (r.nextName() == "timezone") str(r).takeIf { it.isNotBlank() }?.let { Store.serverTimeZone = it } else r.skipValue()
                            }
                            r.endObject()
                        }
                        "user_info" -> {
                            // Also keeps the expiry date, connections and status for Settings.
                            val info = org.json.JSONObject()
                            r.beginObject()
                            while (r.hasNext()) {
                                when (val n = r.nextName()) {
                                    "auth" -> if (r.peek() != JsonToken.NULL) ok = r.nextString() != "0" else r.skipValue()
                                    "exp_date", "max_connections", "active_cons", "status" -> info.put(n, str(r))
                                    else -> r.skipValue()
                                }
                            }
                            r.endObject()
                            accountInfo = info
                        }
                        else -> r.skipValue()
                    }
                }
            }
            if (!ok) throw IOException("The username or password wasn't accepted")
        }
        val cats = ArrayList<Category>()
        Net.open("$base/player_api.php?$auth&action=get_live_categories").use { s ->
            val r = JsonReader(InputStreamReader(s, Charsets.UTF_8))
            r.beginArray()
            while (r.hasNext()) {
                var id = ""; var name = ""
                r.beginObject()
                while (r.hasNext()) when (r.nextName()) {
                    "category_id" -> id = str(r)
                    "category_name" -> name = str(r)
                    else -> r.skipValue()
                }
                r.endObject()
                if (id.isNotEmpty()) cats.add(Category(id, name.ifEmpty { "Other" }))
            }
            r.endArray()
        }
        val chans = ArrayList<Channel>()
        Net.open("$base/player_api.php?$auth&action=get_live_streams", 30_000).use { s ->
            val r = JsonReader(InputStreamReader(s, Charsets.UTF_8))
            r.beginArray()
            var i = 0
            while (r.hasNext()) {
                var id = ""; var name = ""; var cat = ""; var logo = ""; var epg = ""; var num = 0; var archive = false; var days = 0
                r.beginObject()
                while (r.hasNext()) when (r.nextName()) {
                    "stream_id" -> id = str(r)
                    "name" -> name = str(r)
                    "category_id" -> cat = str(r)
                    "stream_icon" -> logo = str(r)
                    "epg_channel_id" -> epg = str(r)
                    "num" -> num = str(r).toIntOrNull() ?: 0
                    "tv_archive" -> archive = str(r).let { it == "1" || it == "true" }
                    "tv_archive_duration" -> days = str(r).toIntOrNull() ?: 0
                    else -> r.skipValue()
                }
                r.endObject()
                i++
                if (id.isEmpty()) continue
                chans.add(Channel(id, name.trim(), if (num > 0) num else i, cat, logo.ifBlank { null }, epg.ifBlank { null },
                    "$base/live/${enc(p.user)}/${enc(p.pass)}/$id.ts", if (archive) days.coerceIn(1, 14) else 0))
            }
            r.endArray()
        }
        return Result(cats, chans, "$base/xmltv.php?$auth")
    }

    /** A value as text, whatever JSON type it came as (Xtream servers mix numbers, strings and nulls). */
    internal fun str(r: JsonReader): String = when (r.peek()) {
        JsonToken.NULL -> { r.nextNull(); "" }
        JsonToken.BOOLEAN -> r.nextBoolean().toString()
        JsonToken.BEGIN_ARRAY, JsonToken.BEGIN_OBJECT -> { r.skipValue(); "" }
        else -> r.nextString()
    }

    // ------------------------------------------------------------------ M3U

    private val attr = Regex("""([a-zA-Z0-9_-]+)="([^"]*)"""")

    private fun m3u(p: Playlist): Result {
        val cats = LinkedHashMap<String, Category>()
        val chans = ArrayList<Channel>()
        var guide: String? = null
        Net.open(p.m3uUrl.trim(), 30_000).bufferedReader(Charsets.UTF_8).use { rd ->
            var info: String? = null
            var group: String? = null
            var n = 0
            rd.forEachLine { raw ->
                val line = raw.trim()
                when {
                    line.isEmpty() -> {}
                    line.startsWith("#EXTM3U") -> if (guide == null) {
                        val a = attr.findAll(line).associate { it.groupValues[1].lowercase() to it.groupValues[2] }
                        guide = (a["url-tvg"] ?: a["x-tvg-url"])?.split(',')?.firstOrNull()?.trim()?.ifBlank { null }
                    }
                    line.startsWith("#EXTINF") -> info = line
                    line.startsWith("#EXTGRP:") -> group = line.substringAfter(':').trim()
                    line.startsWith("#") -> {}
                    else -> {
                        val inf = info ?: "#EXTINF:-1,$line"
                        val a = attr.findAll(inf).associate { it.groupValues[1].lowercase() to it.groupValues[2] }
                        val name = inf.substringAfterLast("\",", inf.substringAfter(',', "")).trim()
                            .ifEmpty { a["tvg-name"] ?: "Channel" }
                        val g = (a["group-title"] ?: group ?: "Other").ifBlank { "Other" }
                        cats.getOrPut(g) { Category(g, g) }
                        n++
                        val num = a["tvg-chno"]?.toIntOrNull() ?: n
                        chans.add(Channel("m$n", name, num, g, a["tvg-logo"]?.ifBlank { null }, a["tvg-id"]?.ifBlank { null }, line))
                        info = null; group = null
                    }
                }
            }
        }
        if (chans.isEmpty()) throw IOException("That link didn't have any channels in it")
        return Result(cats.values.toList(), chans, guide)
    }
}
