package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.URI

/** Your subscription as the provider reports it: expiry date, connections, status (Xtream panels). */
object Account {
    class Info(val expires: Long, val maxConnections: Int, val activeConnections: Int, val status: String, val checkedAt: Long) {
        /** "Expires 12 Mar 2027 (in 119 days)  ·  2 connections  ·  Active" */
        fun summary(): String {
            val parts = ArrayList<String>()
            parts.add(when {
                expires <= 0 -> "No expiry date"
                expires < System.currentTimeMillis() -> "Expired on ${date(expires)}"
                else -> "Expires ${date(expires)} (in ${((expires - System.currentTimeMillis()) / 86_400_000L) + 1} days)"
            })
            if (maxConnections > 0) parts.add("$maxConnections connection${if (maxConnections == 1) "" else "s"}")
            if (status.isNotBlank()) parts.add(status.replaceFirstChar { it.uppercase() })
            return parts.joinToString("  ·  ")
        }
    }

    private fun date(ms: Long) = java.text.SimpleDateFormat("d MMM yyyy", java.util.Locale.UK).format(java.util.Date(ms))

    fun saved(): Info? = try {
        val o = JSONObject(Store.accountJson.ifEmpty { return null })
        Info(o.optLong("e"), o.optInt("m"), o.optInt("a"), o.optString("s"), o.optLong("t"))
    } catch (e: Exception) { null }

    /** From the "user_info" part of an Xtream login reply. */
    fun remember(userInfo: JSONObject) {
        val exp = userInfo.optString("exp_date").toLongOrNull()?.let { it * 1000 } ?: 0L
        val o = JSONObject().put("e", exp)
            .put("m", userInfo.optString("max_connections").toIntOrNull() ?: 0)
            .put("a", userInfo.optString("active_cons").toIntOrNull() ?: 0)
            .put("s", userInfo.optString("status"))
            .put("t", System.currentTimeMillis())
        Store.accountJson = o.toString()
    }

    /** The panel address and login for a playlist: an Xtream login, or an M3U link from an Xtream panel (get.php?username=…&password=…). */
    private fun login(p: Playlist): Triple<String, String, String>? {
        if (p.type == "xtream") return Triple(Sources.xtreamBase(p.server), p.user, p.pass)
        return try {
            val u = URI(p.m3uUrl.trim())
            val q = (u.rawQuery ?: return null).split('&').associate { it.substringBefore('=') to java.net.URLDecoder.decode(it.substringAfter('=', ""), "UTF-8") }
            val user = q["username"] ?: return null
            val pass = q["password"] ?: return null
            Triple("${u.scheme}://${u.authority}", user, pass)
        } catch (e: Exception) { null }
    }

    /** Asks the provider again. Null when the playlist doesn't come from an Xtream panel. */
    suspend fun refresh(p: Playlist): Info? = withContext(Dispatchers.IO) {
        val (base, user, pass) = login(p) ?: return@withContext null
        val text = Net.open("$base/player_api.php?username=${Sources.enc(user)}&password=${Sources.enc(pass)}", 15_000).use { it.readBytes().toString(Charsets.UTF_8) }
        val info = JSONObject(text).optJSONObject("user_info") ?: return@withContext null
        remember(info)
        saved()
    }
}
