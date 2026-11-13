package app.dingus.tv

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.os.Handler
import android.os.Looper
import org.json.JSONArray
import org.json.JSONObject

/** Programme reminders: a pop-up with "Watch" a minute before the programme starts (while Dingus is open). */
object Reminders {
    class Reminder(val channelId: String, val start: Long, val title: String, val channelName: String)

    private val handler = Handler(Looper.getMainLooper())
    private var current: Activity? = null
    private var showing: AlertDialog? = null
    @Volatile private var cache: List<Reminder>? = null

    fun all(): List<Reminder> = cache ?: load().also { cache = it }

    private fun load(): List<Reminder> = try {
        val a = JSONArray(Store.remindersJson)
        (0 until a.length()).map { a.getJSONObject(it) }.map {
            Reminder(it.getString("c"), it.getLong("s"), it.optString("t"), it.optString("n"))
        }
    } catch (e: Exception) { emptyList() }

    private fun save(list: List<Reminder>) {
        val a = JSONArray()
        list.forEach { a.put(JSONObject().put("c", it.channelId).put("s", it.start).put("t", it.title).put("n", it.channelName)) }
        Store.remindersJson = a.toString()
        cache = list
    }

    fun has(c: Channel, p: Programme) = all().any { it.channelId == c.id && it.start == p.start }

    /** Sets or clears a reminder; true when one is now set. */
    fun toggle(c: Channel, p: Programme): Boolean {
        val list = all()
        val on = !has(c, p)
        save(if (on) list + Reminder(c.id, p.start, p.title, c.name) else list.filterNot { it.channelId == c.id && it.start == p.start })
        return on
    }

    /** Called by every screen as it opens and closes, so the pop-up shows on whatever's in front. */
    fun attach(a: Activity) { current = a; handler.removeCallbacks(tick); handler.post(tick) }
    fun detach(a: Activity) { if (current === a) { current = null; handler.removeCallbacks(tick) } }

    private val tick = object : Runnable {
        override fun run() {
            check()
            handler.postDelayed(this, 15_000)
        }
    }

    private fun check() {
        val a = current ?: return
        if (showing?.isShowing == true || a.isFinishing) return
        val now = System.currentTimeMillis()
        val list = all()
        // Forget reminders for programmes that started more than 30 minutes ago.
        val stale = list.filter { it.start < now - 30 * 60_000L }
        if (stale.isNotEmpty()) save(list - stale.toSet())
        val due = all().filter { it.start - 60_000L <= now }.minByOrNull { it.start } ?: return
        save(all().filterNot { it === due })
        val started = due.start <= now
        Ui.test("reminder ${due.title}")
        showing = Dialogs.builder(a)
            .setTitle(if (started) "Started: ${due.title}" else "Starting soon: ${due.title}")
            .setMessage("${due.channelName}  ·  ${Ui.time(due.start)}")
            .setPositiveButton("Watch") { _, _ ->
                if (Repo.channel(due.channelId) != null) a.startActivity(Intent(a, MainActivity::class.java)
                    .putExtra(MainActivity.PLAY, due.channelId).putExtra(MainActivity.PLAY_CATEGORY, Repo.ALL))
            }
            .setNegativeButton("Dismiss", null)
            .show().also { it.getButton(AlertDialog.BUTTON_POSITIVE)?.requestFocus() }
    }
}
