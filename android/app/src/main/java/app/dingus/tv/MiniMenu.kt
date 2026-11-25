package app.dingus.tv

import android.app.Activity
import android.app.Dialog
import android.graphics.Color
import android.graphics.Rect
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.view.Gravity
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The small frosted menu that springs out beside what you're on – holding OK on a programme in the guide, ≡ in the
 * guide or while watching – instead of a pop-up in the middle of the screen. A choice can open a page of its own
 * inside it (Choose source): OK slides across to it, ◀ slides back. ◀ or BACK on the first page closes it.
 */
class MiniMenu(private val a: Activity) {
    /** A quality or frame-rate tag on a row ("FHD", "50 fps"). */
    class Chip(val text: String, val fill: Int, val ink: Int)
    class Item(
        val text: String,
        val sub: String? = null,
        val chips: List<Chip> = emptyList(),
        val playing: Boolean = false,
        /** A page of its own: OK opens it (with a › on the row). */
        val page: (() -> Page)? = null,
        val action: (() -> Unit)? = null,
        /** A heading ("VERSIONS"): not a choice. */
        val heading: Boolean = false,
    )
    class Page(val title: String, val subtitle: String?, val items: List<Item>, val hint: String)

    private val d = a.resources.displayMetrics.density
    private var dialog: Dialog? = null
    private val stack = ArrayList<Page>()
    private lateinit var card: LinearLayout
    private lateinit var body: LinearLayout
    private var shownAt = Long.MAX_VALUE

