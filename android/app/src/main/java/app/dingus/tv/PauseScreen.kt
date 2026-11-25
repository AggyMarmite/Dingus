package app.dingus.tv

import android.app.Activity
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView

/**
 * A film or episode paused for a few seconds: the picture dims on the left and the details glide in – the title (its
 * own logo when there is one), the season and episode, a short description, the time left and when it'll end, and a
 * line along the bottom. ▶ or OK carries on; any other button brings back the normal controls.
 */
class PauseScreen(private val a: Activity) {
    private val d = a.resources.displayMetrics.density
    private fun dp(v: Int) = (v * d).toInt()
    private val root = FrameLayout(a).apply { visibility = View.GONE; isClickable = false }
    private val dim = View(a).apply {
        background = GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(0xE0000000.toInt(), 0xB3000000.toInt(), 0x26000000))
    }
    private val kicker = TextView(a).apply { textSize = 13f; letterSpacing = 0.12f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFCBD5E1.toInt()) }
    private val logo = ImageView(a).apply { adjustViewBounds = true; scaleType = ImageView.ScaleType.FIT_START; visibility = View.GONE }
    private val title = TextView(a).apply { textSize = 40f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFF8FAFC.toInt()); maxLines = 2; ellipsize = android.text.TextUtils.TruncateAt.END }
    private val desc = TextView(a).apply { textSize = 16f; setTextColor(0xFFE2E8F0.toInt()); setLineSpacing(4 * d, 1f); maxLines = 4; ellipsize = android.text.TextUtils.TruncateAt.END }
    private val meta = TextView(a).apply { textSize = 15f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFCBD5E1.toInt()) }
    private val badge = TextView(a).apply {
        text = "❚❚   Paused"; textSize = 15f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(0xFFFFFFFF.toInt())
        setPadding(dp(16), dp(7), dp(18), dp(7))
        background = GradientDrawable().apply { cornerRadius = 99 * d; setColor(0xD9141A26.toInt()); setStroke(dp(1), 0x1AFFFFFF) }
    }
    private val pos = TextView(a).apply { textSize = 14f; setTextColor(0xFFCBD5E1.toInt()) }
    private val left = TextView(a).apply { textSize = 14f; setTextColor(0xFFCBD5E1.toInt()) }
    private val line = ProgressBar(a, null, android.R.attr.progressBarStyleHorizontal).apply { max = 1000; progressDrawable = a.getDrawable(R.drawable.progress_white) }
    private val box: LinearLayout
    private val foot: LinearLayout
    val showing get() = root.visibility == View.VISIBLE

    init {
        root.addView(dim, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        box = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL }
        box.addView(kicker)
        box.addView(logo, LinearLayout.LayoutParams(dp(420), dp(120)).apply { topMargin = dp(10) })
        box.addView(title, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(4) })
        box.addView(desc, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(12) })
        box.addView(meta, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = dp(14) })
        root.addView(box, FrameLayout.LayoutParams(dp(500), ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.START or Gravity.CENTER_VERTICAL).apply { marginStart = dp(56); bottomMargin = dp(40) })
        root.addView(badge, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.TOP or Gravity.END).apply { topMargin = dp(28); marginEnd = dp(36) })
        foot = LinearLayout(a).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        foot.addView(TextView(a).apply {
            text = "▶"; gravity = Gravity.CENTER; textSize = 16f; setTextColor(0xFF0B0E14.toInt())
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(0xFFFFFFFF.toInt()) }
        }, LinearLayout.LayoutParams(dp(40), dp(40)))
        foot.addView(pos, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(14) })
        foot.addView(line, LinearLayout.LayoutParams(0, dp(4), 1f).apply { marginStart = dp(14); marginEnd = dp(14) })
        foot.addView(left)
        root.addView(foot, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.BOTTOM).apply { marginStart = dp(56); marginEnd = dp(56); bottomMargin = dp(32) })
        a.findViewById<ViewGroup>(android.R.id.content).addView(root, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
    }

    /** What it says: [kick] over the title (or its [logoUrl]), [about] under it. */
    fun show(kick: String, name: String, logoUrl: String?, about: String?) {
        kicker.text = kick
        title.text = name
        if (logoUrl != null) TitleLogos.show(logo, title, logoUrl) else { logo.visibility = View.GONE; title.visibility = View.VISIBLE }
        desc.text = about ?: ""; desc.visibility = if (about.isNullOrBlank()) View.GONE else View.VISIBLE
        if (showing) return
        root.visibility = View.VISIBLE
        Ui.test("pause-screen $name")
        if (Ui.lite) return
        dim.alpha = 0f; dim.animate().alpha(1f).setDuration(700).start()
        val parts = (0 until box.childCount).map { box.getChildAt(it) } + listOf(badge, foot)
        parts.forEachIndexed { i, v ->
            v.alpha = 0f; v.translationY = 14 * d
            v.animate().alpha(1f).translationY(0f).setStartDelay(120L + i * 70L).setDuration(560).setInterpolator(Motion.glide).start()
        }
    }

    /** The time: where it's up to, how long is left and when it'll end. */
    fun time(position: Long, length: Long) {
        if (length <= 0) { meta.text = ""; return }
        val leftMs = length - position
        val mins = leftMs / 60_000L
        meta.text = (if (mins >= 60) "${mins / 60} h ${mins % 60} min left" else "$mins min left") + "  ·  ends ${Ui.time(System.currentTimeMillis() + leftMs)}"
        pos.text = PlayBar.clock(position); left.text = "−" + PlayBar.clock(leftMs)
        line.progress = (position * 1000 / length).toInt()
    }

    fun hide() {
        if (!showing) return
        if (Ui.lite) { root.visibility = View.GONE; return }
        root.animate().cancel()
        root.animate().alpha(0f).setDuration(220).withEndAction { root.visibility = View.GONE; root.alpha = 1f }.start()
    }
}
