package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
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
            if (maxConnections > 0) parts.add("Active connections $activeConnections/$maxConnections")
            if (status.isNotBlank()) parts.add(status.replaceFirstChar { it.uppercase() })
            return parts.joinToString("  ·  ")
        }
    }

    /** Whether the provider's server answered the last time it was asked (per playlist; null = not asked yet). */
    @Volatile private var online: Pair<String, Boolean>? = null
    @Volatile var lastCheck = 0L; private set
    fun online(p: Playlist?): Boolean? = online?.takeIf { it.first == p?.id }?.second

    /** For the menu, one per line: expiry, connections in use, and whether the server's online. */
    fun lines(i: Info?, p: Playlist?): List<Pair<String, Int>> {
        val out = ArrayList<Pair<String, Int>>()
        if (i != null) {
            out.add(when {
                i.expires <= 0 -> "No expiry date"
                i.expires < System.currentTimeMillis() -> "Expired ${date(i.expires)}"
                else -> "Expires ${date(i.expires)}"
            } to 0)
            if (i.maxConnections > 0) out.add("Active connections ${i.activeConnections}/${i.maxConnections}" to 0)
        }
        out.add(when (online(p)) {
            true -> "Status  ● Online" to 0xFF4ADE80.toInt()
            false -> "Status  ● Offline" to 0xFFF87171.toInt()
            null -> "Status  checking…" to 0
        })
        return out
    }

    /** Asks the provider (subscription, connections in use) – which also says whether the server's online. */
    suspend fun check(p: Playlist): Info? {
        lastCheck = System.currentTimeMillis()
        return try {
            val info = if (login(p) != null) refresh(p) else { ping(p); saved() }
            online = p.id to true
            Ui.test("account ${info?.activeConnections}/${info?.maxConnections} online")
            info
        } catch (e: Exception) {
            online = p.id to false
            Ui.test("account offline ${e.message}")
            null
        }
    }

    /** Something started or stopped playing: ask again in a few seconds, so "Active connections" keeps up. */
    fun soon() {
        val p = Store.playlist ?: return
        Background.scope.launch {
            kotlinx.coroutines.delay(5_000)
            if (System.currentTimeMillis() - lastCheck > 4_000) check(p)
        }
    }

    /** A playlist that isn't from an Xtream panel: is its address answering? */
    private suspend fun ping(p: Playlist) = withContext(Dispatchers.IO) {
        Net.open(p.m3uUrl.trim(), 10_000).use { it.read() }
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
