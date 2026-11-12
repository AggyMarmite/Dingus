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
import java.util.concurrent.Executors

/**
 * Channel logos: downloaded once (kept on the device), shrunk to the size they're shown at, and held in a small
 * memory cache – Fire Sticks don't have memory to spare.
 */
object Logos {
    private val pool = Executors.newFixedThreadPool(3)
    private val main = Handler(Looper.getMainLooper())
    private val cache = object : LruCache<String, Bitmap>(12 * 1024 * 1024) {
        override fun sizeOf(key: String, value: Bitmap) = value.byteCount
    }
    private val failed = HashSet<String>()
    private lateinit var dir: File

    fun init(ctx: Context) { dir = File(ctx.cacheDir, "logos").apply { mkdirs() } }

    private val loading = HashSet<String>()

    /** For views that draw logos themselves: the logo if it's ready, otherwise starts fetching it and calls [ready]. */
    fun get(url: String?, heightPx: Int, ready: () -> Unit): Bitmap? {
        if (url.isNullOrBlank()) return null
        cache.get(url)?.let { return it }
        synchronized(failed) { if (url in failed || !loading.add(url)) return null }
        pool.execute {
            val bmp = try { fetch(url, heightPx) } catch (e: Exception) { null }
            synchronized(failed) { loading.remove(url); if (bmp == null) failed.add(url) }
            if (bmp != null) { cache.put(url, bmp); main.post(ready) }
        }
        return null
    }

    /** Shows the logo in [view]; [fallback] (e.g. the channel's initials) is shown only while there's no logo. */
    fun load(view: ImageView, url: String?, heightPx: Int, fallback: View? = null) {
        view.tag = url
        view.setImageDrawable(null)
        fallback?.visibility = View.VISIBLE
        if (url.isNullOrBlank() || synchronized(failed) { url in failed }) return
        cache.get(url)?.let { view.setImageBitmap(it); fallback?.visibility = View.GONE; return }
        pool.execute {
            val bmp = try { fetch(url, heightPx) } catch (e: Exception) { null }
            if (bmp == null) synchronized(failed) { failed.add(url) } else cache.put(url, bmp)
            main.post {
                if (view.tag == url && bmp != null) { view.setImageBitmap(bmp); fallback?.visibility = View.GONE }
            }
        }
    }

    private fun fetch(url: String, heightPx: Int): Bitmap? {
        val f = File(dir, hash(url))
        if (!f.exists() || f.length() == 0L) {
            val tmp = File(dir, f.name + ".tmp")
            Net.open(url, 10_000).use { i -> tmp.outputStream().use { o -> i.copyTo(o) } }
            tmp.renameTo(f)
        }
        val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(f.path, o)
        if (o.outHeight <= 0) return null
        var sample = 1
        while (o.outHeight / (sample * 2) >= heightPx) sample *= 2
        return BitmapFactory.decodeFile(f.path, BitmapFactory.Options().apply { inSampleSize = sample; inPreferredConfig = Bitmap.Config.ARGB_8888 })
    }

    private fun hash(s: String) = MessageDigest.getInstance("SHA-1").digest(s.toByteArray()).joinToString("") { "%02x".format(it) }
}
