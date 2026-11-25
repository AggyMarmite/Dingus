package app.dingus.tv

import android.app.Activity
import android.app.AlarmManager
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.TextView
import org.json.JSONArray
import org.json.JSONObject

/**
 * Programme reminders, a minute before the programme starts. With Dingus open: a small card in the corner (Watch /
 * Dismiss, gone by itself after 30 seconds). Anywhere else on the Fire TV, even with Dingus closed: the Fire TV's own
 * notification, which opens Dingus on the channel (alarms set for each reminder, set again after a restart).
 */
object Reminders {
    private lateinit var app: Context
    private const val CHANNEL = "reminders"

    /** How long before: Settings → Live TV → "Remind me". */
    private fun lead() = Store.reminderLead * 1000L

    /** The time before, in words for the card ("1 MIN", "30 SECS"). */
    private fun leadWords(): String = Store.reminderLead.let { if (it < 60) "$it SECS" else "${it / 60} MIN" + if (it >= 120) "S" else "" }

    /** The reminder setting changed: set every reminder's alarm again for the new time. */
    fun rescheduleAll() { Thread { all().forEach { cancel(it); schedule(it) } }.start() }

    fun init(ctx: Context) {
        app = ctx.applicationContext
        Thread { all().forEach { schedule(it) } }.start()
        // Series reminders: each time the guide updates, its new showings get reminders too.
        Repo.onChanged { Thread { applySeries() }.start() }
    }
    class Reminder(val channelId: String, val start: Long, val title: String, val channelName: String)

    private val handler = Handler(Looper.getMainLooper())
    private var current: Activity? = null
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
    @Synchronized fun toggle(c: Channel, p: Programme): Boolean {
        val list = all()
        val on = !has(c, p)
        // One episode of a series cancelled: the series mustn't put it back when the guide updates.
        if (!on && isSeries(c, p)) skip(c.id, p.start) else if (on) unskip(c.id, p.start)
        val r = Reminder(c.id, p.start, p.title, c.name)
        save(if (on) list + r else list.filterNot { it.channelId == c.id && it.start == p.start })
        if (on) schedule(r) else cancel(r)
        return on
    }

    // ------------------------------------------------------------------ series reminders

    /** "Every episode": a programme (by its title) on a channel. */
    class Series(val channelId: String, val title: String, val channelName: String)

    @Volatile private var seriesCache: List<Series>? = null

    fun series(): List<Series> = seriesCache ?: try {
        val a = JSONArray(Store.seriesJson)
        (0 until a.length()).map { a.getJSONObject(it) }.map { Series(it.getString("c"), it.getString("t"), it.optString("n")) }
    } catch (e: Exception) { emptyList() }.also { seriesCache = it }

    private fun saveSeries(list: List<Series>) {
        val a = JSONArray()
        list.forEach { a.put(JSONObject().put("c", it.channelId).put("t", it.title).put("n", it.channelName)) }
        Store.seriesJson = a.toString()
        seriesCache = list
    }

    /** The title a series is known by: "New: Emmerdale" and "Emmerdale." are the same programme. */
    private val newRx = Regex("""^(new|live|premiere)\s*[:\-–]\s*""", RegexOption.IGNORE_CASE)
    fun seriesTitle(t: String) = t.trim().replace(newRx, "").trimEnd('.', '…', ' ').lowercase()

    fun isSeries(c: Channel, p: Programme) = series().any { it.channelId == c.id && it.title == seriesTitle(p.title) }

    /** Reminders for every showing of [p] on [c] from now on (and for new ones as the guide updates). */
    @Synchronized fun addSeries(c: Channel, p: Programme) {
        if (!isSeries(c, p)) saveSeries(series() + Series(c.id, seriesTitle(p.title), c.name))
        applySeries()
        Ui.test("series-reminder ${p.title} ${all().count { it.channelId == c.id && seriesTitle(it.title) == seriesTitle(p.title) }}")
    }

