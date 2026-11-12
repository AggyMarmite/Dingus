package app.dingus.tv

import android.app.Activity
import android.app.AlertDialog

/** Dark dialogs that work with the remote. */
object Dialogs {
    fun builder(a: Activity): AlertDialog.Builder = AlertDialog.Builder(a, android.R.style.Theme_DeviceDefault_Dialog_Alert)

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
