package app.dingus.tv

import android.view.View
import android.widget.TextView

/**
 * The VPN's state next to the clock: a shield (or the word "VPN", Settings → Look) – green when connected, grey and
 * pulsing while connecting, red if it dropped; nothing at all while it's off.
 */
object VpnIndicator {
    fun bind(v: TextView) {
        val s = Vpn.status
        if (s == Vpn.Status.OFF) { stopPulse(v); v.visibility = View.GONE; return }
        v.visibility = View.VISIBLE
        val colour = when (s) { Vpn.Status.ON -> 0xFF4ADE80.toInt(); Vpn.Status.CONNECTING -> 0xFF9AA4B2.toInt(); else -> 0xFFF87171.toInt() }
        if (Store.vpnIndicator == "text") {
            v.setCompoundDrawablesRelativeWithIntrinsicBounds(null, null, null, null)
            v.text = "VPN"
            v.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 13f)
            v.setTextColor(colour)
            v.setTypeface(null, android.graphics.Typeface.BOLD)
            v.letterSpacing = 0.06f
        } else {
            v.text = ""
            // (No text line under the shield, so it lines up with the place name beside it.)
            v.setTextSize(android.util.TypedValue.COMPLEX_UNIT_PX, 1f)
            val icon = when (s) { Vpn.Status.ON -> R.drawable.vpn_shield_on; Vpn.Status.CONNECTING -> R.drawable.vpn_shield_busy; else -> R.drawable.vpn_shield_down }
            // On top (with no text) so it sits centred under the clock.
            v.setCompoundDrawablesRelativeWithIntrinsicBounds(0, icon, 0, 0)
        }
        v.contentDescription = when (s) { Vpn.Status.ON -> "VPN connected"; Vpn.Status.CONNECTING -> "VPN connecting"; else -> "VPN not connected" }
        if (s == Vpn.Status.CONNECTING) startPulse(v) else stopPulse(v)
    }

    private fun startPulse(v: View) {
        if (v.getTag(R.id.vpnPulse) != null || Ui.lite) return
        val a = android.animation.ObjectAnimator.ofFloat(v, View.ALPHA, 1f, 0.35f).apply {
            duration = 700; repeatMode = android.animation.ValueAnimator.REVERSE; repeatCount = android.animation.ValueAnimator.INFINITE; start()
        }
        v.setTag(R.id.vpnPulse, a)
        // (The screen closing stops it: an endless animation would otherwise keep the old screen alive.)
        v.addOnAttachStateChangeListener(object : View.OnAttachStateChangeListener {
            override fun onViewAttachedToWindow(x: View) {}
            override fun onViewDetachedFromWindow(x: View) { stopPulse(x); x.removeOnAttachStateChangeListener(this) }
        })
    }

    private fun stopPulse(v: View) {
        (v.getTag(R.id.vpnPulse) as? android.animation.Animator)?.cancel()
        v.setTag(R.id.vpnPulse, null)
        v.alpha = 1f
    }
}
