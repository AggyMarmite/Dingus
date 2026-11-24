package app.dingus.tv

import android.app.Activity
import android.graphics.drawable.GradientDrawable
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView

/**
 * The mini guide over the bottom of full-screen live TV (▼): five channels with what's on now (or next), and the
 * picture keeps playing. ▲▼ look through the channels without changing, ◀▶ now / next, OK watches the highlighted
 * one, BACK closes. It closes by itself after a while with nothing pressed.
 */
class MiniGuide(private val a: Activity, parent: FrameLayout, private val watch: (Channel) -> Unit) {
    private val d = a.resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private val handler = Handler(Looper.getMainLooper())
    private val autoHide = Runnable { hide() }

    private val box = LinearLayout(a).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(48), dp(40), dp(48), dp(22))
        background = GradientDrawable(GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(0xF5000000.toInt(), 0xE6000000.toInt(), 0x00000000))
        visibility = View.GONE
        isClickable = true
    }
    private val keys = TextView(a).apply {
        textSize = 13f; setTextColor(0xFF9AA4B2.toInt()); setPadding(dp(14), dp(8), 0, 0)
        text = "▲▼ look through channels   ·   OK watch   ·   ◀▶ now / next   ·   BACK close"
    }

    private inner class RowView {
        val root = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL; setPadding(dp(14), dp(7), dp(14), dp(7)) }
        val num = TextView(a).apply { textSize = 15f; gravity = Gravity.END; minWidth = dp(34) }
        val logoBox = FrameLayout(a).apply { setBackgroundResource(R.drawable.logo_bg) }
        val logo = ImageView(a).apply { scaleType = ImageView.ScaleType.FIT_CENTER; setPadding(dp(4), dp(3), dp(4), dp(3)) }
        val initials = TextView(a).apply { textSize = 12f; gravity = Gravity.CENTER; setTypeface(null, android.graphics.Typeface.BOLD) }
        val mid = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        val title = TextView(a).apply { textSize = 17f; setTypeface(null, android.graphics.Typeface.BOLD); maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END }
        val bar = ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply { max = 1000 }
        val side = TextView(a).apply { textSize = 14f; maxLines = 1; ellipsize = android.text.TextUtils.TruncateAt.END }
        init {
            root.addView(num, LinearLayout.LayoutParams(dp(34), -2))
            logoBox.addView(initials, FrameLayout.LayoutParams(-1, -1))
            logoBox.addView(logo, FrameLayout.LayoutParams(-1, -1))
            root.addView(logoBox, LinearLayout.LayoutParams(dp(76), dp(42)).apply { marginStart = dp(14); marginEnd = dp(18) })
            mid.addView(title)
            mid.addView(bar, LinearLayout.LayoutParams(-1, dp(4)).apply { topMargin = dp(5) })
            root.addView(mid, LinearLayout.LayoutParams(0, -2, 1f))
            root.addView(side, LinearLayout.LayoutParams(0, -2, 0.7f).apply { marginStart = dp(22) })
        }
    }
    private val rows = List(5) { RowView() }

    init {
        rows.forEach { box.addView(it.root, LinearLayout.LayoutParams(-1, -2).apply { bottomMargin = dp(3) }) }
        box.addView(keys)
        parent.addView(box, FrameLayout.LayoutParams(-1, -2, Gravity.BOTTOM))
    }

    private var list: List<Channel> = emptyList()
    private var sel = 0
    private var next = false
    val showing: Boolean get() = box.visibility == View.VISIBLE

    fun show(channels: List<Channel>, current: Int) {
        if (channels.isEmpty()) return
        list = channels; sel = current.coerceIn(0, channels.size - 1); next = false
        draw()
        if (!showing) {
            box.visibility = View.VISIBLE
            if (!Ui.lite) { box.alpha = 0f; box.translationY = dp(30).toFloat(); box.animate().alpha(1f).translationY(0f).setDuration(180).start() }
        }
        poke()
        Ui.test("mini-guide open ${list[sel].name}")
    }

    fun hide() {
        handler.removeCallbacks(autoHide)
        if (!showing) return
        box.animate().cancel()
        box.visibility = View.GONE
        Ui.test("mini-guide closed")
    }

    private fun poke() { handler.removeCallbacks(autoHide); handler.postDelayed(autoHide, 10_000) }

    /** The remote while it's open: true when it took the key. */
    fun onKey(keyCode: Int, event: KeyEvent): Boolean {
        if (!showing) return false
        if (event.action != KeyEvent.ACTION_DOWN) return true
        poke()
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_CHANNEL_UP -> { sel = (sel - 1 + list.size) % list.size; draw() }
            KeyEvent.KEYCODE_DPAD_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN -> { sel = (sel + 1) % list.size; draw() }
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_DPAD_RIGHT -> { next = keyCode == KeyEvent.KEYCODE_DPAD_RIGHT; draw() }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> if (event.repeatCount == 0) {
                val c = list[sel]; hide(); Ui.test("mini-guide watch ${c.name}"); watch(c)
            }
            KeyEvent.KEYCODE_BACK -> hide()
            else -> return false
        }
        return true
    }

    private fun draw() {
        val now = System.currentTimeMillis()
        val h = dp(36)
        for ((k, r) in rows.withIndex()) {
            val c = list[(sel + k - 2 + list.size * 3) % list.size]
            val focused = k == 2
            r.root.visibility = if (list.size < 5 && (k - 2).let { it < -(list.size - 1) / 2 || it > list.size / 2 }) View.GONE else View.VISIBLE
            r.root.background = if (focused) GradientDrawable().apply { setColor(0xF2FFFFFF.toInt()); cornerRadius = dp(10).toFloat() } else null
            val fg = if (focused) 0xFF0B0E14.toInt() else 0xFFE6EDF3.toInt()
            val muted = if (focused) 0xFF3A4150.toInt() else 0xFF9AA4B2.toInt()
            r.num.text = if (Store.showNumbers && c.number > 0) c.number.toString() else ""
            r.num.setTextColor(muted)
            r.initials.text = Ui.initials(c.name); r.initials.setTextColor(0xFFE6EDF3.toInt())
            Logos.load(r.logo, c.logo, h, r.initials)
            val (on, nx) = Repo.nowNext(c)
            val p = if (next) nx else on
            r.title.text = when {
                p == null -> c.name
                next -> "${Ui.time(p.start)}  ${p.title}"
                else -> p.title
            }
            r.title.setTextColor(fg)
            r.bar.visibility = if (!next && on != null) View.VISIBLE else View.INVISIBLE
            if (on != null) r.bar.progress = (((now - on.start).toDouble() / (on.stop - on.start).coerceAtLeast(1)) * 1000).toInt().coerceIn(0, 1000)
            r.side.text = if (next) c.name else nx?.let { "Next ${Ui.time(it.start)}  ${it.title}" } ?: c.name
            r.side.setTextColor(muted)
        }
    }
}
