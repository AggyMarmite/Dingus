package app.dingus.tv

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import com.wireguard.android.backend.GoBackend
import com.wireguard.android.backend.Tunnel
import com.wireguard.config.Config
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Dingus's own VPN (WireGuard). Only Dingus's traffic goes through it – other apps on the Fire TV aren't affected.
 *
 * Two kinds of provider:
 *  - Surfshark: you give your WireGuard private key and tunnel address once; the locations come from Surfshark's
 *    public server list.
 *  - WireGuard config files (ProtonVPN and others): each file you add is a location.
 */
object Vpn {
    const val SURFSHARK = "surfshark"
    const val CONFIGS = "configs"

    enum class Status { OFF, CONNECTING, ON, ERROR }

    /** A place to connect to. For Surfshark: host + public key; for config files: the file itself. */
    class Location(
        val id: String, val country: String, val city: String, val countryCode: String,
        val host: String = "", val publicKey: String = "", val load: Int = -1, val conf: String? = null,
    ) {
        val label: String get() = if (city.isNotEmpty() && city != country) "$country – $city" else country
    }

    @Volatile var status = Status.OFF; private set
    @Volatile var message = ""; private set
    @Volatile var connectedTo: Location? = null; private set
    private val listeners = ArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    private val lock = Mutex()
    private lateinit var app: Context
    private val backend by lazy { GoBackend(app) }
    private val tunnel = object : Tunnel {
        override fun getName() = "dingus"
        override fun onStateChange(newState: Tunnel.State) {
            if (newState == Tunnel.State.DOWN && status == Status.ON) set(Status.OFF, "")
        }
    }

    fun init(ctx: Context) { app = ctx.applicationContext }

    fun onChanged(l: () -> Unit) { synchronized(listeners) { listeners.add(l) } }
    fun removeListener(l: () -> Unit) { synchronized(listeners) { listeners.remove(l) } }
    private fun set(s: Status, msg: String) {
        status = s; message = msg
        Ui.test("vpn ${s.name.lowercase()} $msg")
        val ls = synchronized(listeners) { listeners.toList() }
        main.post { ls.forEach { it() } }
    }

    /** Short line for the screens: "VPN · United Kingdom – London". */
    fun badge(): String? = when (status) {
        Status.ON -> "● VPN  ${connectedTo?.label ?: ""}".trim()
        Status.CONNECTING -> "○ VPN connecting…"
        Status.ERROR -> "VPN not connected"
        Status.OFF -> null
    }

    // ------------------------------------------------------------------ settings

    var provider: String
        get() = Store.prefsString("vpnProvider", SURFSHARK)
        set(v) = Store.putString("vpnProvider", v)
    var privateKey: String
        get() = Store.prefsString("vpnKey", "")
        set(v) = Store.putString("vpnKey", v.trim())
    var address: String
        get() = Store.prefsString("vpnAddress", "")
        set(v) = Store.putString("vpnAddress", v.trim())
    var locationId: String
        get() = Store.prefsString("vpnLocation", "")
        set(v) = Store.putString("vpnLocation", v)
    /** Connect when Dingus starts. */
    var auto: Boolean
        get() = Store.prefsString("vpnAuto", "0") == "1"
        set(v) = Store.putString("vpnAuto", if (v) "1" else "0")

    /** Your WireGuard config files (name → text). */
    var configs: List<Pair<String, String>>
        get() = try {
            val a = JSONArray(Store.prefsString("vpnConfigs", "[]"))
            (0 until a.length()).map { a.getJSONObject(it).let { o -> o.getString("n") to o.getString("c") } }
        } catch (e: Exception) { emptyList() }
        set(v) {
            val a = JSONArray(); v.forEach { a.put(JSONObject().put("n", it.first).put("c", it.second)) }
            Store.putString("vpnConfigs", a.toString())
        }

    /** Adds (or replaces, by name) a config file; checks it's a WireGuard config first. */
    fun addConfig(name: String, text: String) {
        Config.parse(text.byteInputStream()) // throws if it isn't a usable WireGuard config
        val n = name.trim().ifEmpty { "Location ${configs.size + 1}" }
        configs = configs.filter { it.first != n } + (n to text.trim())
        Ui.test("vpn-imported $n")
    }

    val ready: Boolean get() = when (provider) {
        SURFSHARK -> privateKey.isNotEmpty() && address.isNotEmpty()
        else -> configs.isNotEmpty()
    }

    // ------------------------------------------------------------------ locations

