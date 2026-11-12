package app.dingus.tv

import android.app.Activity
import android.app.AlertDialog
import android.view.KeyEvent
import android.widget.EditText
import android.widget.ListView

/** Dark dialogs that work with the remote. */
object Dialogs {
    fun builder(a: Activity): AlertDialog.Builder = TvBuilder(a)

    /**
     * Dialogs that always answer the remote's OK button: it presses whatever's highlighted (a button or a list
     * item), whatever the device's own dialogs do with it.
     */
    private class TvBuilder(a: Activity) : AlertDialog.Builder(a, android.R.style.Theme_DeviceDefault_Dialog_Alert) {
        override fun create(): AlertDialog = super.create().also { d ->
            d.setOnKeyListener { _, code, ev ->
                if (code != KeyEvent.KEYCODE_DPAD_CENTER && code != KeyEvent.KEYCODE_ENTER && code != KeyEvent.KEYCODE_NUMPAD_ENTER) return@setOnKeyListener false
                val f = d.currentFocus ?: return@setOnKeyListener false
                if (f is EditText) return@setOnKeyListener false
                if (ev.action == KeyEvent.ACTION_UP && ev.repeatCount == 0) {
                    Ui.test("dialog-ok ${f.javaClass.simpleName}")
                    if (f is ListView) {
                        val pos = f.selectedItemPosition
                        if (pos >= 0) f.performItemClick(f.selectedView, pos, f.getItemIdAtPosition(pos))
                    } else f.performClick()
                }
                true
            }
        }
    }

    /** "Hide <category>?" – special categories (Favourites etc.) can't be hidden. */
    fun hideCategory(a: Activity, key: String, name: String, done: () -> Unit) {
        builder(a)
            .setTitle("Hide “$name”?")
            .setMessage("It won't show in the list any more. You can bring it back in Settings → Hidden categories.")
            .setPositiveButton("Hide") { _, _ -> Store.setHidden(key, true); done() }
            .setNegativeButton("Cancel", null)
            .show()
            .getButton(AlertDialog.BUTTON_POSITIVE)?.requestFocus()
    }
}

/** A one-line text box in a dialog (the Fire TV keyboard opens for it). */
fun Dialogs.input(a: Activity, title: String, initial: String, hint: String, number: Boolean = false, done: (String) -> Unit) {
    val box = android.widget.EditText(a).apply {
        setText(initial)
        setSelection(initial.length)
        this.hint = hint
        isSingleLine = true
        inputType = if (number) android.text.InputType.TYPE_CLASS_NUMBER else android.text.InputType.TYPE_CLASS_TEXT or android.text.InputType.TYPE_TEXT_VARIATION_URI
    }
    val pad = (20 * a.resources.displayMetrics.density).toInt()
    val wrap = android.widget.FrameLayout(a).apply { setPadding(pad, pad / 2, pad, 0); addView(box) }
    builder(a).setTitle(title).setView(wrap)
        .setPositiveButton("OK") { _, _ -> done(box.text.toString().trim()) }
        .setNegativeButton("Cancel", null)
        .show()
    box.requestFocus()
}
