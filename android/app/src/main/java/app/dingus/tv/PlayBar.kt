package app.dingus.tv

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.util.AttributeSet
import android.view.View

/**
 * The film / episode progress bar: watched part white, loaded part softer, and a round knob showing play or pause
 * (OK on the bar pauses or plays). While skipping, a white bubble over the knob shows where you'll be and how far
 * you've skipped ("41:51", "−0:40").
 */
class PlayBar @JvmOverloads constructor(ctx: Context, attrs: AttributeSet? = null) : View(ctx, attrs) {
    var duration = 0L
    var position = 0L
    var buffered = 0L
    var playing = true
    /** While skipping: how far from where it started (shown under the bubble), or null for no bubble. */
    var skipped: Long? = null

    private val d = resources.displayMetrics.density
    private val p = Paint(Paint.ANTI_ALIAS_FLAG)
    private val glyph = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = 0xFF0B0E14.toInt() }
    private val bubbleText = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textSize = 15 * d; isFakeBoldText = true; color = 0xFF0B0E14.toInt(); textAlign = Paint.Align.CENTER
    }
    private val deltaText = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        textSize = 12 * d; isFakeBoldText = true; color = 0xFFFFFFFF.toInt(); textAlign = Paint.Align.CENTER
        setShadowLayer(3 * d, 0f, d, 0xAA000000.toInt())
    }
    private val r = RectF()

    override fun onFocusChanged(gainFocus: Boolean, direction: Int, previouslyFocusedRect: android.graphics.Rect?) {
        super.onFocusChanged(gainFocus, direction, previouslyFocusedRect)
        invalidate()
    }

    override fun onDraw(c: Canvas) {
        val w = width.toFloat()
        val y = height - 14 * d
        val th = 6 * d
        val left = 0f; val right = w
        fun xOf(t: Long) = if (duration > 0) left + (right - left) * (t.coerceIn(0, duration).toFloat() / duration) else left
        // Track, loaded, watched.
        p.color = 0x38FFFFFF; r.set(left, y - th / 2, right, y + th / 2); c.drawRoundRect(r, th / 2, th / 2, p)
        p.color = 0x66FFFFFF; r.set(left, y - th / 2, xOf(buffered), y + th / 2); c.drawRoundRect(r, th / 2, th / 2, p)
        p.color = 0xFFFFFFFF.toInt(); r.set(left, y - th / 2, xOf(position), y + th / 2); c.drawRoundRect(r, th / 2, th / 2, p)
        // The knob: play/pause in it; a soft ring round it when the remote's on the bar.
        val kx = xOf(position).coerceIn(17 * d, w - 17 * d)
        val kr = 17 * d
        if (isFocused) { p.color = 0x40FFFFFF; c.drawCircle(kx, y, kr + 6 * d, p) }
        p.color = 0xFFFFFFFF.toInt(); p.setShadowLayer(6 * d, 0f, 2 * d, 0x80000000.toInt())
        c.drawCircle(kx, y, kr, p); p.clearShadowLayer()
        if (playing) { // pause bars
            val bw = 3.6f * d; val bh = 12 * d; val gap = 3 * d
            r.set(kx - gap - bw, y - bh / 2, kx - gap, y + bh / 2); c.drawRoundRect(r, d, d, glyph)
            r.set(kx + gap, y - bh / 2, kx + gap + bw, y + bh / 2); c.drawRoundRect(r, d, d, glyph)
        } else { // play triangle
            val s = 7 * d
            val path = android.graphics.Path().apply { moveTo(kx - s * 0.6f, y - s); lineTo(kx + s, y); lineTo(kx - s * 0.6f, y + s); close() }
            c.drawPath(path, glyph)
        }
        // The bubble while skipping.
        val sk = skipped ?: return
        val bw = 84 * d; val bh = 26 * d
        val bx = kx.coerceIn(bw / 2, w - bw / 2)
        val top = y - kr - 26 * d - bh
        p.color = 0xFFFFFFFF.toInt(); r.set(bx - bw / 2, top, bx + bw / 2, top + bh); c.drawRoundRect(r, 12 * d, 12 * d, p)
        c.drawText(clock(position), bx, top + bh / 2 - (bubbleText.descent() + bubbleText.ascent()) / 2, bubbleText)
        c.drawText((if (sk < 0) "−" else "+") + clock(kotlin.math.abs(sk)), bx, top + bh + 15 * d, deltaText)
    }

    companion object {
        fun clock(ms: Long): String {
            val s = ms / 1000
            return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
        }
    }
}
