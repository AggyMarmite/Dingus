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
 * A small pill in the bottom corner, on whichever screen is showing (a coloured icon: green done, grey info, red a
 * problem): one that stays while something is happening
 * ("Updating playlist & TV guide…") and a short one when it's done, which goes by itself.
 */
object Notice {
    private var text: String? = null
    private var working = false
    private var until = 0L
    private var screen: WeakReference<Activity>? = null
    private var view: View? = null
    private val handler = Handler(Looper.getMainLooper())
    private val hide = Runnable { if (!working) { text = null; render() } }

    /** Stays until [done] (or [clear]). */
    fun working(t: String) = post { text = t; working = true; handler.removeCallbacks(hide); render(); Ui.test("notice-working $t") }

    /** Shown for a few seconds. */
    fun done(t: String, ms: Long = 4_000) = post {
        text = t; working = false; until = SystemClock.uptimeMillis() + ms
        handler.removeCallbacks(hide); handler.postDelayed(hide, ms); render(); Ui.test("notice-done $t")
    }

    fun clear() = post { text = null; working = false; handler.removeCallbacks(hide); render() }

    fun attach(a: Activity) { screen = WeakReference(a); view = null; render() }
    fun detach(a: Activity) {
        if (screen?.get() !== a) return
        (view?.parent as? ViewGroup)?.removeView(view)
        view = null; screen = null
    }

    private fun post(f: () -> Unit) { if (Looper.myLooper() == Looper.getMainLooper()) f() else handler.post(f) }

    private fun render() {
        val a = screen?.get() ?: return
        val t = text
        if (t == null || (!working && SystemClock.uptimeMillis() > until)) {
            val old = view ?: return
            view = null
            if (Ui.lite) (old.parent as? ViewGroup)?.removeView(old)
            else old.animate().alpha(0f).setDuration(200).withEndAction { (old.parent as? ViewGroup)?.removeView(old) }.start()
            return
        }
        val fresh = view == null
        val v = view ?: build(a).also { view = it }
        v.animate().cancel(); v.alpha = 1f
        if (fresh && !Ui.lite) { v.translationX = 40 * a.resources.displayMetrics.density; v.alpha = 0f; v.animate().translationX(0f).alpha(1f).setDuration(220).start() }
        v.findViewWithTag<TextView>("text").text = t
        v.findViewWithTag<View>("spin").visibility = if (working) View.VISIBLE else View.GONE
        val icon = v.findViewWithTag<TextView>("icon")
        icon.visibility = if (working) View.GONE else View.VISIBLE
        val problem = Regex("(?i)couldn|can.t|failed|error|not reach|no connection|won.t|expired|offline").containsMatchIn(t)
        val good = Regex("(?i)updated|added|saved|connected|done|set|ready|removed").containsMatchIn(t)
        icon.text = when { problem -> "!"; good -> "✓"; else -> "i" }
        (icon.background as? GradientDrawable)?.setColor(when { problem -> 0xFFEF4444.toInt(); good -> 0xFF22C55E.toInt(); else -> 0x24FFFFFF })
    }

    private fun build(a: Activity): View {
        val d = a.resources.displayMetrics.density
        val box = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding((16 * d).toInt(), (10 * d).toInt(), (18 * d).toInt(), (10 * d).toInt())
            setPadding((10 * d).toInt(), (9 * d).toInt(), (18 * d).toInt(), (9 * d).toInt())
            background = GradientDrawable().apply {
                cornerRadius = 24 * d
                setColor(0xFF121722.toInt())
                setStroke((1 * d).toInt(), 0x14FFFFFF)
            }
            elevation = 16 * d
        }
        box.addView(ProgressBar(a, null, android.R.attr.progressBarStyleSmall).apply {
            tag = "spin"
            layoutParams = LinearLayout.LayoutParams((18 * d).toInt(), (18 * d).toInt()).apply { marginEnd = (10 * d).toInt() }
        })
        box.addView(TextView(a).apply {
            tag = "icon"; gravity = Gravity.CENTER; textSize = 13f; setTextColor(0xFFFFFFFF.toInt())
            setTypeface(null, android.graphics.Typeface.BOLD)
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(0xFF22C55E.toInt()) }
            layoutParams = LinearLayout.LayoutParams((26 * d).toInt(), (26 * d).toInt()).apply { marginEnd = (10 * d).toInt() }
        })
        box.addView(TextView(a).apply { tag = "text"; textSize = 14f; setTypeface(null, android.graphics.Typeface.BOLD); setTextColor(0xFFFFFFFF.toInt()) })
        val root = a.findViewById<ViewGroup>(android.R.id.content)
        root.addView(box, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.BOTTOM or Gravity.END).apply { bottomMargin = (36 * d).toInt(); marginEnd = (40 * d).toInt() })
        return box
    }
}
