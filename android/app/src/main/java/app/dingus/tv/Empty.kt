package app.dingus.tv

import android.content.Context
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.style.ForegroundColorSpan
import android.text.style.RelativeSizeSpan
import android.text.style.StyleSpan

/**
 * Friendlier empty screens: a big soft icon, a short title and a hint on how to fill it – in the screen's own
 * "empty" text, so nothing about the layout changes.
 */
object Empty {
    const val STAR = 0xF04D2; const val BOOKMARK = 0xF00C3; const val SEARCH = 0xF0349; const val BELL = 0xF009C
    const val CONTINUE = 0xF040D; const val FOLDER = 0xF0256; const val COMING = 0xF0FCF; const val TV = 0xF07F4; const val OFFLINE = 0xF05AA

    fun text(ctx: Context, icon: Int, title: String, hint: String = ""): CharSequence {
        val sb = SpannableStringBuilder()
        val face = try { androidx.core.content.res.ResourcesCompat.getFont(ctx, R.font.icons) } catch (e: Exception) { null }
        if (face != null) {
            val g = String(Character.toChars(icon))
            sb.append(g)
            sb.setSpan(object : android.text.style.MetricAffectingSpan() {
                override fun updateDrawState(p: android.text.TextPaint) { p.typeface = face }
                override fun updateMeasureState(p: android.text.TextPaint) { p.typeface = face }
            }, 0, g.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            sb.setSpan(RelativeSizeSpan(3.2f), 0, g.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            sb.setSpan(ForegroundColorSpan(Themes.color(ctx, R.attr.dAccent)), 0, g.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
            sb.append("\n")
        }
        val t = sb.length
        sb.append(title)
        sb.setSpan(StyleSpan(android.graphics.Typeface.BOLD), t, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        sb.setSpan(RelativeSizeSpan(1.25f), t, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        sb.setSpan(ForegroundColorSpan(Themes.color(ctx, R.attr.dText)), t, sb.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        if (hint.isNotEmpty()) sb.append("\n").append(hint)
        return sb
    }
}
