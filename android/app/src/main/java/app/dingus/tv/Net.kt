package app.dingus.tv

import java.io.BufferedInputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.zip.GZIPInputStream

/** Plain web requests, the way IPTV servers expect them (they often only answer a media player's name). */
object Net {
    private const val VLC = "VLC/3.0.21 LibVLC/3.0.21"
    private val OWN = "Dingus/${BuildConfig.VERSION_NAME} (Android TV)"
    /**
     * The name Dingus gives servers. VLC's by default (widely recognised), but some providers turn VLC away – often
     * with "not found" – so when a playlist request is refused under VLC's name it's tried once under Dingus's own,
     * and if that works, Dingus's own name is used from then on (remembered).
     */
    val USER_AGENT: String get() = if (Store.prefsString("ownUserAgent", "0") == "1") OWN else VLC
    private fun useOwn() { if (Store.prefsString("ownUserAgent", "0") != "1") { Store.putString("ownUserAgent", "1"); android.util.Log.i("Dingus", "using Dingus's own name with servers") } }

    /** Opens an address for reading (following redirects, unpacking gzip). The caller closes it. */
    fun open(url: String, timeoutMs: Int = 20_000): InputStream = try { openAs(url, timeoutMs, USER_AGENT) } catch (e: IOException) {
        // Refused under VLC's name on a playlist request: once more under Dingus's own.
        val refused = Regex("""answered 404|refused the login""").containsMatchIn(e.message ?: "")
        if (refused && USER_AGENT == VLC && Regex("""(player_api|get|xmltv|panel_api)\.php""").containsMatchIn(url)) {
            val r = openAs(url, timeoutMs, OWN); useOwn(); r
        } else throw e
    }

    private fun openAs(url: String, timeoutMs: Int, agent: String): InputStream {
        var u = URL(url)
        repeat(6) {
            val c = (u.openConnection() as HttpURLConnection).apply {
                connectTimeout = timeoutMs
                readTimeout = 60_000
                instanceFollowRedirects = false
                setRequestProperty("User-Agent", agent)
                setRequestProperty("Accept-Encoding", "gzip")
            }
            val code = c.responseCode
            if (code in 300..399) {
                val loc = c.getHeaderField("Location") ?: throw IOException("Redirect without an address")
                u = URL(u, loc)
                c.disconnect()
                return@repeat
            }
            if (code == 401 || code == 403) throw IOException("The server refused the login ($code)")
            if (code !in 200..299) throw IOException("The server answered $code")
            val raw = BufferedInputStream(c.inputStream, 1 shl 16)
            return unpack(raw, c.contentEncoding)
        }
        throw IOException("Too many redirects")
    }

    /** Gzip by header or by its first bytes (guide files are often .gz without saying so). */
    private fun unpack(s: BufferedInputStream, encoding: String?): InputStream {
        if (encoding.equals("gzip", true)) return BufferedInputStream(GZIPInputStream(s, 1 shl 16), 1 shl 16)
        s.mark(2)
        val a = s.read(); val b = s.read()
        s.reset()
        return if (a == 0x1f && b == 0x8b) BufferedInputStream(GZIPInputStream(s, 1 shl 16), 1 shl 16) else s
    }

    fun friendly(e: Throwable): String = when (e) {
        is java.net.UnknownHostException -> "Couldn't find the server – check the address and your connection"
        is java.net.SocketTimeoutException -> "The server didn't answer in time"
        is java.net.ConnectException -> "Couldn't connect to the server"
        is javax.net.ssl.SSLException -> "A secure connection to the server failed"
        is org.json.JSONException -> "The server sent something unexpected – check the address"
        is java.io.IOException -> e.message?.takeIf { it.length < 80 && !it.contains("Exception") } ?: "Couldn't talk to the server"
        else -> "Something went wrong talking to the server"
    }
}
