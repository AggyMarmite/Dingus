package app.dingus.tv

import android.app.Activity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/**
 * Settings → VPN → Switch server if slow: a stream that buffers three times in a minute while the VPN is on moves
 * by itself to a quicker server in the same country (by ping and how busy it is), with a note saying so. Once per stream.
 */
object SlowVpn {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val events = ArrayList<Long>()
    private var askedFor: String? = null
    private var busy = false

    fun onBuffering(a: Activity, stream: String) {
        if (!Store.vpnSwitchSlow || Vpn.status != Vpn.Status.ON || busy) return
        val now = System.currentTimeMillis()
        events.add(now); events.removeAll { now - it > 60_000 }
        if (events.size < 3 || askedFor == stream) return
        askedFor = stream; events.clear(); busy = true
        scope.launch {
            try {
                val here = Vpn.connectedTo ?: return@launch
                val same = try { Vpn.locations() } catch (_: Exception) { return@launch }.filter { it.countryCode == here.countryCode && it.id != here.id }.take(20)
                if (same.isEmpty()) return@launch
                val p = Vpn.pings(same + here)
                VpnControls.pings.putAll(p)
                val cur = p[here.id]
                val best = same.filter { p[it.id] != null }.minByOrNull { p[it.id]!! + it.load.coerceAtLeast(0) } ?: return@launch
                if (cur != null && p[best.id]!! + best.load.coerceAtLeast(0) >= cur + here.load.coerceAtLeast(0)) return@launch
                if (a.isFinishing || a.isDestroyed) return@launch
                val name = best.city.ifEmpty { best.label }
                Ui.test("vpn-slow-switch $name")
                Notice.done("⛨  Kept buffering  ·  moving to $name, which answers quicker", 6_000)
                Vpn.locationId = best.id; Vpn.connect(best)
            } finally { busy = false }
        }
    }
}
