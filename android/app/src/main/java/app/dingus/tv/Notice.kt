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
 * A small note in the top corner, on whichever screen is showing: one that stays while something is happening
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
            old.animate().alpha(0f).setDuration(200).withEndAction { (old.parent as? ViewGroup)?.removeView(old) }.start()
            return
        }
        val v = view ?: build(a).also { view = it }
        v.animate().cancel(); v.alpha = 1f
        v.findViewWithTag<TextView>("text").text = t
        v.findViewWithTag<View>("spin").visibility = if (working) View.VISIBLE else View.GONE
    }

    private fun build(a: Activity): View {
        val d = a.resources.displayMetrics.density
        val box = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding((16 * d).toInt(), (10 * d).toInt(), (18 * d).toInt(), (10 * d).toInt())
            background = GradientDrawable().apply {
                cornerRadius = 22 * d
                setColor(Themes.color(a, R.attr.dSurface2))
                setStroke((1 * d).toInt(), Themes.color(a, R.attr.dLine))
            }
            elevation = 16 * d
        }
        box.addView(ProgressBar(a, null, android.R.attr.progressBarStyleSmall).apply {
            tag = "spin"
            layoutParams = LinearLayout.LayoutParams((18 * d).toInt(), (18 * d).toInt()).apply { marginEnd = (10 * d).toInt() }
        })
        box.addView(TextView(a).apply { tag = "text"; textSize = 15f; setTextColor(Themes.color(a, R.attr.dText)) })
        val root = a.findViewById<ViewGroup>(android.R.id.content)
        root.addView(box, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT,
            Gravity.TOP or Gravity.END).apply { topMargin = (70 * d).toInt(); marginEnd = (40 * d).toInt() })
        return box
    }
}
