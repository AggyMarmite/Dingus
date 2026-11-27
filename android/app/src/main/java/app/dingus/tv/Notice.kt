package app.dingus.tv

import android.app.Activity
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import java.lang.ref.WeakReference

/**
 * Small frosted notes in the bottom corner, on whichever screen is showing. Each has an icon and colour for its kind
 * (a blue spinner while something's happening, a green tick when it's done, the VPN's shield, an amber "!" for a
 * problem) and a second line when there's more to say ("VPN connected" / "US – Boston"). One that stays while something
 * is happening ("Updating playlist & TV guide…"), short ones that go by themselves; they stack, newest at the bottom.
 */
object Notice {
    private class Note(val text: String, var working: Boolean, var until: Long)
    /** The one that stays while something's happening (replaced by its "done"). */
    private var current: Note? = null
    /** Short notes beside it (the VPN's, which don't end what's happening). */
    private val extras = ArrayList<Note>()
    private var screen: WeakReference<Activity>? = null
    private var stack: LinearLayout? = null
    private val views = HashMap<Note, View>()
    private val handler = Handler(Looper.getMainLooper())
    private val tidy = Runnable { render() }

    /** Stays until [done] (or [clear]). */
    fun working(t: String) = post { current = Note(t, true, Long.MAX_VALUE); render(); Ui.test("notice-working $t") }

    /** Shown for a few seconds. */
    fun done(t: String, ms: Long = 4_000) { Progress.cancelFinish(); show(t, ms) }

    /** An update's "done" note: shown once its last step (films and shows downloading alongside) has finished. */
    fun updateDone(t: String, ms: Long = 4_000) {
        if (Progress.running) { Progress.pendingDone = t to ms; return }
        done(t, ms)
    }

    /** A note beside whatever's happening, without ending it (the VPN connecting or switching while an update runs). */
    fun aside(t: String, ms: Long = 4_000) = post {
        extras.removeAll { it.text == t }
        extras.add(Note(t, false, SystemClock.uptimeMillis() + ms)); while (extras.size > 2) extras.removeAt(0)
        render(); Ui.test("notice-done $t")
    }

    internal fun show(t: String, ms: Long) = post {
        current = Note(t, false, SystemClock.uptimeMillis() + ms)
        render(); Ui.test("notice-done $t")
    }

    fun clear() = post { current = null; render() }

    /** Ends the note that stays, if it's the one starting with [prefix] (the VPN's "connecting…", once it's on). */
    fun endWorking(prefix: String) = post { if (current?.working == true && current?.text?.startsWith(prefix) == true) { current = null; render() } }

    fun attach(a: Activity) { screen = WeakReference(a); stack = null; views.clear(); render() }
    fun detach(a: Activity) {
        if (screen?.get() !== a) return
        (stack?.parent as? ViewGroup)?.removeView(stack)
        stack = null; views.clear(); screen = null
    }

    private fun post(f: () -> Unit) { if (Looper.myLooper() == Looper.getMainLooper()) f() else handler.post(f) }

    private fun render() {
        handler.removeCallbacks(tidy)
        val a = screen?.get() ?: return
        val now = SystemClock.uptimeMillis()
        current?.let { if (!it.working && now > it.until) current = null }
        extras.removeAll { now > it.until }
        val showing = extras + listOfNotNull(current)
        // One "working" note following another ("Updating Live TV…" → "Updating Live TV & TV guide…" →
        // "Saving…"): the same note, its words changed in place. (Each step used to slide a whole new note in while
        // the last slid away, so updating the playlist glitched every time a step started or finished.)
        val cur = current
        if (cur != null && cur.working && views[cur] == null) {
            val old = views.keys.firstOrNull { it.working && it !in showing }
            val v = old?.let { views.remove(it) }
            if (v != null) { retext(v, cur); views[cur] = v }
        }
        // Gone ones fade and slide away.
        for ((n, v) in views.entries.toList()) if (n !in showing) {
            views.remove(n)
            if (Ui.lite) (v.parent as? ViewGroup)?.removeView(v)
            else v.animate().alpha(0f).translationX(24 * a.resources.displayMetrics.density).setDuration(220).setInterpolator(Motion.glide)
                .withEndAction { (v.parent as? ViewGroup)?.removeView(v) }.start()
        }
        if (showing.isEmpty()) return
        val box = stack ?: build(a).also { stack = it }
        for (n in showing) if (views[n] == null) {
            val v = pill(a, n)
            views[n] = v
            box.addView(v)
            if (!Ui.lite) {
                v.translationX = 40 * a.resources.displayMetrics.density; v.alpha = 0f
                v.animate().translationX(0f).alpha(1f).setDuration(480).setInterpolator(Motion.spring).start()
            }
        }
        val next = showing.filter { !it.working }.minOfOrNull { it.until }
        if (next != null) handler.postDelayed(tidy, (next - now).coerceAtLeast(50))
    }

