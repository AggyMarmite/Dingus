package app.dingus.tv

import android.animation.ValueAnimator
import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Drawable
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.TransitionDrawable
import android.view.View
import android.widget.ImageView

/**
 * Artwork-tinted pages: the background behind the rows takes the colour of the highlighted title's poster (a soft
 * glow, top right, fading into the page), and the highlight's shadow glows in it too. It changes gently as you move.
 */
object Tint {
    private val anims = java.util.WeakHashMap<Activity, ValueAnimator>()
    private val current = java.util.WeakHashMap<Activity, Int>()

    private val main = android.os.Handler(android.os.Looper.getMainLooper())
    private var pending: Runnable? = null

    /** Runs [then] once the highlight has stayed on [v] for a moment (a newer call cancels the last). */
    fun settle(v: View, then: () -> Unit) {
        pending?.let { main.removeCallbacks(it) }
        if (Ui.lite) return
        val r = Runnable { if (v.hasFocus() || v.isFocused) then() }
        pending = r
        main.postDelayed(r, 260)
    }

    private val colours = java.util.WeakHashMap<Bitmap, Int>()

    /** The poster's colour, made into a deep, rich glow (or null while there's no picture yet). */
    fun colourOf(poster: ImageView?): Int? {
        val bmp = bitmapOf(poster?.drawable) ?: return null
        colours[bmp]?.let { return it }
        val px = try { Bitmap.createScaledBitmap(bmp, 1, 1, true).getPixel(0, 0) } catch (e: Exception) { return null }
        val hsv = FloatArray(3); Color.colorToHSV(px, hsv)
        hsv[1] = (hsv[1] * 1.4f).coerceIn(0.35f, 0.9f); hsv[2] = 0.5f
        return Color.HSVToColor(hsv).also { colours[bmp] = it }
    }

    private fun bitmapOf(d: Drawable?): Bitmap? = when (d) {
        is BitmapDrawable -> d.bitmap
        is TransitionDrawable -> bitmapOf(d.getDrawable(d.numberOfLayers - 1))
        else -> null
    }

    /** Moves the page's glow to [colour] over half a second. */
    fun to(a: Activity, colour: Int) {
        if (Ui.lite) return
        val bg = Themes.color(a, R.attr.dBg)
        val from = current[a] ?: bg
        if (from == colour) return
        anims[a]?.cancel()
        val g = GradientDrawable().apply {
            gradientType = GradientDrawable.RADIAL_GRADIENT
            gradientRadius = a.resources.displayMetrics.widthPixels * 0.75f
            setGradientCenter(0.72f, 0.28f)
        }
        // On the screen's own background (behind the pictures and rows).
        (a.findViewById<android.view.ViewGroup>(android.R.id.content)?.getChildAt(0) ?: return).background = g
        anims[a] = ValueAnimator.ofArgb(from, colour).apply {
            duration = 350
            addUpdateListener { v -> val c = v.animatedValue as Int; current[a] = c; g.colors = intArrayOf(c, blend(c, bg, 0.6f), bg) }
            start()
        }
    }

    /** The highlighted card's shadow in the same colour (Fire OS 7 and later). */
    fun glow(card: View, colour: Int?, on: Boolean) {
        if (android.os.Build.VERSION.SDK_INT < 28 || Ui.lite) return
        if (on && colour != null) {
            card.outlineSpotShadowColor = colour; card.outlineAmbientShadowColor = colour
            card.translationZ = 10 * card.resources.displayMetrics.density
        } else card.translationZ = 0f
    }

    fun forget(a: Activity) { anims.remove(a)?.cancel(); current.remove(a) }

    private fun blend(a: Int, b: Int, t: Float): Int = Color.rgb(
        (Color.red(a) * (1 - t) + Color.red(b) * t).toInt(), (Color.green(a) * (1 - t) + Color.green(b) * t).toInt(), (Color.blue(a) * (1 - t) + Color.blue(b) * t).toInt())
}
