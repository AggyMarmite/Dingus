package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.widget.LinearLayout
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/** Settings → VPN: provider, your key or config files, location, on/off. */
class VpnActivity : BaseActivity() {
    companion object {
        const val PHONE = "phone"
        private const val ASK_PERMISSION = 7
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var list: LinearLayout
    private val subtitles = ArrayList<Pair<View, () -> String>>()
    private var phone: PhoneSetup? = null
    private var phoneDialog: android.app.AlertDialog? = null
    private var locations: List<Vpn.Location> = emptyList()
    private var connectAfterPermission = false
    private val vpnListener: () -> Unit = { update() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        list = findViewById(R.id.list)
        list.findViewById<TextView>(R.id.settingsTitle)?.text = "VPN"

        header("Built-in VPN")
        val first = row("VPN", { statusLine() }) { toggle() }
        statusRow = first
        row("Location", { locationLine() }) { chooseLocation() }
        row("Provider", { if (Vpn.provider == Vpn.SURFSHARK) "Surfshark" else "WireGuard config files (ProtonVPN and others)" }) { chooseProvider() }
        row("Set up from your phone", { setupLine() }) { phoneSetup() }
        row("Type them in with the remote", { "Private key and tunnel address (Surfshark), or a link to a config file" }) { typeIn() }
        row("Connect when Dingus starts", { (if (Vpn.auto) "On" else "Off") + "  ·  your playlist and streams go through the VPN from the start" }) {
            Vpn.auto = !Vpn.auto; update()
        }
        row("Remove config files", { val n = Vpn.configs.size; if (n == 0) "None added" else "$n added  ·  choose ones to remove" }) { removeConfigs() }
        header("About")
        row("Only Dingus uses this VPN", { "Other apps on your Fire TV aren't affected." }) {}
        first.requestFocus()

        Vpn.onChanged(vpnListener)
        scope.launch { loadLocations() }
        if (intent.getBooleanExtra(PHONE, false)) phoneSetup()
    }

    override fun onResume() { super.onResume(); update(); Ui.test("vpn-screen") }
    override fun onDestroy() { Vpn.removeListener(vpnListener); phone?.stop(); scope.cancel(); super.onDestroy() }

    private suspend fun loadLocations() {
        locations = try { Vpn.locations() } catch (e: Exception) { emptyList() }
        Ui.test("vpn-locations ${locations.size}")
        update()
    }

    // ------------------------------------------------------------------ rows

    private fun header(text: String) {
        val v = LayoutInflater.from(this).inflate(R.layout.item_heading, list, false) as TextView
        v.text = text.uppercase()
        list.addView(v)
    }

    private fun row(title: String, subtitle: () -> String, click: () -> Unit): View {
        val v = LayoutInflater.from(this).inflate(R.layout.item_setting, list, false)
        v.findViewById<TextView>(R.id.title).text = title
        v.setOnClickListener { click() }
        list.addView(v)
        subtitles.add(v to subtitle)
        return v
    }

    private var statusRow: View? = null

    private fun update() {
        for ((v, f) in subtitles) v.findViewById<TextView>(R.id.subtitle).text = f()
        // The status card on top: big, and green while connected (white when the remote's on it, like every row).
        statusRow?.let { v ->
            val d = resources.displayMetrics.density
            val on = Vpn.status == Vpn.Status.ON
            v.findViewById<TextView>(R.id.title).apply {
                text = when (Vpn.status) { Vpn.Status.ON -> "⛨  Connected"; Vpn.Status.CONNECTING -> "⛨  Connecting…"; else -> "⛨  Not connected" }
                textSize = 21f; setTypeface(null, android.graphics.Typeface.BOLD)
            }
            v.background = android.graphics.drawable.StateListDrawable().apply {
                addState(intArrayOf(android.R.attr.state_focused), android.graphics.drawable.GradientDrawable().apply { setColor(0xFFFFFFFF.toInt()); cornerRadius = 18 * d })
                addState(intArrayOf(), android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
                    if (on) intArrayOf(0x5522C55E, 0x8C1E2636.toInt()) else intArrayOf(0x8C1E2636.toInt(), 0x8C1E2636.toInt())).apply { cornerRadius = 18 * d })
            }
            v.setPadding((22 * d).toInt(), (18 * d).toInt(), (22 * d).toInt(), (18 * d).toInt())
        }
    }

