package app.dingus.tv

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Matrix
import android.graphics.drawable.Drawable
import android.util.AttributeSet
import android.widget.ImageView

/**
 * Fills its box like centre-crop, but keeps the top of the picture: the sky, the title and the faces up top stay in
 * view, and only the bottom of a widescreen picture is cut off. Used for the guide's backdrop, which is a thin strip.
 */
class TopCropImageView @JvmOverloads constructor(ctx: Context, attrs: AttributeSet? = null) : ImageView(ctx, attrs) {
    init { scaleType = ScaleType.MATRIX }

    override fun setImageBitmap(bm: Bitmap?) { super.setImageBitmap(bm); fitTop() }
    override fun setImageDrawable(d: Drawable?) { super.setImageDrawable(d); fitTop() }
    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) { super.onSizeChanged(w, h, oldw, oldh); fitTop() }

    private fun fitTop() {
        val d = drawable ?: return
        val dw = d.intrinsicWidth.toFloat(); val dh = d.intrinsicHeight.toFloat()
        if (dw <= 0f || dh <= 0f || width <= 0 || height <= 0) return
        val s = maxOf(width / dw, height / dh)
        imageMatrix = Matrix().apply { setScale(s, s); postTranslate((width - dw * s) / 2f, 0f) }
    }
}
