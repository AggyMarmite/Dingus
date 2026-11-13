package app.dingus.tv

import android.content.Context
import android.util.Log
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small shared bits for the screens. */
object Ui {
    /** Lite mode (Settings): a simpler Home and next to no animation, for when Dingus feels slow. */
    val lite: Boolean get() = Store.homeLite

    /** A card growing when the remote's on it – straight away in lite mode. */
    fun grow(v: android.view.View, has: Boolean, ms: Long = 110) {
        val s = if (has) 1.06f else 1f
        if (lite) { v.animate().cancel(); v.scaleX = s; v.scaleY = s } else v.animate().scaleX(s).scaleY(s).setDuration(ms).start()
    }

    private val hm = SimpleDateFormat("HH:mm", Locale.UK)
    fun time(ms: Long): String = synchronized(hm) { hm.format(Date(ms)) }
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

    fun progress(p: Programme, now: Long = System.currentTimeMillis()): Int {
        val len = (p.stop - p.start).coerceAtLeast(1)
        return (((now - p.start) * 1000) / len).toInt().coerceIn(0, 1000)
    }

    fun initials(name: String): String {
        val words = name.replace(Regex("^[A-Za-z]{2,3}[:|]\\s*"), "").split(' ', '-', '_').filter { it.isNotBlank() }
        return words.take(2).joinToString("") { it.take(1).uppercase() }.ifEmpty { "TV" }
    }

    fun dp(ctx: Context, v: Int) = (v * ctx.resources.displayMetrics.density).toInt()

    /** Lines the emulator self-test looks for in the log (debug builds only). */
    fun test(msg: String) { if (BuildConfig.DEBUG) Log.i("DINGUS-TEST", msg) }
}