    private enum class Kind { WORKING, DONE, VPN, PROBLEM, INFO }

    private fun kind(n: Note): Kind {
        val t = n.text
        if (n.working) return Kind.WORKING
        if (Regex("(?i)couldn|can.t|failed|error|not reach|no connection|won.t|expired|offline|not connected|dropped").containsMatchIn(t)) return Kind.PROBLEM
        if (t.startsWith("⛨")) return Kind.VPN
        if (Regex("(?i)updated|added|saved|connected|done|set|ready|removed|switched|online").containsMatchIn(t)) return Kind.DONE
        return Kind.INFO
    }

    private fun pill(a: Activity, n: Note): View {
        val d = a.resources.displayMetrics.density
        val k = kind(n)
        // "VPN connected  ·  US – Boston": the first part on top, the rest underneath.
        val clean = n.text.removePrefix("⛨").trim()
        val cut = clean.indexOf("  ·  ")
        val title = if (cut > 0) clean.substring(0, cut) else clean
        val sub = if (cut > 0) clean.substring(cut + 5) else null
        val row = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            setPadding((9 * d).toInt(), (8 * d).toInt(), (20 * d).toInt(), (8 * d).toInt())
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0xF21E2636.toInt(), 0xF5101520.toInt())).apply {
                cornerRadius = 30 * d; setStroke((1 * d).toInt(), 0x1AFFFFFF)
            }
            elevation = 16 * d
        }
        val (fill, ink, glyph) = when (k) {
            Kind.DONE -> Triple(0x3316A34A, 0xFF4ADE80.toInt(), "✓")
            Kind.VPN -> Triple(0x2622C55E, 0xFF4ADE80.toInt(), "⛨")
            Kind.PROBLEM -> Triple(0x33D97706, 0xFFFBBF24.toInt(), "!")
            Kind.INFO -> Triple(0x333B82F6, 0xFF93C5FD.toInt(), "i")
            Kind.WORKING -> Triple(0x333B82F6, 0xFF93C5FD.toInt(), "")
        }
        val icon = FrameLayout(a).apply { background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(fill) } }
        if (k == Kind.WORKING) icon.addView(ProgressBar(a, null, android.R.attr.progressBarStyleSmall).apply {
            indeterminateTintList = android.content.res.ColorStateList.valueOf(ink)
        }, FrameLayout.LayoutParams((16 * d).toInt(), (16 * d).toInt(), Gravity.CENTER))
        else icon.addView(TextView(a).apply { text = glyph; gravity = Gravity.CENTER; textSize = 13f; setTextColor(ink); setTypeface(typeface, android.graphics.Typeface.BOLD) },
            FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        row.addView(icon, LinearLayout.LayoutParams((30 * d).toInt(), (30 * d).toInt()).apply { marginEnd = (11 * d).toInt() })
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        col.addView(TextView(a).apply { text = title; textSize = 14f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFF1F5F9.toInt()); maxLines = 2 })
        if (sub != null) col.addView(TextView(a).apply { text = sub; textSize = 12f; setTextColor(0xFF94A3B8.toInt()); maxLines = 2 })
        row.addView(col)
        row.tag = "text" // (the self-test reads notes by their words)
        return row.also { it.layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = (10 * d).toInt(); gravity = Gravity.END } }
    }

    /** A note's words changed in place (its title, and the line under it after "  ·  "). */
    private fun retext(v: View, n: Note) {
        val col = ((v as? ViewGroup)?.getChildAt(1) as? LinearLayout) ?: return
        val clean = n.text.removePrefix("⛨").trim()
        val cut = clean.indexOf("  ·  ")
        val title = if (cut > 0) clean.substring(0, cut) else clean
        val sub = if (cut > 0) clean.substring(cut + 5) else null
        (col.getChildAt(0) as? TextView)?.text = title
        val subView = col.getChildAt(1) as? TextView
        when {
            sub == null -> if (subView != null) col.removeView(subView)
            subView != null -> subView.text = sub
            else -> col.addView(TextView(v.context).apply { text = sub; textSize = 12f; setTextColor(0xFF94A3B8.toInt()); maxLines = 2 })
        }
        Ui.test("notice-retext ${n.text}")
    }

    private fun build(a: Activity): LinearLayout {
        val d = a.resources.displayMetrics.density
        val box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.END; clipChildren = false; clipToPadding = false }
        val root = a.findViewById<ViewGroup>(android.R.id.content)
        root.addView(box, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.BOTTOM or Gravity.END).apply { bottomMargin = (36 * d).toInt(); marginEnd = (40 * d).toInt() })
        return box
    }
}
