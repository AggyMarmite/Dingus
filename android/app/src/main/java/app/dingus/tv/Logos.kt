package app.dingus.tv

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.util.LruCache
import android.view.View
import android.widget.ImageView
import java.io.File
import java.security.MessageDigest
import java.lang.ref.WeakReference
import java.util.concurrent.LinkedBlockingDeque
import java.util.concurrent.ThreadPoolExecutor
import java.util.concurrent.TimeUnit

/** Channel logos (kept sharp, with transparency). */
val Logos = ImageCache("logos", 12, Bitmap.Config.ARGB_8888)
/** Film and show artwork: bigger, so stored at half the memory per pixel. */
val Posters = ImageCache("posters", 16, Bitmap.Config.RGB_565, threads = 4)

/**
 * Pictures from the internet: downloaded once (kept on the device), shrunk to the size they're shown at, and held
 * in a small memory cache – Fire Sticks don't have memory to spare.
 */
class ImageCache(private val name: String, megabytes: Int, private val config: Bitmap.Config, threads: Int = 3) {
    /** Newest requests first: what's on screen now loads before what you've already scrolled past. */
    private class Newest : LinkedBlockingDeque<Runnable>() {
        override fun offer(e: Runnable): Boolean = offerFirst(e)
    }
    private val pool = ThreadPoolExecutor(threads, threads, 30, TimeUnit.SECONDS, Newest())
    private val main = Handler(Looper.getMainLooper())
    private val cache = object : LruCache<String, Bitmap>(megabytes * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap) = value.byteCount
    }
    /** Pictures that couldn't be had, and when: tried again after a while (the network may have been down). */
    private val failed = HashMap<String, Long>()
    /** Pictures being fetched, with everyone waiting for each (the same poster can be in several rows). */
    private val waiting = HashMap<String, MutableList<(Bitmap?) -> Unit>>()
    private lateinit var dir: File

    fun init(ctx: Context) {
        dir = File(ctx.cacheDir, name).apply { mkdirs() }
        // Keep the saved pictures to a sensible size (oldest go first).
        Thread {
            val files = dir.listFiles() ?: return@Thread
            var total = files.sumOf { it.length() }
            val limit = (if (name == "posters") 200L else 60L) * 1024 * 1024
            if (total > limit) for (f in files.sortedBy { it.lastModified() }) { if (total <= limit * 3 / 4) break; total -= f.length(); f.delete() }
        }.start()
    }

    /** Forget pictures in memory (e.g. when leaving a screen full of posters). */
    fun trim() = cache.trimToSize(cache.maxSize() / 3)

    private fun hasFailed(url: String): Boolean = synchronized(failed) {
        val t = failed[url] ?: return false
        if (System.currentTimeMillis() - t > 10 * 60_000L) { failed.remove(url); false } else true
    }

    /** Fetches [url] once however many ask; [wanted] says whether it's still needed when its turn comes. */
    private fun request(url: String, heightPx: Int, wanted: () -> Boolean, done: (Bitmap?) -> Unit) {
        synchronized(waiting) {
            waiting[url]?.let { it.add(done); return }
            waiting[url] = mutableListOf(done)
        }
        pool.execute {
            val skip = synchronized(waiting) { waiting[url]?.size == 1 } && !wanted()
            if (skip) { synchronized(waiting) { waiting.remove(url) }; return@execute } // scrolled away: not now
            val bmp = try { fetch(url, heightPx) } catch (e: Exception) { null }
            if (bmp == null) synchronized(failed) { failed[url] = System.currentTimeMillis() } else cache.put(url, bmp)
            val all = synchronized(waiting) { waiting.remove(url) } ?: return@execute
            main.post { all.forEach { it(bmp) } }
        }
    }

    /** For views that draw logos themselves: the logo if it's ready, otherwise starts fetching it and calls [ready]. */
    fun get(url: String?, heightPx: Int, ready: () -> Unit): Bitmap? {
        if (url.isNullOrBlank()) return null
        cache.get(url)?.let { return it }
        if (hasFailed(url)) return null
        if (synchronized(waiting) { waiting.containsKey(url) }) return null
        request(url, heightPx, { true }) { if (it != null) ready() }
        return null
    }

    /** Shows the logo in [view]; [fallback] (e.g. the channel's initials) is shown only while there's no logo. */
    fun load(view: ImageView, url: String?, heightPx: Int, fallback: View? = null) {
        view.tag = url
        view.setImageDrawable(null)
        fallback?.visibility = View.VISIBLE
        if (url.isNullOrBlank() || hasFailed(url)) return
        cache.get(url)?.let { view.setImageBitmap(it); fallback?.visibility = View.GONE; return }
        val ref = WeakReference(view)
        val fb = WeakReference(fallback)
        request(url, heightPx, { ref.get()?.tag == url }) { bmp ->
            val v = ref.get()
            if (v != null && v.tag == url && bmp != null) { v.setImageBitmap(bmp); fb.get()?.visibility = View.GONE }
        }
    }

    private fun fetch(url: String, heightPx: Int): Bitmap? {
        val f = File(dir, hash(url))
        if (!f.exists() || f.length() == 0L) {
            val tmp = File(dir, f.name + ".tmp." + Thread.currentThread().id)
            Net.open(url, 10_000).use { i -> tmp.outputStream().use { o -> i.copyTo(o) } }
            tmp.renameTo(f)
        }
        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(f.path, o)
        if (o.outHeight <= 0) return null
        var sample = 1
        while (o.outHeight / (sample * 2) >= heightPx) sample *= 2
        return BitmapFactory.decodeFile(f.path, BitmapFactory.Options().apply { inSampleSize = sample; inPreferredConfig = config })
    }

    private fun hash(s: String) = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
}
