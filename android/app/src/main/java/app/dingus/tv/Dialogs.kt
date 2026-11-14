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
    private class TvBuilder(val a: Activity) : AlertDialog.Builder(a, android.R.style.Theme_DeviceDefault_Dialog_Alert) {
        // Lists as pills (white when highlighted, a soft fill and ✓ on what it's set to now).
        override fun setItems(items: Array<out CharSequence>, listener: android.content.DialogInterface.OnClickListener?): AlertDialog.Builder =
            setAdapter(PillAdapter(a, items, -1), listener)

        override fun setSingleChoiceItems(items: Array<out CharSequence>, checkedItem: Int, listener: android.content.DialogInterface.OnClickListener?): AlertDialog.Builder =
            setSingleChoiceItems(PillAdapter(a, items, checkedItem), checkedItem, listener)

        override fun create(): AlertDialog = super.create().also { d ->
            d.setOnShowListener { style(a, d) }
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

    private class PillAdapter(a: Activity, items: Array<out CharSequence>, val checked: Int) :
        android.widget.ArrayAdapter<CharSequence>(a, R.layout.dialog_item, items) {
        override fun getView(position: Int, convertView: android.view.View?, parent: android.view.ViewGroup): android.view.View {
            val v = super.getView(position, convertView, parent) as android.widget.TextView
            val on = position == checked
            v.setTypeface(null, if (on) android.graphics.Typeface.BOLD else android.graphics.Typeface.NORMAL)
            v.setCompoundDrawablesRelative(null, null, if (on) Check(v) else null, null)
            return v
        }
    }

    /** The ✓ on the current choice (dark on the white highlight). */
    private class Check(val v: android.widget.TextView) : android.graphics.drawable.Drawable() {
        private val p = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply { textSize = v.textSize; isFakeBoldText = true }
        override fun getIntrinsicWidth() = p.measureText("✓").toInt()
        override fun getIntrinsicHeight() = v.textSize.toInt()
        override fun isStateful() = true
        override fun onStateChange(state: IntArray): Boolean { invalidateSelf(); return true }
        override fun draw(c: android.graphics.Canvas) {
            p.color = if (state.contains(android.R.attr.state_selected) || state.contains(android.R.attr.state_focused)) 0xFF0B0E14.toInt() else 0xFFFFFFFF.toInt()
            c.drawText("✓", bounds.left.toFloat(), bounds.centerY() - (p.descent() + p.ascent()) / 2, p)
        }
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(cf: android.graphics.ColorFilter?) {}
        @Deprecated("Deprecated in Java") override fun getOpacity() = android.graphics.PixelFormat.TRANSLUCENT
    }

    /**
     * The new look on every pop-up: a dark rounded card, a bold title with a quieter line under it, pills for the
     * choices, and pill buttons with the main one first.
     */
    private fun style(a: Activity, d: AlertDialog) {
        try {
            val dp = a.resources.displayMetrics.density
            d.window?.setBackgroundDrawable(android.graphics.drawable.InsetDrawable(android.graphics.drawable.GradientDrawable().apply {
                setColor(0xFF121722.toInt()); cornerRadius = 20 * dp; setStroke((1 * dp).toInt(), 0x14FFFFFF)
            }, (16 * dp).toInt()))
            d.window?.setDimAmount(0.6f)
            val title = d.findViewById<android.widget.TextView>(a.resources.getIdentifier("alertTitle", "id", "android"))
            title?.apply { textSize = 20f; setTypeface(null, android.graphics.Typeface.BOLD); setTextColor(0xFFFFFFFF.toInt()) }
            d.findViewById<android.widget.TextView>(android.R.id.message)?.apply {
                textSize = 14f; setTextColor(0xFFAAB2C2.toInt()); setLineSpacing(3 * dp, 1f)
            }
            d.listView?.apply {
                selector = android.graphics.drawable.ColorDrawable(0)
                divider = android.graphics.drawable.ColorDrawable(0); dividerHeight = (4 * dp).toInt()
                setPadding((14 * dp).toInt(), (6 * dp).toInt(), (14 * dp).toInt(), (12 * dp).toInt())
                clipToPadding = false
            }
            val pos = d.getButton(AlertDialog.BUTTON_POSITIVE)
            val buttons = listOf(pos, d.getButton(AlertDialog.BUTTON_NEUTRAL), d.getButton(AlertDialog.BUTTON_NEGATIVE))
                .filter { it != null && it.visibility == android.view.View.VISIBLE && it.text.isNotEmpty() }
            for (b in buttons) {
                b.background = a.getDrawable(R.drawable.dialog_btn_bg)
                b.setTextColor(a.getColorStateList(R.color.pill_text))
                b.isAllCaps = false; b.textSize = 15f; b.setTypeface(null, android.graphics.Typeface.BOLD)
                b.minHeight = (40 * dp).toInt(); b.minimumHeight = (40 * dp).toInt()
                b.setPadding((22 * dp).toInt(), 0, (22 * dp).toInt(), 0)
                (b.layoutParams as? android.view.ViewGroup.MarginLayoutParams)?.let { it.marginEnd = (10 * dp).toInt(); it.marginStart = 0; b.layoutParams = it }
            }
            // The main button first (on the left), the others after it.
            val bar = pos?.parent as? android.view.ViewGroup
            val had = d.currentFocus
            if (bar != null && buttons.isNotEmpty()) {
                for (i in 0 until bar.childCount) bar.getChildAt(i).let { if (it !is android.widget.Button) it.visibility = android.view.View.GONE }
                buttons.reversed().forEach { b -> bar.removeView(b); bar.addView(b, 0) }
                (bar as? android.widget.LinearLayout)?.gravity = android.view.Gravity.START or android.view.Gravity.CENTER_VERTICAL
                bar.setPadding((20 * dp).toInt(), (6 * dp).toInt(), (20 * dp).toInt(), (18 * dp).toInt())
                // (Moving the buttons drops the highlight: back where it was, or the main button.)
                if (had != null && had.isAttachedToWindow) had.requestFocus()
                else if (d.listView == null || d.listView.count == 0) pos.requestFocus()
            }
        } catch (_: Exception) {}
    }

    /** ≡ on a category: move it up or down, or hide it. [visible] is the list as shown (ids). */
    fun categoryOptions(a: Activity, kind: String, visible: List<String>, id: String, name: String, done: () -> Unit) {
        val i = visible.indexOf(id)
        val opts = listOfNotNull(if (i > 0) "Move up" else null, if (i >= 0 && i < visible.size - 1) "Move down" else null, "Hide")
        builder(a).setTitle(name)
            .setItems(opts.toTypedArray()) { _, k ->
                when (opts[k]) {
                    "Move up" -> { Store.moveCategory(kind, visible, id, -1); done() }
                    "Move down" -> { Store.moveCategory(kind, visible, id, 1); done() }
                    "Hide" -> hideCategory(a, "$kind:$id", name, done)
                }
            }
            .show()
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
