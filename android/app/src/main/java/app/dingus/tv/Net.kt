package app.dingus.tv

import java.io.BufferedInputStream
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.zip.GZIPInputStream

/** Plain web requests, the way IPTV servers expect them (they often only answer a media player's name). */
object Net {
    /** The name the video player gives (VLC's: streams play under it fine). */
    const val USER_AGENT = "VLC/3.0.21 LibVLC/3.0.21"
    /**
     * The name for the playlist, guide and films lists – the same kind the Windows app has always used without trouble
     * (some providers answer "not found" to playlist requests that say they're VLC). If a server refuses it, VLC's is
     * tried once.
     */
    private val API = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Dingus/0.2"

    fun open(url: String, timeoutMs: Int = 20_000): InputStream = try {
        openAs(url, timeoutMs, API)
    } catch (e: IOException) {
        val refused = Regex("""answered 404|refused the login""").containsMatchIn(e.message ?: "")
        if (refused && Regex("""(player_api|get|xmltv|panel_api)\.php""").containsMatchIn(url)) openAs(url, timeoutMs, USER_AGENT) else throw e
    }

    fun openAs(url: String, timeoutMs: Int, agent: String): InputStream {
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
