package app.dingus.tv

import android.content.Context
import android.util.Log
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Small shared bits for the screens. */
object Ui {
    private val hm = SimpleDateFormat("HH:mm", Locale.UK)
    fun time(ms: Long): String = synchronized(hm) { hm.format(Date(ms)) }
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