    @Volatile private var surfshark: List<Location>? = null

    /** The locations for the chosen provider (Surfshark's list is fetched once, then kept for a day). */
    suspend fun locations(): List<Location> = withContext(Dispatchers.IO) {
        if (provider != SURFSHARK) return@withContext configs.map { (n, c) -> Location("cfg:$n", n, "", "", conf = c) }
        surfshark?.let { return@withContext it }
        val cache = File(app.filesDir, "surfshark.json")
        val text = if (cache.exists() && System.currentTimeMillis() - cache.lastModified() < 86_400_000L) cache.readText()
        else try {
            Net.open("https://api.surfshark.com/v4/server/clusters/generic", 20_000).use { it.readBytes().toString(Charsets.UTF_8) }
                .also { cache.writeText(it) }
        } catch (e: Exception) { if (cache.exists()) cache.readText() else throw e }
        val a = JSONArray(text)
        val list = ArrayList<Location>()
        for (i in 0 until a.length()) {
            val o = a.getJSONObject(i)
            val host = o.optString("connectionName")
            val key = o.optString("pubKey")
            if (host.isEmpty() || key.isEmpty()) continue
            list.add(Location(host, o.optString("country"), o.optString("location"), o.optString("countryCode"),
                host, key, o.optInt("load", -1)))
        }
        list.sortWith(compareBy({ it.country }, { it.city }, { it.load }))
        surfshark = list
        list
    }

    fun current(list: List<Location>): Location? = list.firstOrNull { it.id == locationId } ?: list.firstOrNull()

    // ------------------------------------------------------------------ connecting

    private fun configFor(loc: Location): Config {
        val text = loc.conf ?: """
            [Interface]
            PrivateKey = $privateKey
            Address = $address
            DNS = 162.252.172.57, 149.154.159.92

            [Peer]
            PublicKey = ${loc.publicKey}
            Endpoint = ${loc.host}:51820
            AllowedIPs = 0.0.0.0/0
            PersistentKeepalive = 25
        """.trimIndent()
        return Config.parse(onlyDingus(text).byteInputStream())
    }

    /** Puts "only Dingus" in the [Interface] section (replacing any app list the file had). */
    private fun onlyDingus(text: String): String {
        val lines = text.lines().filterNot { it.trim().startsWith("IncludedApplications", true) || it.trim().startsWith("ExcludedApplications", true) }
        val i = lines.indexOfFirst { it.trim().equals("[Interface]", true) }
        if (i < 0) return text
        return (lines.subList(0, i + 1) + "IncludedApplications = ${app.packageName}" + lines.subList(i + 1, lines.size)).joinToString("\n")
    }

    /** Android's one-time "allow this app to set up a VPN" question: null when already allowed. */
    fun permissionIntent(ctx: Context) = android.net.VpnService.prepare(ctx)

    /** Connects to [loc] (or the saved location). Returns an error message, or null when connected. */
    suspend fun connect(loc: Location? = null): String? = lock.withLock {
        withContext(Dispatchers.IO) {
            try {
                if (!ready) return@withContext "Set up your VPN first".also { set(Status.ERROR, it) }
                if (permissionIntent(app) != null) return@withContext "Dingus needs permission to use a VPN – open Settings → VPN".also { set(Status.ERROR, it) }
                val target = loc ?: current(locations()) ?: return@withContext "No VPN locations".also { set(Status.ERROR, it) }
                set(Status.CONNECTING, target.label)
                backend.setState(tunnel, Tunnel.State.UP, configFor(target))
                locationId = target.id
                connectedTo = target
                set(Status.ON, target.label)
                null
            } catch (e: Throwable) {
                Log.w("Dingus", "vpn", e)
                val msg = when {
                    e.message?.contains("key", true) == true -> "Your private key doesn't look right"
                    else -> e.message ?: e.javaClass.simpleName
                }
                set(Status.ERROR, msg)
                msg
            }
        }
    }

    suspend fun disconnect() = lock.withLock {
        withContext(Dispatchers.IO) {
            try { backend.setState(tunnel, Tunnel.State.DOWN, null) } catch (e: Throwable) { Log.w("Dingus", "vpn down", e) }
            connectedTo = null
            set(Status.OFF, "")
        }
    }

    /** At start: if "connect when Dingus starts" is on, connect first (so the playlist loads through the VPN). */
    suspend fun startup() {
        if (!auto || status == Status.ON || !ready) return
        withTimeoutOrNull(15_000) { connect() }
    }
}
