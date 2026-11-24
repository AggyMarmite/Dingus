package app.dingus.tv

import android.app.Activity
import androidx.media3.ui.AspectRatioFrameLayout
import androidx.media3.ui.PlayerView

/**
 * Picture size (≡ → Picture size): Fit (the whole picture), Fill (fills the screen, trimming the edges), Stretch
 * (fills the screen, changing the shape) or Zoom (for a picture with black bars all round). Remembered for each
 * channel, and once for films and episodes.
 */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
object PictureSize {
    val ids = listOf("fit", "fill", "stretch", "zoom")
    val names = listOf("Fit  ·  the whole picture", "Fill  ·  fills the screen, trims the edges", "Stretch  ·  fills the screen, changes the shape", "Zoom  ·  for a picture with black bars all round")
    fun label(id: String) = when (id) { "fill" -> "Fill"; "stretch" -> "Stretch"; "zoom" -> "Zoom"; else -> "Fit" }

    private fun key(channelId: String?) = if (channelId == null) "picture:vod" else "picture:$channelId"
    fun get(channelId: String?): String = try { Store.prefsString(key(channelId), "fit") } catch (_: Exception) { "fit" }

    /** Sizes the picture in [v] ("fit" for the guide's small window, whatever's chosen). */
    fun apply(v: PlayerView, id: String) {
        v.resizeMode = when (id) {
            "fill", "zoom" -> AspectRatioFrameLayout.RESIZE_MODE_ZOOM
            "stretch" -> AspectRatioFrameLayout.RESIZE_MODE_FILL
            else -> AspectRatioFrameLayout.RESIZE_MODE_FIT
        }
        val frame = v.findViewById<android.view.View>(androidx.media3.ui.R.id.exo_content_frame) ?: return
        val s = if (id == "zoom") 1.18f else 1f
        frame.scaleX = s; frame.scaleY = s
    }

    /** The choice, for [channelId] (null = films and episodes); [done] gets the new one. */
    fun choose(a: Activity, channelId: String?, title: String, done: (String) -> Unit) {
        val cur = ids.indexOf(get(channelId)).coerceAtLeast(0)
        Dialogs.builder(a).setTitle("Picture size  ·  $title")
            .setSingleChoiceItems(names.toTypedArray(), cur) { d, i ->
                d.dismiss()
                Store.putString(key(channelId), ids[i])
                Ui.test("picture-size ${ids[i]}")
                done(ids[i])
            }
            .show()
    }
}