    /** Stops the series, and its reminders still to come. */
    @Synchronized fun cancelSeries(channelId: String, title: String) {
        val t = seriesTitle(title)
        saveSeries(series().filterNot { it.channelId == channelId && it.title == t })
        val now = System.currentTimeMillis()
        val (gone, keep) = all().partition { it.channelId == channelId && seriesTitle(it.title) == t && it.start > now }
        save(keep)
        gone.forEach { cancel(it) }
    }

    /** Episodes of a series you cancelled one by one ("channel|start"), so the series leaves them alone. */
    private fun skips(): MutableSet<String> = try { Store.prefsString("seriesSkip", "").split('\n').filter { it.isNotBlank() }.toMutableSet() } catch (_: Exception) { HashSet() }
    private fun saveSkips(s: Set<String>) {
        val now = System.currentTimeMillis()
        Store.putString("seriesSkip", s.filter { (it.substringAfter('|').toLongOrNull() ?: 0) > now }.joinToString("\n")) // (past ones go)
    }
    private fun skipped(channelId: String, start: Long) = "$channelId|$start" in skips()
    private fun skip(channelId: String, start: Long) = saveSkips(skips() + "$channelId|$start")
    private fun unskip(channelId: String, start: Long) = saveSkips(skips() - "$channelId|$start")

    /** Adds reminders for the series' showings in the guide that don't have one yet. */
    @Synchronized private fun applySeries() {
        val rules = series()
        if (rules.isEmpty()) return
        val now = System.currentTimeMillis()
        val have = all().map { it.channelId to it.start }.toHashSet()
        val added = ArrayList<Reminder>()
        for (r in rules) {
            val c = Repo.channel(r.channelId) ?: continue
            for (p in Repo.programmes(c)) {
                // (Not one about to start: its reminder may just have been shown and taken off the list.)
                if (p.start <= now + lead() + 60_000 || seriesTitle(p.title) != r.title || (c.id to p.start) in have || skipped(c.id, p.start)) continue
                added.add(Reminder(c.id, p.start, p.title, c.name))
            }
        }
        if (added.isEmpty()) return
        // Old reminders that have passed go at the same time, so the list doesn't grow for ever.
        save(all().filter { it.start > now - 6 * 3_600_000L } + added)
        added.forEach { schedule(it) }
    }

