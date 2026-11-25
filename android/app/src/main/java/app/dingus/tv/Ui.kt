package app.dingus.tv

import android.content.Context
import android.util.Log
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small shared bits for the screens. */
object Ui {
    /**
     * A description never cut off: once laid out, it shows only as many whole lines as fit above whatever's below it
     * (or the bottom of its box) – fewer when there's no title logo and the name takes two lines, up to [max].
     */
    fun fitLines(t: android.widget.TextView, max: Int) {
        val min = (t.getTag(R.id.heroKicker) as? Int) ?: t.minLines.also { t.setTag(R.id.heroKicker, it) }
        t.minLines = minOf(min, max); t.maxLines = max
        t.setTag(R.id.heroText, max)
        // Again whenever the box is laid out (e.g. the title logo arriving a moment later).
        if (t.getTag(R.id.heroMeta) == null) {
            t.setTag(R.id.heroMeta, true)
            (t.parent as? android.view.View)?.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> fitNow(t) }
        }
        t.post { fitNow(t) }
    }

    private fun fitNow(t: android.widget.TextView) {
        val max = t.getTag(R.id.heroText) as? Int ?: return
        val parent = t.parent as? android.view.ViewGroup ?: return
        var limit = parent.height - parent.paddingBottom
        // Anything under it in the same column (buttons, dots…) keeps its room.
        val i = parent.indexOfChild(t)
        for (j in i + 1 until parent.childCount) parent.getChildAt(j).takeIf { it.visibility != android.view.View.GONE }?.let { limit = minOf(limit, it.top - ((it.layoutParams as? android.view.ViewGroup.MarginLayoutParams)?.topMargin ?: 0)) }
        if (t.lineHeight <= 0 || parent.height <= 0) return
        // Room it has now, counting the lines it's already showing (so it can grow back as well as shrink).
        val room = limit - t.top - t.paddingTop - t.paddingBottom
        val fit = (room / t.lineHeight).coerceIn(1, max)
        if (fit != t.maxLines) t.post { if (t.minLines > fit) t.minLines = fit; t.maxLines = fit }
    }

    /** Lite mode (Settings): a simpler Home and next to no animation, for when Dingus feels slow. */
    val lite: Boolean get() = Store.homeLite

    /** A card growing when the remote's on it – straight away in lite mode. */
    fun grow(v: android.view.View, has: Boolean, ms: Long = 110) {
        val s = if (has) 1.06f else 1f
        if (lite) { v.animate().cancel(); v.scaleX = s; v.scaleY = s } else Motion.lift(v, has)
    }

    private val hm = SimpleDateFormat("HH:mm", Locale.UK)
    private val times = android.util.LruCache<Long, String>(512)
    fun time(ms: Long): String {
        val minute = ms / 60_000L
        times.get(minute)?.let { return it }
        return synchronized(hm) { hm.format(Date(ms)) }.also { times.put(minute, it) }
    }
    private val wd = SimpleDateFormat("EEE d MMM", Locale.UK)
    /** "" for today, otherwise "Tomorrow" or the date, ready to go in front of a time. */
    fun day(ms: Long): String {
        val c = java.util.Calendar.getInstance()
        val today = c.get(java.util.Calendar.DAY_OF_YEAR) to c.get(java.util.Calendar.YEAR)
        c.timeInMillis = ms
        val d = c.get(java.util.Calendar.DAY_OF_YEAR) to c.get(java.util.Calendar.YEAR)
        if (d == today) return ""
        c.add(java.util.Calendar.DAY_OF_YEAR, -1)
        if ((c.get(java.util.Calendar.DAY_OF_YEAR) to c.get(java.util.Calendar.YEAR)) == today) return "Tomorrow   "
        c.add(java.util.Calendar.DAY_OF_YEAR, 2)
        if ((c.get(java.util.Calendar.DAY_OF_YEAR) to c.get(java.util.Calendar.YEAR)) == today) return "Yesterday   "
        return synchronized(wd) { wd.format(Date(ms)) } + "   "
    }

    fun span(p: Programme) = "${time(p.start)} – ${time(p.stop)}"
    /** "11 min left" (or "1 h 5 min left") for what's on now; "starts at 20:00" for one still to come. */
    fun minutesLeft(p: Programme, now: Long = System.currentTimeMillis()): String {
        if (p.start > now) return "starts at ${time(p.start)}"
        val m = ((p.stop - now) / 60_000L).coerceAtLeast(0)
        return if (m >= 60) "${m / 60} h ${m % 60} min left" else "$m min left"
    }

    fun progress(p: Programme, now: Long = System.currentTimeMillis()): Int {
        val len = (p.stop - p.start).coerceAtLeast(1)
        return (((now - p.start) * 1000) / len).toInt().coerceIn(0, 1000)
    }

    fun initials(name: String): String {
        val words = name.replace(Regex("^[A-Za-z]{2,3}[:|]\\s*"), "").split(' ', '-', '_').filter { it.isNotBlank() }
        return words.take(2).joinToString("") { it.take(1).uppercase() }.ifEmpty { "TV" }
    }

    /** A count with thousands separators ("3,140"). */
    fun count(n: Int): String = String.format(java.util.Locale.UK, "%,d", n)

    fun dp(ctx: Context, v: Int) = (v * ctx.resources.displayMetrics.density).toInt()

    /** Lines the emulator self-test looks for in the log (debug builds only). */
    fun test(msg: String) { if (BuildConfig.DEBUG) Log.i("DINGUS-TEST", msg) }
}
