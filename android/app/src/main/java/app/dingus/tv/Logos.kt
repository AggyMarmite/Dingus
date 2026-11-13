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
val Logos = ImageCache("logos", 16, Bitmap.Config.ARGB_8888, threads = 6, exact = true)
/** Film and show artwork: bigger, so stored at half the memory per pixel. */
val Posters = ImageCache("posters", 16, Bitmap.Config.RGB_565, threads = 4)

/**
 * Pictures from the internet: downloaded once (kept on the device), shrunk to the size they're shown at, and held
 * in a small memory cache – Fire Sticks don't have memory to spare.
 */
class ImageCache(private val name: String, megabytes: Int, private val config: Bitmap.Config, threads: Int = 3, private val exact: Boolean = false) {
    /** Newest requests first: what's on screen now loads before what you've already scrolled past. */
    private class Newest : LinkedBlockingDeque<Runnable>() {
        override fun offer(e: Runnable): Boolean = if (e is Later) offerLast(e) else offerFirst(e)
    }
    /** Fetching ahead of time: after everything that's actually wanted on screen. */
    private class Later(val r: Runnable) : Runnable by r
    /** Downloads. */
    private val pool = ThreadPoolExecutor(threads, threads, 30, TimeUnit.SECONDS, Newest())
    /** Pictures already saved on the device: read straight away, never stuck behind downloads. */
    private val disk = ThreadPoolExecutor(2, 2, 30, TimeUnit.SECONDS, Newest())
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

    /** Memory cache key: the same picture can be wanted at different sizes (the guide, the info bar). */
    private fun key(url: String, h: Int) = "$url#$h"

    /** Fetches [url] once however many ask; [wanted] says whether it's still needed when its turn comes. */
    private fun request(url: String, heightPx: Int, wanted: () -> Boolean, later: Boolean = false, done: (Bitmap?) -> Unit) {
        val k = key(url, heightPx)
        synchronized(waiting) {
            waiting[k]?.let { it.add(done); return }
            waiting[k] = mutableListOf(done)
        }
        val job = Runnable {
            val skip = synchronized(waiting) { waiting[k]?.size == 1 } && !wanted()
            if (skip) { synchronized(waiting) { waiting.remove(k) }; return@Runnable } // scrolled away: not now
            val bmp = try { fetch(url, heightPx) } catch (e: Exception) { null }
            if (bmp == null) synchronized(failed) { failed[url] = System.currentTimeMillis() } else cache.put(k, bmp)
            val all = synchronized(waiting) { waiting.remove(k) } ?: return@Runnable
            main.post { all.forEach { it(bmp) } }
        }
        val saved = File(dir, hash(url)).let { it.exists() && it.length() > 0 }
        (if (saved) disk else pool).execute(if (later) Later(job) else job)
    }

    /** For views that draw logos themselves: the logo if it's ready, otherwise starts fetching it and calls [ready]. */
    fun get(url: String?, heightPx: Int, ready: () -> Unit): Bitmap? {
        if (url.isNullOrBlank()) return null
        cache.get(key(url, heightPx))?.let { return it }
        if (hasFailed(url)) return null
        if (synchronized(waiting) { waiting.containsKey(key(url, heightPx)) }) return null
        request(url, heightPx, { true }) { if (it != null) ready() }
        return null
    }

    /** Get these ready (downloaded and in memory) before they're needed, e.g. the guide's logos at start. */
    fun prefetch(urls: List<String?>, heightPx: Int) {
        for (u in urls) {
            if (u.isNullOrBlank() || cache.get(key(u, heightPx)) != null || hasFailed(u)) continue
            if (synchronized(waiting) { waiting.containsKey(key(u, heightPx)) }) continue
            request(u, heightPx, { true }, later = true) {}
        }
    }

    /** Shows the logo in [view]; [fallback] (e.g. the channel's initials) is shown only while there's no logo. */
    fun load(view: ImageView, url: String?, heightPx: Int, fallback: View? = null) {
        view.tag = url
        view.setImageDrawable(null)
        fallback?.visibility = View.VISIBLE
        if (url.isNullOrBlank() || hasFailed(url)) return
        cache.get(key(url, heightPx))?.let { view.setImageBitmap(it); fallback?.visibility = View.GONE; return }
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
        val b = BitmapFactory.decodeFile(f.path, BitmapFactory.Options().apply { inSampleSize = sample; inPreferredConfig = config }) ?: return null
        if (!exact) return b
        // Logos: down to the size they're shown at (no bigger than needed: more stay in memory, so they're there next time).
        val scale = minOf(1f, heightPx.toFloat() / b.height, heightPx * 2.4f / b.width)
        if (scale > 0.85f) return b
        val out = Bitmap.createScaledBitmap(b, maxOf(1, (b.width * scale).toInt()), maxOf(1, (b.height * scale).toInt()), true)
        if (out !== b) b.recycle()
        return out
    }

    private fun hash(s: String) = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
}
