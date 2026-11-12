package app.dingus.tv

import android.os.SystemClock
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.RandomAccessFile
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URL

/**
 * Pause and rewind for live TV. One connection to the provider is saved to small files on the device (the last
 * [maxSeconds] only), and a tiny server on the device plays it to the video player from any point – "live" is just
 * the newest bytes. Uses one provider connection, like watching normally.
 */
class LiveBuffer(private val url: String, root: File, private val maxSeconds: Int) {
    private class Seg(val start: Long, val file: File) { @Volatile var length = 0L }

    private val dir = File(root, "timeshift").apply { deleteRecursively(); mkdirs() }
    private val segs = ArrayList<Seg>()
    private val lock = Object()
    @Volatile private var closed = false
    @Volatile var written = 0L; private set
    @Volatile var failed: Exception? = null; private set
    @Volatile private var firstByteAt = 0L
    private var server: ServerSocket? = null
    private var conn: HttpURLConnection? = null
    @Volatile private var client: Socket? = null

    /** Bytes per second of the stream (0 until a few seconds have been saved). */
    val byteRate: Double get() {
        val t = firstByteAt
        if (t == 0L) return 0.0
        val secs = (SystemClock.elapsedRealtime() - t) / 1000.0
        return if (secs < 4) 0.0 else written / secs
    }

    val oldest: Long get() = synchronized(lock) { segs.firstOrNull()?.start ?: 0L }

    /** Seconds of the programme that can be rewound right now. */
    val secondsAvailable: Double get() { val r = byteRate; return if (r <= 0) 0.0 else (written - oldest) / r }

    fun start(): LiveBuffer {
        val ss = ServerSocket(0, 4, InetAddress.getByName("127.0.0.1"))
        server = ss
        Thread({ record() }, "dingus-record").apply { isDaemon = true; start() }
        Thread({ serve(ss) }, "dingus-serve").apply { isDaemon = true; start() }
        return this
    }

    /** The address the player opens to play from byte [offset] (clamped to what's still kept). */
    fun urlAt(offset: Long): String = "http://127.0.0.1:${server?.localPort ?: 0}/s?o=$offset"

    /** The byte offset [secondsBehind] before live, lined up on a transport-stream packet. */
    fun offsetBehind(secondsBehind: Double): Long {
        val r = byteRate
        val o = if (r <= 0) 0L else written - (secondsBehind * r).toLong()
        val clamped = o.coerceIn(oldest, maxOf(oldest, written))
        return clamped - clamped % 188
    }

    fun close() {
        closed = true
        try { server?.close() } catch (_: Exception) {}
        try { client?.close() } catch (_: Exception) {}
        try { conn?.disconnect() } catch (_: Exception) {}
        synchronized(lock) { (lock as Object).notifyAll() }
        Thread { dir.deleteRecursively() }.start()
    }

    // ------------------------------------------------------------------ saving

    private fun open(): InputStream {
        var u = URL(url)
        repeat(6) {
            val c = (u.openConnection() as HttpURLConnection).apply {
                instanceFollowRedirects = false
                connectTimeout = 12_000; readTimeout = 20_000
                setRequestProperty("User-Agent", Net.USER_AGENT)
            }
            val code = c.responseCode
            if (code in 300..399) { u = URL(u, c.getHeaderField("Location") ?: throw IOException("Bad redirect")); c.disconnect(); return@repeat }
            if (code != 200) throw IOException("HTTP $code")
            conn = c
            return c.inputStream
        }
        throw IOException("Too many redirects")
    }

    private fun record() {
        var attempts = 0
        val buf = ByteArray(64 * 1024)
        while (!closed) {
            try {
                open().use { input ->
                    attempts = 0
                    var seg = newSeg()
                    var out = FileOutputStream(seg.file)
                    try {
                        while (!closed) {
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            if (firstByteAt == 0L) firstByteAt = SystemClock.elapsedRealtime()
                            synchronized(lock) {
                                seg.length += n
                                written += n
                                (lock as Object).notifyAll()
                            }
                            if (seg.length >= SEG_BYTES) {
                                out.close()
                                trim()
                                seg = newSeg()
                                out = FileOutputStream(seg.file)
                            }
                        }
                    } finally { out.close() }
                }
            } catch (e: Exception) {
                if (closed) return
                Log.w("Dingus", "live buffer", e)
                if (++attempts > 3) { failed = e; synchronized(lock) { (lock as Object).notifyAll() }; return }
                SystemClock.sleep(1_000L * attempts)
            }
        }
    }

    private fun newSeg(): Seg = synchronized(lock) {
        Seg(written, File(dir, "${segs.size}_$written.ts")).also { segs.add(it) }
    }

    /** Keep only the last [maxSeconds] (at least a few segments, at most what the storage can spare). */
    private fun trim() {
        val r = byteRate
        val keep = if (r > 0) (r * maxSeconds).toLong() else 64L * 1024 * 1024
        val free = dir.usableSpace
        val limit = minOf(keep, 2L * 1024 * 1024 * 1024, maxOf(32L * 1024 * 1024, free / 2 + (written - oldest)))
        synchronized(lock) {
            while (segs.size > 2 && written - segs[1].start > limit) segs.removeAt(0).file.delete()
        }
    }

    // ------------------------------------------------------------------ playing

    private fun serve(ss: ServerSocket) {
        while (!closed) {
            val s = try { ss.accept() } catch (e: Exception) { return }
            // A new request (a jump back or forward) replaces the old one.
            try { client?.close() } catch (_: Exception) {}
            client = s
            Thread({ answer(s) }, "dingus-play").apply { isDaemon = true; start() }
        }
    }

    private fun answer(s: Socket) {
        try {
            s.soTimeout = 15_000
            val reqLine = s.getInputStream().bufferedReader().readLine() ?: return
            val o = Regex("""[?&]o=(\d+)""").find(reqLine)?.groupValues?.get(1)?.toLongOrNull() ?: 0L
            val out = s.getOutputStream()
            out.write("HTTP/1.1 200 OK\r\nContent-Type: video/mp2t\r\nConnection: close\r\nCache-Control: no-cache\r\n\r\n".toByteArray())
            var pos = maxOf(o, oldest)
            val buf = ByteArray(64 * 1024)
            while (!closed && !s.isClosed) {
                val n = readAt(pos, buf)
                if (n < 0) break
                if (n == 0) continue
                out.write(buf, 0, n)
                pos += n
            }
        } catch (_: Exception) {
        } finally {
            try { s.close() } catch (_: Exception) {}
        }
    }

    /** Reads saved bytes at [pos]: waits for new ones at the live edge; -1 when finished. */
    private fun readAt(pos: Long, buf: ByteArray): Int {
        val seg = synchronized(lock) {
            while (!closed && pos >= written) {
                if (failed != null) return -1
                (lock as Object).wait(500)
            }
            if (closed) return -1
            if (pos < (segs.firstOrNull()?.start ?: 0L)) return -1 // what we were reading has been dropped
            segs.lastOrNull { it.start <= pos } ?: return -1
        }
        val within = pos - seg.start
        val avail = (seg.length - within).toInt()
        if (avail <= 0) return 0
        RandomAccessFile(seg.file, "r").use { f ->
            f.seek(within)
            return f.read(buf, 0, minOf(buf.size, avail))
        }
    }

    companion object {
        private const val SEG_BYTES = 8L * 1024 * 1024
    }
}
