package app.dingus.tv

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.ImageSpan
import android.text.style.ReplacementSpan

/**
 * The age rating (the UK certificate) in a soft translucent tag, and TMDB's mark with its score, for a details line.
 */
object Badges {
    /** A soft translucent tag round the certificate – the same for every one (no colours). */
    private class Round(val bg: Int, val d: Float) : ReplacementSpan() {
        override fun getSize(p: Paint, t: CharSequence, s: Int, e: Int, fm: Paint.FontMetricsInt?): Int =
            (p.measureText(t, s, e) + 16 * d).toInt()
        override fun draw(c: Canvas, t: CharSequence, s: Int, e: Int, x: Float, top: Int, y: Int, bottom: Int, p: Paint) {
            // Sized and placed from the text itself (not the line, which can have extra spacing), so the tag sits
            // evenly round the certificate, level with the words either side.
            val w = getSize(p, t, s, e, null).toFloat()
            val fm = p.fontMetrics
            val pad = 3 * d
            val boxTop = y + fm.ascent + 2 * d - pad
            val boxBottom = y + fm.descent - 1 * d + pad
            val old = p.color; val bold = p.isFakeBoldText
            p.color = bg
            c.drawRoundRect(RectF(x, boxTop, x + w, boxBottom), 5 * d, 5 * d, p)
            p.color = 0xFFFFFFFF.toInt(); p.isFakeBoldText = true
            c.drawText(t, s, e, x + 8 * d, y.toFloat(), p)
            p.color = old; p.isFakeBoldText = bold
        }
    }

    fun age(sb: SpannableStringBuilder, age: String, ctx: Context) {
        val d = ctx.resources.displayMetrics.density
        val at = sb.length
        sb.append(age.trim())
        sb.setSpan(Round(0x40FFFFFF, d), at, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
    }

    /** A picture centred on the text's height (an ImageSpan on the baseline sat too high). */
    private class Centred(dr: android.graphics.drawable.Drawable) : ImageSpan(dr) {
        override fun draw(c: Canvas, t: CharSequence, s: Int, e: Int, x: Float, top: Int, y: Int, bottom: Int, p: Paint) {
            val b = drawable
            val fm = p.fontMetrics
            val mid = y + (fm.ascent + fm.descent) / 2
            c.save(); c.translate(x, mid - b.bounds.height() / 2f); b.draw(c); c.restore()
        }
    }

    /** TMDB's mark, then the score as a percentage ("73%"). */
    fun tmdb(sb: SpannableStringBuilder, score: Float, ctx: Context, sizePx: Int) {
        val dr = ctx.getDrawable(R.drawable.tmdb) ?: return
        dr.setBounds(0, 0, (sizePx * 1.3f).toInt(), (sizePx * 1.3f).toInt())
        val at = sb.length
        sb.append(" ")
        sb.setSpan(Centred(dr), at, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        sb.append(" %d%%".format(Math.round(score * 10)))
    }

    /** "2022  [12]  [TMDB] 73%  ·  2 h 4 min  ·  Fantasy": the ratings as badges after the year. */
    fun line(ctx: Context, year: Int, r: Ratings.Info?, fallbackScore: Float, rest: List<String>, textPx: Float): CharSequence {
        val sb = SpannableStringBuilder()
        fun gap() { if (sb.isNotEmpty()) sb.append("   ") }
        if (year > 0) sb.append(year.toString())
        r?.age?.takeIf { it.isNotBlank() }?.let { gap(); age(sb, it, ctx) }
        val tmdb = if (r != null && r.score > 0 && r.votes > 0) r.score else null
        if (tmdb != null) { gap(); tmdb(sb, tmdb, ctx, textPx.toInt()) }
        else if (fallbackScore > 0) { gap(); sb.append("★ %.1f".format(fallbackScore)) }
        rest.filter { it.isNotBlank() }.forEach { if (sb.isNotEmpty()) sb.append("  ·  "); sb.append(it) }
        return sb
    }
}
