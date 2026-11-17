package app.dingus.tv

import android.app.Activity
import android.os.Build
import android.util.Log
import android.view.Display
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Match the TV's refresh rate to the video (25/50 fps → 50 Hz, 23.976 → 24 Hz, 29.97/59.94 → 60 Hz) so motion is
 * smooth, then put it back when playback ends. Fire TV and Android TV switch modes through the window's preferred
 * display mode.
 */
object FrameRate {
    private var switched = false
    /** The TV's own mode before the first switch: what going back to the guide returns to. */
    private var baseModeId = 0

    fun match(a: Activity, fps: Float) {
        if (!Store.matchFrameRate || fps <= 0f || Build.VERSION.SDK_INT < 23) return
        try {
            val display = a.windowManager.defaultDisplay
            val current = display.mode
            val modes = display.supportedModes.filter {
                it.physicalWidth == current.physicalWidth && it.physicalHeight == current.physicalHeight
            }
            val best = pick(modes, fps) ?: return
            if (best.modeId == current.modeId && !switched) return
            val lp = a.window.attributes
            if (lp.preferredDisplayModeId == best.modeId) return
            if (!switched) baseModeId = current.modeId
            lp.preferredDisplayModeId = best.modeId
            a.window.attributes = lp
            switched = true
            Ui.test("frame-rate ${"%.3f".format(fps)} fps → ${"%.2f".format(best.refreshRate)} Hz")
        } catch (e: Exception) { Log.w("Dingus", "frame rate", e) }
    }

    /** The mode whose refresh rate is the video's frame rate or a whole multiple of it (lowest such), if any. */
    private fun pick(modes: List<Display.Mode>, fps: Float): Display.Mode? {
        fun fits(hz: Float): Int? {
            val ratio = hz / fps
            val n = ratio.roundToInt()
            return if (n in 1..4 && abs(ratio - n) < 0.004f * n) n else null
        }
        return modes.mapNotNull { m -> fits(m.refreshRate)?.let { m to it } }.minByOrNull { it.second }?.first
    }

    /** Back to the TV's own rate (the guide, the menus): the mode it had before, not "no preference". */
    fun reset(a: Activity) {
        if (!switched) return
        try {
            val lp = a.window.attributes
            lp.preferredDisplayModeId = baseModeId
            a.window.attributes = lp
            Ui.test("frame-rate back to ${baseModeId}")
        } catch (e: Exception) { Log.w("Dingus", "frame rate reset", e) }
        switched = false
    }
}