    /**
     * OK on a programme still to come: remind me (this one, or every episode), cancel, or watch the channel now.
     * [done] gets a line for the screen to show ("Reminder set for …").
     */
    fun ask(a: Activity, c: Channel, p: Programme, watch: () -> Unit, done: (String) -> Unit) {
        val items = ArrayList<Pair<String, () -> Unit>>()
        val set = has(c, p)
        val isSeries = isSeries(c, p)
        if (!set) {
            items.add("Remind me  ·  this episode" to { toggle(c, p); Ui.test("reminder-set true ${p.title}"); done("Reminder set for ${p.title}") })
            items.add("⟳  Remind me  ·  every episode" to { addSeries(c, p); done("Series reminder set: every ${p.title} on ${c.name}") })
        } else {
            items.add("Cancel this reminder" to { toggle(c, p); Ui.test("reminder-set false ${p.title}"); done("Reminder cancelled") })
            if (!isSeries) items.add("⟳  Remind me  ·  every episode" to { addSeries(c, p); done("Series reminder set: every ${p.title} on ${c.name}") })
        }
        if (isSeries) items.add("Cancel series reminders" to { cancelSeries(c.id, p.title); Ui.test("series-cancelled ${p.title}"); done("Series reminders cancelled") })
        items.add("Watch ${c.name} now" to watch)
        Dialogs.builder(a).setTitle("${p.title}  ·  ${Ui.day(p.start)}${Ui.span(p)}")
            .setItems(items.map { it.first }.toTypedArray()) { _, i -> items[i].second() }
            .show()
        Ui.test("reminder-menu ${p.title}")
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

    /** (The self-test: a reminder for the first channel, starting in 40 seconds, so its card shows now.) */
    fun testSoon() {
        val c = Repo.channels.firstOrNull() ?: return
        save(all() + Reminder(c.id, System.currentTimeMillis() + 40_000L, "Reminder test", c.name))
        handler.postDelayed({ check() }, 1500)
    }

    @Synchronized private fun check() {
        val a = current ?: return
        if (card != null || a.isFinishing) return
        val now = System.currentTimeMillis()
        val list = all()
        // Forget reminders for programmes that started more than 30 minutes ago.
        val stale = list.filter { it.start < now - 30 * 60_000L }
        if (stale.isNotEmpty()) save(list - stale.toSet())
        val due = all().filter { it.start - lead() <= now }.minByOrNull { it.start } ?: return
        save(all().filterNot { it === due })
        cancel(due)
        showCard(a, due)
    }

    // ------------------------------------------------------------------ the card (Dingus open)

    private var card: View? = null
    private val hideCard = Runnable { dismissCard() }

    private fun showCard(a: Activity, r: Reminder) {
        Ui.test("reminder ${r.title}")
        val root = a.findViewById<FrameLayout>(android.R.id.content)
        val v = LayoutInflater.from(a).inflate(R.layout.reminder_card, root, false)
        val started = r.start <= System.currentTimeMillis()
        val auto = Store.reminderSwitch && Repo.channel(r.channelId) != null
        v.findViewById<TextView>(R.id.remWhen).text = when {
            started -> "STARTED"
            auto -> "SWITCHING IN ${leadWords()}"
            else -> "STARTING IN ${leadWords()}"
        }
        v.findViewById<TextView>(R.id.remTitle).text = r.title
        v.findViewById<TextView>(R.id.remChannel).text = "${r.channelName}  ·  ${Ui.time(r.start)}"
        val ch = Repo.channel(r.channelId)
        Logos.load(v.findViewById(R.id.remLogo), ch?.logo, Ui.dp(a, 36), v.findViewById<TextView>(R.id.remInitials).apply { text = Ui.initials(r.channelName) })
        val watch = v.findViewById<View>(R.id.remWatch)
        val dismiss = v.findViewById<View>(R.id.remDismiss)
        watch.setOnClickListener {
            dismissCard()
            if (Repo.channel(r.channelId) != null) a.startActivity(Intent(a, MainActivity::class.java)
                .putExtra(MainActivity.PLAY, r.channelId).putExtra(MainActivity.PLAY_CATEGORY, Repo.ALL))
        }
        dismiss.setOnClickListener { dismissCard() }
        val back = View.OnKeyListener { _, code, e ->
            if (code == KeyEvent.KEYCODE_BACK) { if (e.action == KeyEvent.ACTION_UP) dismissCard(); true } else false
        }
        watch.setOnKeyListener(back); dismiss.setOnKeyListener(back)
        root.addView(v, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM or Gravity.END).apply {
            val m = Ui.dp(a, 24); setMargins(m, m, m, m)
        })
        card = v
        returnFocus = a.currentFocus
        watch.requestFocus()
        if (!Ui.lite) { v.translationY = Ui.dp(a, 40).toFloat(); v.alpha = 0f; v.animate().translationY(0f).alpha(1f).setDuration(220).start() }
        handler.removeCallbacks(hideCard)
        handler.removeCallbacks(autoSwitch)
        if (auto) {
            // "Switch for reminders" (Settings): nothing pressed by the start → over to the channel. Dismiss stays put.
            pendingSwitch = { if (card === v) watch.performClick() }
            handler.postDelayed(autoSwitch, (r.start - System.currentTimeMillis()).coerceAtLeast(0L))
        } else handler.postDelayed(hideCard, 30_000)
    }

    private var pendingSwitch: (() -> Unit)? = null
    private val autoSwitch = Runnable { pendingSwitch?.invoke(); pendingSwitch = null; Ui.test("reminder-switched") }

    private var returnFocus: View? = null