    private fun statusLine() = when (Vpn.status) {
        Vpn.Status.ON -> "On  ·  connected to ${Vpn.connectedTo?.label ?: ""}"
        Vpn.Status.CONNECTING -> "Connecting to ${Vpn.message}…"
        Vpn.Status.ERROR -> "Off  ·  ${Vpn.message}"
        Vpn.Status.OFF -> if (Vpn.ready) "Off  ·  press OK to connect" else "Off  ·  set up your provider below first"
    }

    private fun locationLine(): String {
        if (Vpn.provider == Vpn.CONFIGS && Vpn.configs.isEmpty()) return "Add WireGuard config files first"
        val cur = Vpn.current(locations) ?: return if (locations.isEmpty()) "Loading locations…" else "Choose a location"
        return cur.label + (if (cur.load >= 0) "  ·  ${cur.load}% busy" else "") + "  ·  ${locations.size} locations"
    }

    private fun setupLine(): String = when {
        Vpn.provider == Vpn.SURFSHARK && Vpn.privateKey.isNotEmpty() -> "Key and address saved (${Vpn.address})  ·  send new ones from your phone"
        Vpn.provider == Vpn.CONFIGS && Vpn.configs.isNotEmpty() -> "${Vpn.configs.size} config files  ·  add more from your phone"
        else -> "Easiest: paste your key and address (or config files) on your phone"
    }

    // ------------------------------------------------------------------ actions

    private fun toggle() {
        if (Vpn.status == Vpn.Status.ON || Vpn.status == Vpn.Status.CONNECTING) { scope.launch { Vpn.disconnect() }; return }
        connect(null)
    }

    private fun connect(loc: Vpn.Location?) {
        if (!Vpn.ready) { phoneSetup(); return }
        // Android asks once whether Dingus may set up a VPN.
        val ask = Vpn.permissionIntent(this)
        if (ask != null) {
            connectAfterPermission = true
            pendingLocation = loc
            try { startActivityForResult(ask, ASK_PERMISSION) } catch (e: Exception) {
                Dialogs.builder(this).setTitle("VPN").setMessage("This device didn't let Dingus ask for VPN permission.").setPositiveButton("OK", null).show()
            }
            return
        }
        scope.launch {
            val err = Vpn.connect(loc)
            if (err != null) Dialogs.builder(this@VpnActivity).setTitle("Couldn't connect").setMessage(err).setPositiveButton("OK", null).show()
        }
    }

