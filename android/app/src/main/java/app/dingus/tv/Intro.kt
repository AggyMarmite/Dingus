package app.dingus.tv

import android.app.Activity
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.animation.DecelerateInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView

/**
 * The start-up animation: carries on from the still logo Android shows while Dingus opens – the logo springs, a
 * soft glow comes up behind it and the name rises beneath – then fades into Home. Once per start, about 1.3 seconds.
 */
object Intro {
    private var shown = false

    fun play(a: Activity) {
        if (shown || Ui.lite) return
        shown = true
        val d = a.resources.displayMetrics.density
        val root = a.findViewById<ViewGroup>(android.R.id.content) ?: return
        val box = FrameLayout(a).apply {
            setBackgroundColor(0xFF0B0E14.toInt())
            isClickable = false; isFocusable = false
        }
        val glow = View(a).apply {
            background = GradientDrawable().apply {
                gradientType = GradientDrawable.RADIAL_GRADIENT; gradientRadius = 260 * d
                colors = intArrayOf(0x806D4BFF, 0x002E6DFF)
            }
            alpha = 0f
        }
        box.addView(glow, FrameLayout.LayoutParams((520 * d).toInt(), (520 * d).toInt(), Gravity.CENTER))
        val col = LinearLayout(a).apply { orientation = LinearLayout.VERTICAL; gravity = Gravity.CENTER_HORIZONTAL }
        // The same picture and size as the still one, so it carries straight on from it.
        val logo = ImageView(a).apply { setImageResource(R.drawable.splash_logo) }
        col.addView(logo, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        val name = TextView(a).apply {
            text = "Dingus"; textSize = 30f; setTextColor(0xFFFFFFFF.toInt()); setTypeface(typeface, android.graphics.Typeface.BOLD)
            alpha = 0f; translationY = 14 * d
        }
        col.addView(name, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply { topMargin = (14 * d).toInt() })
        box.addView(col, FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER))
        root.addView(box, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        Ui.test("intro-shown")

        logo.scaleX = 0.92f; logo.scaleY = 0.92f
        logo.animate().scaleX(1f).scaleY(1f).setDuration(520).setInterpolator(OvershootInterpolator(2.2f)).start()
        glow.animate().alpha(1f).setDuration(600).start()
        name.animate().alpha(1f).translationY(0f).setStartDelay(260).setDuration(420).setInterpolator(DecelerateInterpolator()).start()
        box.animate().alpha(0f).setStartDelay(1_050).setDuration(320).withEndAction { root.removeView(box) }.start()
    }
}
