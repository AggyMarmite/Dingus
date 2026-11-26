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

/** Settings → VPN → More VPN settings: the provider, your key or config files, connecting at start. (On/off, Quick Connect and Location are in Settings → VPN.) */
class VpnActivity : BaseActivity() {
    companion object {
        const val PHONE = "phone"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var list: LinearLayout
    private val subtitles = ArrayList<Pair<View, () -> String>>()
    private var phone: PhoneSetup? = null
    private var phoneDialog: android.app.AlertDialog? = null
    private val controls: VpnControls by lazy { VpnControls(this, scope, { update() }, { phoneSetup() }) }
    private var locations: List<Vpn.Location>
        get() = controls.locations
        set(v) { controls.locations = v }
    private val vpnListener: () -> Unit = { update() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        list = findViewById(R.id.list)
        list.findViewById<TextView>(R.id.settingsTitle)?.text = "More VPN settings"

        header("Set up")
        val first = row("Provider", { controls.providerName() }) { chooseProvider() }
        row("Set up from your phone", { setupLine() }) { phoneSetup() }
        row("Type them in with the remote", { "Private key and tunnel address, or a link to a config file" }) { typeIn() }
        row("Remove config files", { val n = Vpn.configs.size; if (n == 0) "None added" else "$n added  ·  choose ones to remove" }) { removeConfigs() }
        header("When it's on")
        row("Connect when Dingus starts", { onOff(Vpn.auto) + "your playlist and streams go through the VPN from the start" }) {
            Vpn.auto = !Vpn.auto; update()
        }
        row("Reconnect by itself", { onOff(Store.vpnReconnect) + "if the VPN drops, connect again: the same place, or the least busy one nearby" }) {
            Store.vpnReconnect = !Store.vpnReconnect; update()
        }
        row("Find a location that works", { onOff(VpnFallback.enabled) + "if your playlist can't connect through this location, try others until one does" }) {
            VpnFallback.enabled = !VpnFallback.enabled; update()
        }
        row("Kill switch", { onOff(Store.vpnKillSwitch) + "if the VPN drops, stop playing until it's back" }) {
            Store.vpnKillSwitch = !Store.vpnKillSwitch; update()
        }
        row("Switch server if slow", { onOff(Store.vpnSwitchSlow) + "move to a quicker server when streams keep buffering" }) {
            Store.vpnSwitchSlow = !Store.vpnSwitchSlow; update()
        }
        header("About")
        row("Only Dingus uses this VPN", { "Other apps on your Fire TV aren't affected." }) {}
        first.requestFocus()

        Vpn.onChanged(vpnListener)
        scope.launch { loadLocations() }
        if (intent.getBooleanExtra(PHONE, false)) phoneSetup()
    }

    override fun onResume() { super.onResume(); update(); Ui.test("vpn-screen") }
    override fun onDestroy() { Vpn.removeListener(vpnListener); phone?.stop(); scope.cancel(); super.onDestroy() }

    private suspend fun loadLocations() = controls.loadLocations()

    // ------------------------------------------------------------------ rows

    private fun header(text: String) {
        val v = LayoutInflater.from(this).inflate(R.layout.item_heading, list, false) as TextView
        v.text = text.uppercase()
        list.addView(v)
    }

    private fun row(title: String, subtitle: () -> String, click: () -> Unit): View {
        val v = LayoutInflater.from(this).inflate(R.layout.item_setting, list, false)
        v.findViewById<TextView>(R.id.title).text = SettingsIcons.inline(this, title)
        v.setOnClickListener { click() }
        list.addView(v)
        subtitles.add(v to subtitle)
        return v
    }

    private fun onOff(on: Boolean) = if (on) "On  ·  " else "Off  ·  "

    private fun update() {
        for ((v, f) in subtitles) v.findViewById<TextView>(R.id.subtitle).text = f()
    }

    private fun setupLine(): String = when {
        Vpn.provider == Vpn.SURFSHARK && Vpn.privateKey.isNotEmpty() -> "Key and address saved (${Vpn.address})  ·  send new ones from your phone"
        Vpn.provider == Vpn.PROTON && Vpn.protonKey.isNotEmpty() -> "Key and address saved (${Vpn.protonAddress})  ·  send new ones from your phone"
        Vpn.provider == Vpn.CONFIGS && Vpn.configs.isNotEmpty() -> "${Vpn.configs.size} config files  ·  add more from your phone"
        Vpn.provider in listOf(Vpn.MULLVAD, Vpn.IVPN, Vpn.NORD) && Vpn.keyFor().isNotEmpty() -> "Key saved${Vpn.addressFor().let { if (it.isEmpty()) "" else " ($it)" }}  ·  send new ones from your phone"
        Vpn.provider == Vpn.NORD -> "Paste a NordLynx config, or type in your NordVPN access token (Nord account → Manual setup)"
        Vpn.provider == Vpn.MULLVAD || Vpn.provider == Vpn.IVPN -> "Paste one WireGuard config file from ${Vpn.providerName(Vpn.provider)}: its key and address are all that's needed"
        else -> "Easiest: paste your key and address (or config files) on your phone"
    }

    // ------------------------------------------------------------------ actions

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        controls.onActivityResult(requestCode, resultCode)
    }