    private fun dismissCard() {
        handler.removeCallbacks(hideCard)
        handler.removeCallbacks(autoSwitch); pendingSwitch = null
        val v = card ?: return
        card = null
        val hadFocus = v.hasFocus()
        val done = { (v.parent as? ViewGroup)?.removeView(v); if (hadFocus) returnFocus?.takeIf { it.isAttachedToWindow }?.requestFocus(); returnFocus = null }
        if (Ui.lite) done() else v.animate().translationY(v.height / 2f).alpha(0f).setDuration(180).withEndAction(done).start()
    }

    // ------------------------------------------------------------------ alarms and the Fire TV notification

    private fun pending(r: Reminder, flags: Int): PendingIntent? {
        val i = Intent(app, ReminderReceiver::class.java).setAction("app.dingus.tv.REMINDER")
            .putExtra("c", r.channelId).putExtra("s", r.start)
        return PendingIntent.getBroadcast(app, (r.channelId + r.start).hashCode(), i, flags or PendingIntent.FLAG_IMMUTABLE)
    }

    private fun schedule(r: Reminder) {
        if (!::app.isInitialized) return
        val at = r.start - lead()
        if (r.start < System.currentTimeMillis()) return
        val am = app.getSystemService(Context.ALARM_SERVICE) as AlarmManager
        val pi = pending(r, PendingIntent.FLAG_UPDATE_CURRENT) ?: return
        try {
            if (Build.VERSION.SDK_INT >= 31 && !am.canScheduleExactAlarms()) am.setWindow(AlarmManager.RTC_WAKEUP, at, 60_000L, pi)
            else if (Build.VERSION.SDK_INT >= 23) am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, at, pi)
            else am.setExact(AlarmManager.RTC_WAKEUP, at, pi)
        } catch (_: Exception) { am.set(AlarmManager.RTC_WAKEUP, at, pi) }
    }

    private fun cancel(r: Reminder) {
        if (!::app.isInitialized) return
        val pi = pending(r, PendingIntent.FLAG_NO_CREATE) ?: return
        (app.getSystemService(Context.ALARM_SERVICE) as AlarmManager).cancel(pi)
    }

    /** An alarm went off: the card if Dingus is in front, the Fire TV's notification if not. */
    @Synchronized internal fun fired(ctx: Context, channelId: String, start: Long) {
        if (!::app.isInitialized) init(ctx)
        if (current != null) { handler.post { check() }; return }
        val r = all().firstOrNull { it.channelId == channelId && it.start == start } ?: return
        save(all().filterNot { it === r })
        notify(r)
    }

    private fun notify(r: Reminder) {
        val nm = app.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (Build.VERSION.SDK_INT >= 26 && nm.getNotificationChannel(CHANNEL) == null)
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Programme reminders", NotificationManager.IMPORTANCE_HIGH))
        val open = Intent(app, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            .putExtra(MainActivity.PLAY, r.channelId).putExtra(MainActivity.PLAY_CATEGORY, Repo.ALL)
        val pi = PendingIntent.getActivity(app, (r.channelId + r.start).hashCode(), open, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val n = androidx.core.app.NotificationCompat.Builder(app, CHANNEL)
            .setSmallIcon(R.drawable.ic_live_tv)
            .setContentTitle("Starting soon: ${r.title}")
            .setContentText("${r.channelName}  ·  ${Ui.time(r.start)} – watch in Dingus")
            .setPriority(androidx.core.app.NotificationCompat.PRIORITY_HIGH)
            .setCategory(androidx.core.app.NotificationCompat.CATEGORY_REMINDER)
            .setContentIntent(pi)
            .addAction(0, "Watch now", pi)
            .setAutoCancel(true)
            .build()
        try { nm.notify((r.channelId + r.start).hashCode(), n) } catch (_: SecurityException) {}
    }
}

/** A reminder's alarm. */
class ReminderReceiver : BroadcastReceiver() {
    override fun onReceive(ctx: Context, i: Intent) {
        Reminders.fired(ctx, i.getStringExtra("c") ?: return, i.getLongExtra("s", 0L))
    }
}

/** After the Fire TV restarts: set the reminders' alarms again. */
class ReminderBoot : BroadcastReceiver() {
    override fun onReceive(ctx: Context, i: Intent) { Reminders.init(ctx) }
}
