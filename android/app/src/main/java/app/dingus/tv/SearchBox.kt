package app.dingus.tv

import android.content.Context
import android.util.AttributeSet
import android.view.KeyEvent

/**
 * The search box. With the Fire TV keyboard up, BACK goes to the keyboard first, and on some Fire OS versions the
 * keyboard just stays there. This catches BACK before the keyboard does, so it always closes it ([onBack]).
 */
class SearchBox @JvmOverloads constructor(ctx: Context, attrs: AttributeSet? = null, style: Int = android.R.attr.editTextStyle) :
    android.widget.EditText(ctx, attrs, style) {
    var onBack: (() -> Unit)? = null

    override fun onKeyPreIme(keyCode: Int, event: KeyEvent): Boolean {
        val b = onBack
        if (keyCode == KeyEvent.KEYCODE_BACK && b != null && isFocused) {
            if (event.action == KeyEvent.ACTION_UP) b()
            return true
        }
        return super.onKeyPreIme(keyCode, event)
    }
}