    private fun chooseProvider() {
        val ids = Vpn.listed + Vpn.CONFIGS
        val opts = ids.map { Vpn.providerName(it) }
        Dialogs.builder(this).setTitle("Provider")
            .setSingleChoiceItems(opts.toTypedArray(), ids.indexOf(Vpn.provider).coerceAtLeast(0)) { d, i ->
                d.dismiss()
                Vpn.provider = ids[i]
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
            .setMessage("On a phone or computer on the same Wi-Fi, open:\n\n$url\n\nand enter the code  ${p.code}\n\nThen paste your VPN private key and tunnel address, or WireGuard config files (for Mullvad, IVPN or NordVPN, one config file is enough).")
            .setPositiveButton("Done") { _, _ -> }
            .setOnDismissListener { p.stop(); if (phone === p) phone = null }
            .show()
    }

    private fun typeIn() {
        val pv = if (Vpn.provider == Vpn.CONFIGS) Vpn.SURFSHARK else Vpn.provider
        val name = Vpn.providerName(pv)
        val opts = listOf("$name private key", "$name tunnel address", "WireGuard config file from a link") +
            (if (pv == Vpn.NORD) listOf("NordVPN access token (gets your key)") else emptyList())
        Dialogs.builder(this).setTitle("Type them in")
            .setItems(opts.toTypedArray()) { _, i ->
                when (i) {
                    0 -> Dialogs.input(this, "WireGuard private key", Vpn.keyFor(pv), "e.g. aBcD…=") {
                        Vpn.setKey(pv, it); Vpn.provider = pv; locations = emptyList(); scope.launch { loadLocations() }; update()
                    }
                    1 -> Dialogs.input(this, "Tunnel address", Vpn.addressFor(pv), when (pv) { Vpn.PROTON -> "e.g. 10.2.0.2/32"; Vpn.NORD -> "10.5.0.2/32"; else -> "e.g. 10.14.0.2/16" }) {
                        Vpn.setAddress(pv, it); Vpn.provider = pv; update()
                    }
                    3 -> Dialogs.input(this, "NordVPN access token", "", "From your Nord account → Manual setup") { t ->
                        if (t.isEmpty()) return@input
                        scope.launch {
                            val msg = try { Vpn.setKey(Vpn.NORD, Vpn.nordKeyFromToken(t)); Vpn.provider = Vpn.NORD; locations = emptyList(); loadLocations(); "Your NordLynx key is saved ✓" }
                            catch (e: Exception) { "Couldn't get your key: ${Net.friendly(e)}" }
                            Dialogs.builder(this@VpnActivity).setTitle("NordVPN").setMessage(msg).setPositiveButton("OK", null).show()
                            update()
                        }
                    }
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
