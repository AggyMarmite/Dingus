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

    fun init(ctx: Context) {
        app = ctx.applicationContext
        Thread { all().forEach { schedule(it) } }.start()
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
    fun toggle(c: Channel, p: Programme): Boolean {
        val list = all()
        val on = !has(c, p)
        val r = Reminder(c.id, p.start, p.title, c.name)
        save(if (on) list + r else list.filterNot { it.channelId == c.id && it.start == p.start })
        if (on) schedule(r) else cancel(r)
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

    /** (The self-test: a reminder for the first channel, starting in 40 seconds, so its card shows now.) */
    fun testSoon() {
        val c = Repo.channels.firstOrNull() ?: return
        save(all() + Reminder(c.id, System.currentTimeMillis() + 40_000L, "Reminder test", c.name))
        handler.postDelayed({ check() }, 1500)
    }

    private fun check() {
        val a = current ?: return
        if (card != null || a.isFinishing) return
        val now = System.currentTimeMillis()
        val list = all()
        // Forget reminders for programmes that started more than 30 minutes ago.
        val stale = list.filter { it.start < now - 30 * 60_000L }
        if (stale.isNotEmpty()) save(list - stale.toSet())
        val due = all().filter { it.start - 60_000L <= now }.minByOrNull { it.start } ?: return
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
            auto -> "SWITCHING IN 1 MIN"
            else -> "STARTING IN 1 MIN"
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
        val at = r.start - 60_000L
        if (at < System.currentTimeMillis() - 60_000L) return
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
    internal fun fired(ctx: Context, channelId: String, start: Long) {
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
