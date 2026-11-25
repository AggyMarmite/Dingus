package app.dingus.tv

import java.io.IOException

/**
 * Backup server addresses for a playlist. When the playlist refreshes it tries the main address first, then each
 * backup in order, and uses the first that answers; streams then play from that one until the next refresh (which
 * tries the main address first again, so it goes back by itself). The same login is used on every address; for an
 * M3U playlist the backups are alternative links.
 */
object Failover {
    /** The main address, then the backups. */
    fun addresses(p: Playlist): List<String> = listOf(if (p.type == "xtream") p.server else p.m3uUrl) + p.backupUrls

    fun hasBackups(p: Playlist? = Store.playlist): Boolean = p != null && p.backupUrls.isNotEmpty()

    /** Which address is in use: 0 = the main one. */
    fun index(p: Playlist): Int {
        val i = Store.prefsString("failover:${p.id}", "0").toIntOrNull() ?: 0
        return if (i in 0 until addresses(p).size) i else 0
    }

    private fun setIndex(p: Playlist, i: Int) { if (index(p) != i) Store.putString("failover:${p.id}", i.toString()) }

    /** The address in use now. */
    fun active(p: Playlist): String = addresses(p)[index(p)]

    /** An Xtream login's base address (the one in use), for stream and API addresses. */
    fun base(p: Playlist): String = Sources.xtreamBase(active(p))

    /** "Using backup server 1" when a backup is in use, else null. */
    fun status(p: Playlist): String? = index(p).takeIf { it > 0 }?.let { "Using backup server $it" }

    /**
     * Stream addresses saved before a switch still carry the old server: this swaps in the one in use (so the saved
     * lists keep working the moment a backup takes over, before the next refresh).
     */
    fun rewrite(url: String, p: Playlist? = Store.playlist): String {
        if (p == null || p.type != "xtream" || p.backupUrls.isEmpty()) return url
        val now = base(p)
        for (a in addresses(p)) {
            val b = Sources.xtreamBase(a)
            if (b != now && url.startsWith("$b/", true)) return now + url.substring(b.length)
        }
        return url
    }

    /** Whether the error means the server can't be reached (as opposed to a wrong login or a bad playlist). */
    fun isDown(e: Throwable): Boolean {
        if (e is java.net.UnknownHostException || e is java.net.SocketTimeoutException || e is java.net.ConnectException ||
            e is java.net.NoRouteToHostException || e is javax.net.ssl.SSLException || e is java.net.SocketException || e is java.io.EOFException) return true
        val m = e.message ?: return false
        return e is IOException && Regex("""answered (5\d\d|404)""").containsMatchIn(m)
    }

    private fun wrongLogin(e: Throwable) = e.message?.contains("wasn't accepted") == true

    /** A quick "is anyone there?": any answer counts, even a refused login (that's the login's problem, not the server's). */
    private fun reachable(p: Playlist, address: String): Boolean = try {
        val url = if (p.type == "xtream") "${Sources.xtreamBase(address)}/player_api.php?username=${Sources.enc(p.user)}&password=${Sources.enc(p.pass)}" else address.trim()
        Net.open(url, 6_000).close()
        true
    } catch (e: Exception) { !isDown(e) }

    /** [load] with the main address, then each backup (only skipping ahead when the server can't be reached). */
    fun <T> load(p: Playlist, load: (Playlist) -> T): T {
        val all = addresses(p)
        if (all.size == 1) return load(p)
        var first: Exception? = null
        for ((i, a) in all.withIndex()) {
            if (a.isBlank()) continue
            if (!reachable(p, a)) { if (first == null) first = IOException("Couldn't connect to the server"); continue }
            val q = if (p.type == "xtream") p.copy(server = a) else p.copy(m3uUrl = a)
            try {
                val r = load(q)
                setIndex(p, i)
                Ui.test("server-in-use $i")
                return r
            } catch (e: Exception) {
                // Only a refused login stops here (the same login is used on every address). Anything else – a "not
                // found", a half-answered list, something garbled – means try the next address: a server can answer
                // its login yet fail the channel list, and the backup then works (it was showing "Something went
                // wrong talking to the server" or "The server answered 404" while a backup was fine).
                if (wrongLogin(e)) throw e
                android.util.Log.w("Dingus", "server ${i}: ${e.message}", e)
                if (first == null) first = e
            }
        }
        throw first ?: IOException("Couldn't connect to the server")
    }

    /**
     * A stream won't start because the server is unreachable: switch to the next address that answers. Returns
     * whether it switched (call off the main thread).
     */
    fun recover(): Boolean {
        val p = Store.playlist ?: return false
        val all = addresses(p)
        if (all.size < 2) return false
        val cur = index(p)
        for (step in 1 until all.size) {
            val i = (cur + step) % all.size
            if (all[i].isNotBlank() && reachable(p, all[i])) { setIndex(p, i); Ui.test("failover to ${i}"); return true }
        }
        return false
    }
}
