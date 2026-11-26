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
 * One way of moving for the whole app: things glide to a stop, and the highlighted card
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
    fun lift(v: View, has: Boolean, scale: Float = 1.08f, shine: Boolean = true) {
        v.animate().cancel()
        if (has && scale >= 1.08f) v.setTag(R.id.motionOwn, true)
        if (!has) {
            v.animate().scaleX(1f).scaleY(1f).rotationX(0f).rotationY(0f).setDuration(260).setInterpolator(glide).start()
            return
        }
        val recent = System.currentTimeMillis() - dirAt < 400
        // (Only cards and buttons lean: a long pill leaning in 3D swings its far end out past its edges.)
        if (recent && (v.height == 0 || v.width < v.height * 2.5f)) {
            v.cameraDistance = 9000 * v.resources.displayMetrics.density
            // Lean in the direction the highlight came from, then settle as it lifts.
            v.rotationY = dirX * 7f; v.rotationX = -dirY * 5f
        }
        v.animate().scaleX(scale).scaleY(scale).rotationX(0f).rotationY(0f).setDuration(420).setInterpolator(spring).start()
        if (shine) sheen(v)
        if (BuildConfig.DEBUG) checkRoom(v, scale)
    }

    /**
     * (Self-test only.) Whether the lifted view, at its full size, still fits inside everything that clips it – a
     * highlight that's cut off at an edge is logged, and the test fails on it.
     */
    private fun checkRoom(v: View, scale: Float) {
        if (v.width == 0) return
        val growX = v.width * (scale - 1f) / 2f; val growY = v.height * (scale - 1f) / 2f
        var l = v.left - growX; var t = v.top - growY; var r = v.right + growX; var b = v.bottom + growY
        var p = v.parent
        var depth = 0
        while (p is android.view.ViewGroup && depth < 6) {
            // Clipped by this parent's edges (or its padding)?
            val padL = if (p.clipToPadding) p.paddingLeft else 0; val padR = if (p.clipToPadding) p.paddingRight else 0
            val padT = if (p.clipToPadding) p.paddingTop else 0; val padB = if (p.clipToPadding) p.paddingBottom else 0
            val cl = padL + p.scrollX; val cr = p.width - padR + p.scrollX; val ct = padT + p.scrollY; val cb = p.height - padB + p.scrollY
            // (Only along an edge the view isn't simply scrolled past: a card half off the end of a row is fine.)
            val onScreenX = v.right > 0 && l >= cl - v.width / 2 && r <= cr + v.width / 2
            if (p.clipChildren && onScreenX && (l < cl - 1 || r > cr + 1) && p !is androidx.recyclerview.widget.RecyclerView) {
                Ui.test("clip-risk ${name(v)} in ${name(p)}"); return
            }
            if (p.clipChildren && (t < ct - 1 || b > cb + 1) && p !is androidx.recyclerview.widget.RecyclerView && p !is android.widget.ScrollView) {
                Ui.test("clip-risk ${name(v)} in ${name(p)}"); return
            }
            l += p.left - p.scrollX; r += p.left - p.scrollX; t += p.top - p.scrollY; b += p.top - p.scrollY
            p = p.parent; depth++
        }
    }

    private fun name(v: View): String = try { if (v.id != View.NO_ID) v.resources.getResourceEntryName(v.id) else v.javaClass.simpleName } catch (_: Exception) { v.javaClass.simpleName }

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

    /**
     * The highlight moving between buttons (a film's Play, Trailer and season pills, Home's showcase buttons, choices
     * in Settings): the new one lifts with the same soft bounce as the cards, the old one settles. Cards in rows look
     * after themselves, and wide things (whole rows, lists) just change colour.
     */
    private val liftedKey = R.id.motionLifted
    fun buttons(old: View?, new: View?) {
        if (Ui.lite) return
        if (old != null && old.getTag(liftedKey) == true) { old.setTag(liftedKey, null); lift(old, false) }
        if (new == null) return
        // (After the view's own listeners: cards lift themselves, and say so.)
        new.post {
            if (!new.isFocused || new.getTag(R.id.motionOwn) == true) return@post
            val scale = liftScale(new) ?: return@post
            new.setTag(liftedKey, true)
            // Room to grow: the button's row (and the row's row) mustn't trim the lifted edges.
            (new.parent as? android.view.ViewGroup)?.let { p -> p.clipChildren = false; (p.parent as? android.view.ViewGroup)?.clipChildren = false }
            lift(new, true, scale, shine = false)
        }
    }

    /** How much a highlighted thing lifts: buttons and pills a little, whole rows only just (they're wide). */
    private fun liftScale(v: View): Float? {
        if (v.background !is android.graphics.drawable.StateListDrawable) return null
        val d = v.resources.displayMetrics
        if (v.height > 120 * d.density) return null
        var p = v.parent
        while (p is View) { if (p.id == R.id.sideMenu) return null; p = p.parent }
        return if (v.width > d.widthPixels * 0.34f) 1.02f else 1.06f
    }

    /** A Settings switch: the knob slides across with a little bounce and the colour fades (instant in lite mode). */
    fun switch(v: android.widget.ImageView, on: Boolean) {
        val sw = v.drawable as? Switch
        if (sw == null) { v.setImageDrawable(Switch(v.resources.displayMetrics.density, if (on) 1f else 0f)); return }
        val goal = if (on) 1f else 0f
        if (sw.goal == goal) return
        sw.goal = goal
        sw.anim?.cancel()
        if (Ui.lite) { sw.t = goal; sw.invalidateSelf(); return }
        sw.anim = ValueAnimator.ofFloat(sw.t, goal).apply {
            duration = 340; interpolator = OvershootInterpolator(1.2f)
            addUpdateListener { sw.t = it.animatedValue as Float; sw.invalidateSelf() }
            start()
        }
    }

    private class Switch(private val d: Float, var t: Float) : Drawable() {
        var goal = t
        var anim: ValueAnimator? = null
        private var focused = false
        private val p = Paint(Paint.ANTI_ALIAS_FLAG)
        private val r = RectF()
        override fun getIntrinsicWidth() = (40 * d).toInt()
        override fun getIntrinsicHeight() = (22 * d).toInt()
        override fun isStateful() = true
        override fun onStateChange(state: IntArray): Boolean {
            val f = state.contains(android.R.attr.state_focused) || state.contains(android.R.attr.state_selected)
            if (f != focused) { focused = f; invalidateSelf(); return true }
            return false
        }
        override fun draw(c: Canvas) {
            val b = bounds; val k = t.coerceIn(0f, 1f)
            val off = if (focused) 0xFFC9CED8.toInt() else 0x2EFFFFFF
            val onC = if (focused) 0xFF16A34A.toInt() else 0xFF22C55E.toInt()
            p.color = android.animation.ArgbEvaluator().evaluate(k, off, onC) as Int
            r.set(b.left.toFloat(), b.top.toFloat(), b.right.toFloat(), b.bottom.toFloat())
            c.drawRoundRect(r, r.height() / 2, r.height() / 2, p)
            val knob = r.height() - 6 * d
            val x = r.left + 3 * d + (r.width() - 6 * d - knob) * t // (t may overshoot a touch: the bounce)
            p.color = android.animation.ArgbEvaluator().evaluate(k, if (focused) 0xFFFFFFFF.toInt() else 0xFFCFD6E4.toInt(), 0xFFFFFFFF.toInt()) as Int
            c.drawOval(x, r.top + 3 * d, x + knob, r.top + 3 * d + knob, p)
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