    /**
     * Shows [first]: beside [anchor] (the highlighted programme, in screen pixels) or, with no anchor, at the bottom
     * right over the picture (full screen).
     */
    fun show(first: Page, anchor: Rect?) {
        val dlg = Dialog(a, android.R.style.Theme_Translucent_NoTitleBar)
        dialog = dlg
        card = LinearLayout(a).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(12), dp(14), dp(12), dp(10))
            background = GradientDrawable(GradientDrawable.Orientation.TOP_BOTTOM, intArrayOf(0xF71E2636.toInt(), 0xFA101520.toInt())).apply {
                cornerRadius = 22 * d; setStroke(dp(1), 0x1AFFFFFF)
            }
            if (android.os.Build.VERSION.SDK_INT >= 21) elevation = 24 * d
            clipChildren = false; clipToPadding = false
        }
        body = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; clipChildren = false; clipToPadding = false }
        card.addView(body)
        val wrap = FrameLayout(a).apply { setPadding(dp(16), dp(16), dp(16), dp(16)); clipChildren = false; clipToPadding = false }
        wrap.addView(card, FrameLayout.LayoutParams(dp(400), ViewGroup.LayoutParams.WRAP_CONTENT))
        dlg.setContentView(wrap)
        stack.clear(); stack.add(first)
        build(first)

        // Where: beside the programme (kept on screen), or bottom right.
        val w = dlg.window ?: return
        w.setBackgroundDrawable(android.graphics.drawable.ColorDrawable(Color.TRANSPARENT))
        w.setDimAmount(0.35f); w.addFlags(WindowManager.LayoutParams.FLAG_DIM_BEHIND)
        w.setWindowAnimations(0)
        val screen = a.resources.displayMetrics
        wrap.measure(View.MeasureSpec.makeMeasureSpec(screen.widthPixels, View.MeasureSpec.AT_MOST), View.MeasureSpec.makeMeasureSpec(screen.heightPixels, View.MeasureSpec.AT_MOST))
        val lp = w.attributes
        lp.width = ViewGroup.LayoutParams.WRAP_CONTENT; lp.height = ViewGroup.LayoutParams.WRAP_CONTENT
        if (anchor != null) {
            lp.gravity = Gravity.TOP or Gravity.START
            lp.x = (anchor.left + dp(8)).coerceIn(0, (screen.widthPixels - wrap.measuredWidth).coerceAtLeast(0))
            lp.y = (anchor.top - dp(40)).coerceIn(0, (screen.heightPixels - wrap.measuredHeight).coerceAtLeast(0))
            card.pivotX = 0f; card.pivotY = dp(40).toFloat()
        } else {
            lp.gravity = Gravity.BOTTOM or Gravity.END
            lp.x = dp(24); lp.y = dp(24)
            card.pivotX = dp(400).toFloat(); card.pivotY = wrap.measuredHeight.toFloat()
        }
        w.attributes = lp

        dlg.setOnKeyListener { _, code, ev -> key(code, ev) }
        dlg.setOnShowListener {
            shownAt = android.os.SystemClock.uptimeMillis()
            // Springs out from the programme.
            if (!Ui.lite) {
                card.alpha = 0f; card.scaleX = 0.92f; card.scaleY = 0.92f
                card.animate().alpha(1f).setDuration(200).start()
                card.animate().scaleX(1f).scaleY(1f).setDuration(420).setInterpolator(Motion.spring).start()
            }
            focusFirst()
        }
        dlg.show()
        Ui.test("mini-menu ${first.title}")
    }

    fun dismiss() {
        val dlg = dialog ?: return
        dialog = null
        if (Ui.lite) { dlg.dismiss(); return }
        card.animate().cancel()
        card.animate().alpha(0f).scaleX(0.96f).scaleY(0.96f).setDuration(150).withEndAction { try { dlg.dismiss() } catch (_: Exception) {} }.start()
    }

    private fun key(code: Int, ev: KeyEvent): Boolean {
        val ok = code == KeyEvent.KEYCODE_DPAD_CENTER || code == KeyEvent.KEYCODE_ENTER || code == KeyEvent.KEYCODE_NUMPAD_ENTER
        // The release of the OK that opened it (held down on the guide) mustn't choose the first item.
        if (ok && ev.downTime < shownAt) return true
        if (ok && ev.action == KeyEvent.ACTION_UP && ev.repeatCount == 0) { card.findFocus()?.performClick(); return true }
        if (ok) return true
        if (code == KeyEvent.KEYCODE_DPAD_LEFT || code == KeyEvent.KEYCODE_BACK) {
            if (ev.action == KeyEvent.ACTION_UP) { if (stack.size > 1) back() else dismiss() }
            return true
        }
        if (code == KeyEvent.KEYCODE_DPAD_RIGHT) return true
        return false
    }

    private fun open(p: Page) { stack.add(p); slide(p, forward = true); Ui.test("mini-menu-page ${p.title}") }
    private fun back() { stack.removeAt(stack.size - 1); slide(stack.last(), forward = false) }

    /** Across to another page: the old one slides out, the new one slides in from the other side. */
    private fun slide(p: Page, forward: Boolean) {
        val dx = 28 * d * (if (forward) 1 else -1)
        if (Ui.lite) { build(p); focusFirst(); return }
        body.animate().cancel()
        body.animate().translationX(-dx).alpha(0f).setDuration(140).setInterpolator(Motion.glide).withEndAction {
            build(p); focusFirst()
            body.translationX = dx
            body.animate().translationX(0f).alpha(1f).setDuration(320).setInterpolator(Motion.glide).start()
        }.start()
    }

    private fun focusFirst() {
        for (i in 0 until body.childCount) { val v = body.getChildAt(i); if (v.isFocusable) { v.requestFocus(); return } }
    }

    private fun build(p: Page) {
        body.removeAllViews()
        // Title (with ‹ on a page inside), then a quieter line.
        body.addView(TextView(a).apply {
            val sb = android.text.SpannableStringBuilder()
            if (stack.size > 1) { sb.append("‹  "); sb.setSpan(android.text.style.ForegroundColorSpan(0xFF94A3B8.toInt()), 0, sb.length, 0) }
            val at = sb.length; sb.append(p.title); sb.setSpan(android.text.style.StyleSpan(android.graphics.Typeface.BOLD), at, sb.length, 0)
            p.subtitle?.let { s -> val b = sb.length; sb.append("   $s"); sb.setSpan(android.text.style.ForegroundColorSpan(0xFF94A3B8.toInt()), b, sb.length, 0); sb.setSpan(android.text.style.RelativeSizeSpan(0.82f), b, sb.length, 0) }
            text = sb; textSize = 17f; setTextColor(0xFFF1F5F9.toInt()); maxLines = 2; ellipsize = TextUtils.TruncateAt.END
            setPadding(dp(8), 0, dp(8), dp(8))
        })
        for (it in p.items) body.addView(if (it.heading) heading(it.text) else row(it))
        body.addView(TextView(a).apply { text = p.hint; textSize = 12f; setTextColor(0xFF7D889A.toInt()); setPadding(dp(8), dp(8), dp(8), 0) })
    }

    private fun heading(t: String) = TextView(a).apply {
        text = t; textSize = 11f; letterSpacing = 0.12f; setTypeface(typeface, android.graphics.Typeface.BOLD)
        setTextColor(0xFF7D889A.toInt()); setPadding(dp(10), dp(8), dp(10), dp(4))
    }

    private fun row(it: Item): View {
        val r = LinearLayout(a).apply {
            orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL
            isFocusable = true; isClickable = true
            minimumHeight = dp(44)
            setPadding(dp(12), dp(8), dp(12), dp(8))
            background = a.getDrawable(R.drawable.mini_row_bg)
        }
        val inkMain = android.content.res.ColorStateList(arrayOf(intArrayOf(android.R.attr.state_focused), intArrayOf()), intArrayOf(0xFF0B0E14.toInt(), 0xFFF1F5F9.toInt()))
        val inkSub = android.content.res.ColorStateList(arrayOf(intArrayOf(android.R.attr.state_focused), intArrayOf()), intArrayOf(0xFF475569.toInt(), 0xFF94A3B8.toInt()))
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; isDuplicateParentStateEnabled = true }
        col.addView(TextView(a).apply {
            text = it.text; textSize = 15f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(inkMain)
            isDuplicateParentStateEnabled = true; maxLines = 1; ellipsize = TextUtils.TruncateAt.END
        })
        it.sub?.let { s -> col.addView(TextView(a).apply { text = s; textSize = 12f; setTextColor(inkSub); isDuplicateParentStateEnabled = true; maxLines = 1; ellipsize = TextUtils.TruncateAt.END }) }
        r.addView(col, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        for (c in it.chips) r.addView(TextView(a).apply {
            text = c.text; textSize = 11f; setTypeface(typeface, android.graphics.Typeface.BOLD); setTextColor(c.ink)
            setPadding(dp(6), dp(2), dp(6), dp(2))
            background = GradientDrawable().apply { cornerRadius = 5 * d; setColor(c.fill) }
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(6) })
        if (it.playing) r.addView(TextView(a).apply {
            text = "●  Playing"; textSize = 12f; setTypeface(typeface, android.graphics.Typeface.BOLD)
            setTextColor(android.content.res.ColorStateList(arrayOf(intArrayOf(android.R.attr.state_focused), intArrayOf()), intArrayOf(0xFF15803D.toInt(), 0xFF4ADE80.toInt())))
            isDuplicateParentStateEnabled = true
        }, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(8) })
        if (it.page != null) r.addView(TextView(a).apply { text = "›"; textSize = 20f; setTextColor(inkSub); isDuplicateParentStateEnabled = true },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { marginStart = dp(8) })
        r.setOnFocusChangeListener { v, has -> Motion.lift(v, has, 1.04f, shine = false) }
        r.setOnClickListener { _ ->
            val pg = it.page
            if (pg != null) open(pg())
            else { dismiss(); it.action?.invoke() }
        }
        return r.also { v -> v.layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { bottomMargin = dp(2) } }
    }

    private fun dp(v: Int) = (v * d).toInt()

    companion object {
        /** "FHD" "50 fps" tags for a channel version, from what Dingus measured (or its name). */
        fun qualityChips(c: Channel): List<Chip> {
            val q = Merge.qualityText(c)
            val m = Regex("""(\d+)p(?: (\d+)fps)?""").find(q)
            val label = if (m != null) {
                val h = m.groupValues[1].toInt()
                when { h >= 2000 -> "4K"; h >= 1000 -> "FHD"; h >= 700 -> "HD"; else -> "SD" }
            } else q
            val fill = when (label) { "4K" -> 0x33A855F7; "FHD" -> 0x3322C55E; "HD" -> 0x333B82F6; else -> 0x1AFFFFFF }
            val ink = when (label) { "4K" -> 0xFFE9D5FF.toInt(); "FHD" -> 0xFFBBF7D0.toInt(); "HD" -> 0xFFBFDBFE.toInt(); else -> 0xFFE2E8F0.toInt() }
            val out = arrayListOf(Chip(label, fill, ink))
            m?.groupValues?.getOrNull(2)?.takeIf { it.isNotEmpty() }?.let { out.add(Chip("$it fps", 0x1AFFFFFF, 0xFFE2E8F0.toInt())) }
            return out
        }
    }
}