    private var pendingLocation: Vpn.Location? = null

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == ASK_PERMISSION && connectAfterPermission) {
            connectAfterPermission = false
            if (resultCode == RESULT_OK) connect(pendingLocation) else update()
        }
    }

    private fun chooseLocation() {
        if (locations.isEmpty()) { scope.launch { loadLocations(); if (locations.isNotEmpty()) chooseLocation() }; return }
        if (Vpn.provider == Vpn.CONFIGS) { pick(locations); return }
        // Surfshark has many: country first, then the city if there's more than one.
        val byCountry = locations.groupBy { it.country }
        val countries = byCountry.keys.toList()
        val cur = Vpn.current(locations)
        Dialogs.builder(this).setTitle("Country")
            .setSingleChoiceItems(countries.toTypedArray(), countries.indexOf(cur?.country)) { d, i ->
                d.dismiss()
                val cities = byCountry.getValue(countries[i])
                if (cities.size == 1) select(cities[0]) else pick(cities)
            }
            .show()
    }

    private fun pick(options: List<Vpn.Location>) {
        val cur = Vpn.current(locations)
        val names = options.map { (if (it.city.isNotEmpty()) it.city else it.label) + if (it.load >= 0) "   (${it.load}% busy)" else "" }
        Dialogs.builder(this).setTitle(options.firstOrNull()?.country?.takeIf { Vpn.provider == Vpn.SURFSHARK } ?: "Location")
            .setSingleChoiceItems(names.toTypedArray(), options.indexOfFirst { it.id == cur?.id }) { d, i -> d.dismiss(); select(options[i]) }
            .show()
    }

    /** Picking a location connects to it straight away (or just remembers it while the VPN is off). */
    private fun select(loc: Vpn.Location) {
        Vpn.locationId = loc.id
        update()
        if (Vpn.status == Vpn.Status.ON) connect(loc)
    }

    private fun chooseProvider() {
        val opts = listOf("Surfshark", "WireGuard config files (ProtonVPN and others)")
        Dialogs.builder(this).setTitle("Provider")
            .setSingleChoiceItems(opts.toTypedArray(), if (Vpn.provider == Vpn.SURFSHARK) 0 else 1) { d, i ->
                d.dismiss()
                Vpn.provider = if (i == 0) Vpn.SURFSHARK else Vpn.CONFIGS
                Vpn.locationId = ""
                locations = emptyList()
                update()
                scope.launch { loadLocations() }
            }
            .show()
    }

    private fun phoneSetup() {
        phone?.stop()
        val p = PhoneSetup { msg ->
            phoneDialog?.setMessage("$msg ✓")
            locations = emptyList()
            scope.launch { loadLocations() }
            update()
        }
        val url = p.start()
        if (url == null) {
            Dialogs.builder(this).setTitle("No network").setMessage("Connect the Fire TV to Wi-Fi or Ethernet first.").setPositiveButton("OK", null).show()
            return
        }
        phone = p
        phoneDialog = Dialogs.builder(this).setTitle("Set up from your phone")
            .setMessage("On a phone or computer on the same Wi-Fi, open:\n\n$url\n\nand enter the code  ${p.code}\n\nThen paste your Surfshark private key and tunnel address, or your WireGuard config files.")
            .setPositiveButton("Done") { _, _ -> }
            .setOnDismissListener { p.stop(); if (phone === p) phone = null }
            .show()
    }

    private fun typeIn() {
        val opts = listOf("Surfshark private key", "Surfshark tunnel address", "WireGuard config file from a link")
        Dialogs.builder(this).setTitle("Type them in")
            .setItems(opts.toTypedArray()) { _, i ->
                when (i) {
                    0 -> Dialogs.input(this, "WireGuard private key", Vpn.privateKey, "e.g. aBcD…=") { Vpn.privateKey = it; Vpn.provider = Vpn.SURFSHARK; update() }
                    1 -> Dialogs.input(this, "Tunnel address", Vpn.address, "e.g. 10.14.0.2/16") { Vpn.address = it; Vpn.provider = Vpn.SURFSHARK; update() }
                    2 -> Dialogs.input(this, "Link to a .conf file", "", "http://…/uk.conf") { url ->
                        if (url.isEmpty()) return@input
                        scope.launch {
                            val msg = try {
                                val text = kotlinx.coroutines.withContext(Dispatchers.IO) { Net.open(url, 15_000).use { it.readBytes().toString(Charsets.UTF_8) } }
                                Vpn.addConfig(url.substringAfterLast('/').substringBefore('?').removeSuffix(".conf"), text)
                                Vpn.provider = Vpn.CONFIGS
                                locations = emptyList(); loadLocations()
                                "Added ✓"
                            } catch (e: Exception) { "Couldn't add it: ${Net.friendly(e)}" }
                            Dialogs.builder(this@VpnActivity).setTitle("Config file").setMessage(msg).setPositiveButton("OK", null).show()
                            update()
                        }
                    }
                }
            }
            .show()
    }

    private fun removeConfigs() {
        val all = Vpn.configs
        if (all.isEmpty()) return
        val remove = BooleanArray(all.size)
        Dialogs.builder(this).setTitle("Remove config files")
            .setMultiChoiceItems(all.map { it.first }.toTypedArray(), remove) { _, i, on -> remove[i] = on }
            .setPositiveButton("Remove") { _, _ ->
                Vpn.configs = all.filterIndexed { i, _ -> !remove[i] }
                locations = emptyList()
                scope.launch { loadLocations() }
                update()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }
}
