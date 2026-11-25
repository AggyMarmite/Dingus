package app.dingus.tv

import android.animation.ValueAnimator
import android.app.Activity
import android.graphics.Canvas
import android.graphics.ColorFilter
import android.graphics.LinearGradient
import android.graphics.Paint
import android.graphics.Path
import android.graphics.PixelFormat
import android.graphics.RectF
import android.graphics.Shader
import android.graphics.drawable.Drawable
import android.os.Build
import android.view.KeyEvent
import android.view.View
import android.view.animation.OvershootInterpolator
import android.view.animation.PathInterpolator

/**
 * One way of moving for the whole app, in the style of Apple TV: things glide to a stop, and the highlighted card
 * lifts with a soft bounce, leans the way you pressed and catches the light. Lite mode keeps it all instant.
 */
object Motion {
    /** Quick to start, long soft finish: scrolling, fades, slides. */
    val glide = PathInterpolator(0.25f, 0.9f, 0.3f, 1f)
    /** A small bounce past the end and back: the highlight lifting. */
    val spring = OvershootInterpolator(1.4f)

    // The last arrow pressed, so a newly highlighted card can lean that way.
    private var dirX = 0; private var dirY = 0; private var dirAt = 0L
    fun key(e: KeyEvent) {
        if (e.action != KeyEvent.ACTION_DOWN) return
        dirX = when (e.keyCode) { KeyEvent.KEYCODE_DPAD_LEFT -> -1; KeyEvent.KEYCODE_DPAD_RIGHT -> 1; else -> 0 }
        dirY = when (e.keyCode) { KeyEvent.KEYCODE_DPAD_UP -> -1; KeyEvent.KEYCODE_DPAD_DOWN -> 1; else -> 0 }
        dirAt = if (dirX != 0 || dirY != 0) System.currentTimeMillis() else 0L
    }

    /** The highlight arriving on (or leaving) a card. */
    fun lift(v: View, has: Boolean, scale: Float = 1.08f) {
        v.animate().cancel()
        if (!has) {
            v.animate().scaleX(1f).scaleY(1f).rotationX(0f).rotationY(0f).setDuration(260).setInterpolator(glide).start()
            return
        }
        val recent = System.currentTimeMillis() - dirAt < 400
        if (recent) {
            v.cameraDistance = 9000 * v.resources.displayMetrics.density
            // Lean in the direction the highlight came from, then settle as it lifts.
            v.rotationY = dirX * 7f; v.rotationX = -dirY * 5f
        }
        v.animate().scaleX(scale).scaleY(scale).rotationX(0f).rotationY(0f).setDuration(420).setInterpolator(spring).start()
        sheen(v)
    }

    /** A faint glint sweeping across the card once, as if it's caught the light. */
    private fun sheen(v: View) {
        if (Build.VERSION.SDK_INT < 23 || v.width == 0) return
        val old = v.foreground
        if (old is Sheen) return
        val s = Sheen(18 * v.resources.displayMetrics.density)
        v.foreground = s
        ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 850; interpolator = glide
            addUpdateListener { s.t = it.animatedValue as Float; s.invalidateSelf() }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(a: android.animation.Animator) { if (v.foreground === s) v.foreground = old }
            })
            start()
        }
    }

    private class Sheen(private val radius: Float) : Drawable() {
        var t = 0f
        private val p = Paint(Paint.ANTI_ALIAS_FLAG)
        private val clip = Path(); private val r = RectF()
        override fun draw(c: Canvas) {
            val b = bounds; if (b.isEmpty) return
            val w = b.width().toFloat()
            val x = -w * 0.6f + t * w * 1.8f
            val a = ((1f - t) * 0x50).toInt().coerceIn(0, 0x50)
            p.shader = LinearGradient(x, 0f, x + w * 0.45f, b.height() * 0.35f,
                intArrayOf(0, (a shl 24) or 0xFFFFFF, 0), floatArrayOf(0f, 0.5f, 1f), Shader.TileMode.CLAMP)
            r.set(b); clip.reset(); clip.addRoundRect(r, radius, radius, Path.Direction.CW)
            c.save(); c.clipPath(clip); c.drawRect(r, p); c.restore()
        }
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(cf: ColorFilter?) {}
        @Deprecated("Deprecated in Java") override fun getOpacity() = PixelFormat.TRANSLUCENT
    }

    /** Opening a page from a card: the page grows out of the card. */
    fun from(v: View?): android.os.Bundle? =
        if (v == null || Ui.lite || v.width == 0) null
        else android.app.ActivityOptions.makeScaleUpAnimation(v, 0, 0, v.width, v.height).toBundle()

    /** Leaving a page that grew out of a card: it shrinks away as the page behind comes forward. */
    fun closing(a: Activity) { if (!Ui.lite) a.overridePendingTransition(R.anim.page_back_in, R.anim.page_shrink_out) }

    /** A page's text rising in, one line after another. */
    fun rise(vararg views: View?) {
        if (Ui.lite) return
        val d = views.firstOrNull { it != null }?.resources?.displayMetrics?.density ?: return
        views.filterNotNull().forEachIndexed { i, v ->
            v.alpha = 0f; v.translationY = 18 * d
            v.animate().alpha(1f).translationY(0f).setStartDelay(120L + i * 60L).setDuration(460).setInterpolator(glide).start()
        }
    }
}
