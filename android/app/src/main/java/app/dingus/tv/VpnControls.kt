package app.dingus.tv

import android.app.Activity
import android.content.Intent
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject

/**
 * The VPN's everyday controls – on/off, Quick Connect, Location and the IP address – shared by Settings → VPN and
 * More VPN settings, so both behave the same. The screen forwards [onActivityResult] (Android's VPN permission).
 */
class VpnControls(
    private val a: Activity,
    private val scope: CoroutineScope,
    /** Something changed: the screen redraws its rows. */
    private val update: () -> Unit,
    /** The VPN isn't set up yet: the screen takes you to setting it up. */
    private val needsSetup: () -> Unit,
) {
    companion object {
        const val ASK_PERMISSION = 7

        /** The address the internet sees, and where ("185.159.157.12 · London, GB"), and when it was looked up. */
        @Volatile var ip: String? = null; private set
        @Volatile private var ipFor: Vpn.Status? = null
        /** The server it was looked up through (so switching server looks it up again). */
        @Volatile private var ipServer: String? = null
        @Volatile private var looking = false
        /** Ping (ms) by location id, measured when a list of places is opened. */
        val pings = java.util.concurrent.ConcurrentHashMap<String, Int>()

        /** Looks the IP address up again (through the VPN when it's on: Dingus's own connections are the ones it carries). */
        suspend fun lookUpIp(): String? = withContext(Dispatchers.IO) {
            if (looking) return@withContext ip
            looking = true
            try {
                val status = Vpn.status
                val o = JSONObject(Net.open("https://ipinfo.io/json", 8_000).use { it.readBytes().toString(Charsets.UTF_8) })
                val where = listOf(o.optString("city"), o.optString("country")).filter { it.isNotBlank() }.joinToString(", ")
                ip = o.optString("ip").ifBlank { null }?.let { if (where.isEmpty()) it else "$it · $where" }
                ipFor = status
                ipServer = Vpn.connectedTo?.id
                Ui.test("vpn-ip ${ip ?: "none"}")
                ip
            } catch (e: Exception) { ip = null; ipFor = null; null } finally { looking = false }
        }

        /** Whether the address shown was looked up with the VPN as it is now, through the same server (otherwise it's out of date). */
        val ipCurrent: Boolean get() = ip != null && ipFor == Vpn.status && ipServer == Vpn.connectedTo?.id
    }

    var locations: List<Vpn.Location> = emptyList()
    private var connectAfterPermission = false
    private var pendingLocation: Vpn.Location? = null

    suspend fun loadLocations() {
        locations = try { Vpn.locations() } catch (e: Exception) { emptyList() }
        Ui.test("vpn-locations ${locations.size}")
        update()
    }

    /** The VPN changed: look the address up again once it has settled (connected, or off). */
    fun onVpnChanged() {
        if ((Vpn.status == Vpn.Status.ON || Vpn.status == Vpn.Status.OFF) && !ipCurrent) scope.launch { kotlinx.coroutines.delay(800); lookUpIp(); update() }
    }

    // ------------------------------------------------------------------ what the rows say

    fun statusTitle() = when (Vpn.status) { Vpn.Status.ON -> "⛨  Connected"; Vpn.Status.CONNECTING -> "⛨  Connecting…"; else -> "⛨  Not connected" }

    fun statusLine() = when (Vpn.status) {
        Vpn.Status.ON -> "On  ·  ${Vpn.connectedTo?.label ?: ""}  ·  ${providerName()}  ·  OK to turn off"
        Vpn.Status.CONNECTING -> "Connecting to ${Vpn.message}…"
        Vpn.Status.ERROR -> "Off  ·  ${Vpn.message}"
        Vpn.Status.OFF -> if (Vpn.ready) "Off  ·  press OK to connect" else "Off  ·  set it up in More VPN settings first"
    }

    fun providerName() = Vpn.providerName(Vpn.provider)

    fun quickLine(): String {
        val here = Vpn.region().let { if (it.isEmpty()) "" else java.util.Locale("", it).getDisplayCountry(java.util.Locale.ENGLISH) }
        return if (Vpn.status == Vpn.Status.ON) "Connected to ${Vpn.connectedTo?.label ?: ""}  ·  OK to pick the least busy again"
        else "Least busy server " + if (here.isEmpty()) "anywhere" else "near you ($here)"
    }

    fun locationLine(): String {
        if (Vpn.provider == Vpn.CONFIGS && Vpn.configs.isEmpty()) return "Add WireGuard config files first"
        val cur = Vpn.current(locations) ?: return if (locations.isEmpty()) "Loading locations…" else "Choose a location"
        return cur.label + (if (cur.load >= 0) "  ·  ${cur.load}% busy" else "") + (pings[cur.id]?.let { "  ·  $it ms" } ?: "") + "  ·  ${locations.size} locations"
    }

    fun ipLine(): String = when {
        ip == null && looking -> "Looking it up…"
        ip == null -> "What websites and your provider see  ·  OK to look it up"
        Vpn.status == Vpn.Status.ON && ipCurrent -> "What websites and your provider see: the VPN's address, so it's working  ·  OK to check again"
        Vpn.status == Vpn.Status.OFF && ipCurrent -> "Your home connection's address (not hidden)  ·  OK to check again"
        else -> "Checking again…"
    }

    fun ipValue(): String = ip ?: ""

    // ------------------------------------------------------------------ actions

    fun toggle() {
        if (Vpn.status == Vpn.Status.ON || Vpn.status == Vpn.Status.CONNECTING) { scope.launch { Vpn.disconnect() }; return }
        connect(null)
    }

    fun connect(loc: Vpn.Location?) {
        if (!Vpn.ready) { needsSetup(); return }
        // Android asks once whether Dingus may set up a VPN.
        val ask = Vpn.permissionIntent(a)
        if (ask != null) {
            connectAfterPermission = true
            pendingLocation = loc
            try { a.startActivityForResult(ask, ASK_PERMISSION) } catch (e: Exception) {
                Dialogs.builder(a).setTitle("VPN").setMessage("This device didn't let Dingus ask for VPN permission.").setPositiveButton("OK", null).show()
            }
            return
        }
        scope.launch {
            val err = Vpn.connect(loc)
            if (err != null) Dialogs.builder(a).setTitle("Couldn't connect").setMessage(err).setPositiveButton("OK", null).show()
        }
    }

    /** Returns true when it was the VPN permission's answer. */
    fun onActivityResult(requestCode: Int, resultCode: Int): Boolean {
        if (requestCode != ASK_PERMISSION || !connectAfterPermission) return false
        connectAfterPermission = false
        if (resultCode == Activity.RESULT_OK) connect(pendingLocation) else update()
        return true
    }

    fun checkIp() { scope.launch { ip = null; update(); lookUpIp(); update() } }

    fun quickConnect() {
        if (!Vpn.ready) { needsSetup(); return }
        scope.launch {
            if (locations.isEmpty()) loadLocations()
            val pick = Vpn.best(locations)
            if (pick == null) {
                Dialogs.builder(a).setTitle("Quick Connect").setMessage("No VPN locations are available yet. Check the Fire TV's connection and try again.").setPositiveButton("OK", null).show()
                return@launch
            }
            Vpn.locationId = pick.id
            update()
            connect(pick)
        }
    }

    fun chooseLocation() {
        if (!Vpn.ready && Vpn.provider != Vpn.CONFIGS) { needsSetup(); return }
        if (locations.isEmpty()) { scope.launch { loadLocations(); if (locations.isNotEmpty()) chooseLocation() }; return }
        if (Vpn.provider == Vpn.CONFIGS) { pick(locations); return }
        // Surfshark and Proton have many: country first, then the city if there's more than one.
        val byCountry = locations.groupBy { it.country }
        val countries = byCountry.keys.toList()
        val cur = Vpn.current(locations)
        Dialogs.builder(a).setTitle("Country")
            .setSingleChoiceItems(countries.toTypedArray(), countries.indexOf(cur?.country)) { d, i ->
                d.dismiss()
                val cities = byCountry.getValue(countries[i])
                if (cities.size == 1) select(cities[0]) else pick(cities)
            }
            .show()
    }

    private fun pick(options: List<Vpn.Location>) {
        // How quickly each answers (ping), measured first – a second or two.
        if (options.any { !pings.containsKey(it.id) } && options.any { it.host.isNotEmpty() }) {
            Notice.working("Checking each server's speed…")
            scope.launch { pings.putAll(Vpn.pings(options.take(40))); Notice.clear(); showPick(options) }
            return
        }
        showPick(options)
    }

    private fun showPick(options: List<Vpn.Location>) {
        val cur = Vpn.current(locations)
        val names = options.map { l ->
            (if (l.city.isNotEmpty()) l.city else l.label) + listOfNotNull(
                l.load.takeIf { it >= 0 }?.let { "$it% busy" },
                pings[l.id]?.let { "$it ms" + when { it < 50 -> " ●"; it <= 120 -> " ◐"; else -> " ○" } },
            ).joinToString("  ·  ").let { if (it.isEmpty()) "" else "   ($it)" }
        }
        Dialogs.builder(a).setTitle(options.firstOrNull()?.country?.takeIf { Vpn.provider != Vpn.CONFIGS } ?: "Location")
            .setSingleChoiceItems(names.toTypedArray(), options.indexOfFirst { it.id == cur?.id }) { d, i -> d.dismiss(); select(options[i]) }
            .show()
    }

    /** Picking a location connects to it straight away (or just remembers it while the VPN is off). */
    private fun select(loc: Vpn.Location) {
        Vpn.locationId = loc.id
        update()
        if (Vpn.status == Vpn.Status.ON) connect(loc)
    }

    /** Speed test: about 10 seconds, through the VPN when it's on (your normal connection otherwise). */
    fun speedTest(done: () -> Unit) {
        val through = if (Vpn.status == Vpn.Status.ON) "through the VPN (${Vpn.connectedTo?.label ?: ""})" else "your connection (the VPN's off)"
        val d = Dialogs.builder(a).setTitle("Speed test  ·  $through").setMessage("Measuring…").setPositiveButton("Close", null).show()
        val job = scope.launch {
            val mbps = try { Vpn.speedTest { m -> a.runOnUiThread { d.setMessage("%.0f Mbps…".format(m)) } } } catch (e: Exception) { d.setMessage("Couldn't run the test: ${Net.friendly(e)}"); return@launch }
            val verdict = "Good for HD ${if (mbps >= 8) "✓" else "✗"}  ·  Good for 4K ${if (mbps >= 25) "✓" else "✗"} (25 Mbps needed)"
            d.setMessage("%.0f Mbps\n\n%s".format(mbps, verdict))
            Store.vpnLastSpeed = "%.0f Mbps".format(mbps)
            done()
        }
        d.setOnDismissListener { job.cancel() }
    }

    /** The status row: big, and green while connected (white when the remote's on it, like every row). */
    fun styleStatus(v: android.view.View, title: android.widget.TextView) {
        val d = a.resources.displayMetrics.density
        val on = Vpn.status == Vpn.Status.ON
        title.text = statusTitle()
        title.textSize = 21f; title.setTypeface(null, android.graphics.Typeface.BOLD)
        v.background = android.graphics.drawable.StateListDrawable().apply {
            addState(intArrayOf(android.R.attr.state_focused), android.graphics.drawable.GradientDrawable().apply { setColor(0xFFFFFFFF.toInt()); cornerRadius = 18 * d })
            addState(intArrayOf(), android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
                if (on) intArrayOf(0x5522C55E, 0x8C1E2636.toInt()) else intArrayOf(0x8C1E2636.toInt(), 0x8C1E2636.toInt())).apply { cornerRadius = 18 * d })
        }
        v.setPadding((22 * d).toInt(), (18 * d).toInt(), (22 * d).toInt(), (18 * d).toInt())
    }

    /** More VPN settings (provider, phone setup, keys…), or straight to the phone setup. */
    fun openSetup(phone: Boolean = false) {
        a.startActivity(Intent(a, VpnActivity::class.java).putExtra(VpnActivity.PHONE, phone))
    }
}
