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
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.IOException

/**
 * Dingus's own VPN (WireGuard). Only Dingus's traffic goes through it – other apps on the Fire TV aren't affected.
 *
 * Two kinds of provider:
 *  - Surfshark: you give your WireGuard private key and tunnel address once; the locations come from Surfshark's
 *    public server list.
 *  - Proton VPN: the same, with the private key and tunnel address from a Proton WireGuard config; the locations
 *    come from a public copy of Proton's server list (their own list needs a login).
 *  - WireGuard config files (any other provider): each file you add is a location.
 */
object Vpn {
    const val SURFSHARK = "surfshark"
    const val PROTON = "proton"
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
    /** You turned the VPN on (and haven't turned it off): a drop is put right, and the kill switch holds streams. */
    @Volatile var wanted = false; private set
    /** The VPN dropped and is being put right: with the kill switch on, nothing plays meanwhile. */
    val holding: Boolean get() = wanted && Store.vpnKillSwitch && status != Status.ON
    private val listeners = ArrayList<() -> Unit>()
    private val main = Handler(Looper.getMainLooper())
    private val lock = Mutex()
    private lateinit var app: Context
    private val backend by lazy { GoBackend(app) }
    private val tunnel = object : Tunnel {
        override fun getName() = "dingus"
        override fun onStateChange(newState: Tunnel.State) {
            if (newState == Tunnel.State.DOWN && status == Status.ON) { set(Status.ERROR, "VPN dropped"); dropped() }
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
    /** Proton VPN's own key and address (kept apart, so switching provider doesn't lose Surfshark's). */
    var protonKey: String
        get() = Store.prefsString("vpnProtonKey", "")
        set(v) = Store.putString("vpnProtonKey", v.trim())
    var protonAddress: String
        get() = Store.prefsString("vpnProtonAddress", "").ifEmpty { "10.2.0.2/32" }
        set(v) = Store.putString("vpnProtonAddress", v.trim())
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
        PROTON -> protonKey.isNotEmpty()
        else -> configs.isNotEmpty()
    }

    // ------------------------------------------------------------------ locations

    @Volatile private var surfshark: List<Location>? = null
    @Volatile private var proton: List<Location>? = null

    /** The locations for the chosen provider (the server lists are fetched once, then kept for a day). */
    suspend fun locations(): List<Location> = withContext(Dispatchers.IO) {
        when (provider) {
            SURFSHARK -> surfsharkList()
            PROTON -> protonList()
            else -> configs.map { (n, c) -> Location("cfg:$n", n, "", "", conf = c) }
        }
    }

    /** A list from the web, kept in a file for a day (and still used if the web is down). */
    private fun cached(name: String, url: String): String {
        val cache = File(app.filesDir, name)
        return if (cache.exists() && System.currentTimeMillis() - cache.lastModified() < 86_400_000L) cache.readText()
        else try {
            Net.open(url, 20_000).use { it.readBytes().toString(Charsets.UTF_8) }.also { cache.writeText(it) }
        } catch (e: Exception) { if (cache.exists()) cache.readText() else throw e }
    }

    private fun surfsharkList(): List<Location> {
        surfshark?.let { return it }
        val text = cached("surfshark.json", "https://api.surfshark.com/v4/server/clusters/generic")
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
        return list
    }

    /** Proton's server list is public only as a daily copy kept by a community project. */
    private const val PROTON_LIST = "https://raw.githubusercontent.com/tn3w/ProtonVPN-IPs/master/protonvpn_logicals.json"

    private fun protonList(): List<Location> {
        proton?.let { return it }
        val list = parseProton(cached("protonvpn.json", PROTON_LIST))
        if (list.isEmpty()) throw IOException("No Proton VPN servers in the list")
        proton = list
        return list
    }

    /**
     * One place per country and city (the least busy server there), with the free servers as their own entries
     * ("Amsterdam (Free)") for accounts on the free plan. Secure Core and Tor servers are left out.
     */
    internal fun parseProton(text: String): List<Location> {
        val servers = JSONObject(text).getJSONArray("LogicalServers")
        val best = HashMap<String, Location>()
        for (i in 0 until servers.length()) {
            val o = servers.getJSONObject(i)
            if ((o.optInt("Features", 0) and 3) != 0) continue // 1 = Secure Core, 2 = Tor
            if (o.optInt("Status", 1) != 1) continue
            val cc = if (o.isNull("ExitCountry")) continue else o.getString("ExitCountry")
            val city = if (o.isNull("City")) "" else o.getString("City")
            val phys = o.optJSONArray("Servers") ?: continue
            var entry = ""; var key = ""
            for (j in 0 until phys.length()) {
                val p = phys.getJSONObject(j)
                if (p.optInt("Status", 1) != 1 || p.optInt("ServicesDown", 0) != 0 || p.isNull("X25519PublicKey") || p.isNull("EntryIP")) continue
                entry = p.getString("EntryIP"); key = p.getString("X25519PublicKey")
                if (entry.isNotEmpty() && key.isNotEmpty()) break
            }
            if (entry.isEmpty() || key.isEmpty()) continue
            val free = o.optInt("Tier", 2) == 0
            val load = o.optInt("Load", -1)
            val id = "proton:$cc:$city:${if (free) "free" else "plus"}"
            // (Proton writes "UK" for the United Kingdom; the standard code is GB.)
            val country = java.util.Locale("", if (cc == "UK") "GB" else cc).getDisplayCountry(java.util.Locale.ENGLISH).ifEmpty { cc }
            val place = if (free) (if (city.isEmpty()) "Free servers" else "$city (Free)") else city
            val cur = best[id]
            if (cur == null || (load >= 0 && (cur.load < 0 || load < cur.load))) best[id] = Location(id, country, place, cc, entry, key, load)
        }
        return best.values.sortedWith(compareBy({ it.country }, { it.city }, { it.load }))
    }

    /** The saved location, or (when none is chosen yet) the one Quick Connect would pick. */
    fun current(list: List<Location>): Location? = list.firstOrNull { it.id == locationId } ?: best(list)

    /** The country the Fire TV is set to, as a standard code ("GB"); empty when unknown. */
    fun region(): String = normCountry(java.util.Locale.getDefault().country)

    /** Proton writes "UK" for the United Kingdom; the standard code is GB. */
    private fun normCountry(c: String) = c.uppercase().let { if (it == "UK") "GB" else it }

    /**
     * Quick Connect's pick: the least busy server in your country (or anywhere, if there's none there). Proton's free
     * servers are left out unless you've chosen a free one before, since a free account can't use the others.
     */
    fun best(list: List<Location>): Location? {
        val freeOnly = provider == PROTON && locationId.endsWith(":free")
        val pool = if (provider == PROTON) list.filter { it.id.endsWith(":free") == freeOnly }.ifEmpty { list } else list
        val here = region()
        val near = pool.filter { here.isNotEmpty() && normCountry(it.countryCode) == here }.ifEmpty { pool }
        return near.filter { it.load >= 0 }.minByOrNull { it.load } ?: near.firstOrNull()
    }

    /** Connects to [best]; returns an error message, or null when connected. */
    suspend fun quickConnect(): String? {
        val target = best(locations()) ?: return "No VPN locations".also { set(Status.ERROR, it) }
        Ui.test("vpn-quick ${target.label}")
        return connect(target)
    }

    // ------------------------------------------------------------------ connecting

    private fun configFor(loc: Location): Config {
        val isProton = provider == PROTON
        val text = loc.conf ?: """
            [Interface]
            PrivateKey = ${if (isProton) protonKey else privateKey}
            Address = ${if (isProton) protonAddress else address}
            DNS = ${if (isProton) "10.2.0.1" else "162.252.172.57, 149.154.159.92"}

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
                // Close what's playing first, so the provider sees it end instead of counting it for minutes.
                Streams.closeAll("vpn connecting")
                try { backend.setState(tunnel, Tunnel.State.UP, configFor(target)) } finally { Streams.reopenAll() }
                locationId = target.id
                connectedTo = target
                wanted = true
                set(Status.ON, target.label)
                Streams.reopenAll() // (held while switching: the kill switch keeps them until it's on)
                watch()
                null
            } catch (e: Throwable) {
                Log.w("Dingus", "vpn", e)
                val msg = when {
                    e.message?.contains("key", true) == true -> "Your private key doesn't look right"
                    e is java.net.UnknownHostException || e is java.net.SocketTimeoutException || e is java.net.ConnectException -> "Couldn't reach the VPN. Check the internet connection."
                    else -> "The VPN couldn't connect. Try another location, or Quick Connect."
                }
                set(Status.ERROR, msg)
                msg
            }
        }
    }

    suspend fun disconnect() = lock.withLock {
        withContext(Dispatchers.IO) {
            Streams.closeAll("vpn off")
            try { backend.setState(tunnel, Tunnel.State.DOWN, null) } catch (e: Throwable) { Log.w("Dingus", "vpn down", e) }
            Streams.reopenAll()
            connectedTo = null
            wanted = false
            set(Status.OFF, "")
        }
    }

    // ------------------------------------------------------------------ drops: kill switch and reconnecting

    private val scope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + Dispatchers.IO)
    private var watcher: kotlinx.coroutines.Job? = null
    @Volatile private var fixing = false

    /**
     * While connected: every 30 seconds, is the server still answering? WireGuard renews its handshake every two
     * minutes while there's traffic (and Dingus sends a keep-alive every 25 seconds), so none for over three minutes
     * means the VPN has dropped.
     */
    private fun watch() {
        watcher?.cancel()
        watcher = scope.launch {
            while (true) {
                kotlinx.coroutines.delay(30_000)
                if (status != Status.ON || fixing) continue
                val last = try { backend.getStatistics(tunnel).peers().firstOrNull()?.let { backend.getStatistics(tunnel).peer(it)?.latestHandshakeEpochMillis } } catch (_: Throwable) { null } ?: continue
                if (last > 0 && System.currentTimeMillis() - last > 180_000) { set(Status.ERROR, "VPN dropped"); dropped() }
            }
        }
    }

    /** The VPN dropped: hold streams (kill switch), then reconnect – the same place three times, then the least busy nearby. */
    private fun dropped() {
        if (!wanted || fixing) return
        fixing = true
        Ui.test("vpn-dropped")
        if (Store.vpnKillSwitch) { Streams.closeAll("vpn dropped"); Notice.working("VPN dropped  ·  reconnecting…") }
        scope.launch {
            try {
                if (!Store.vpnReconnect) { if (Store.vpnKillSwitch) Notice.done("VPN dropped  ·  turn it on again in Settings → VPN", 8_000); return@launch }
                val here = connectedTo
                val all = try { locations() } catch (_: Exception) { emptyList() }
                val nearby = all.filter { it.countryCode == here?.countryCode && it.id != here?.id }.sortedBy { if (it.load < 0) 100 else it.load }
                val tries = listOfNotNull(here, here, here) + nearby.take(2)
                for (loc in tries) {
                    if (!wanted) return@launch // (turned off meanwhile)
                    if (connect(loc) == null) { Ui.test("vpn-reconnected ${loc.label}"); Streams.reopenAll(); Notice.done("VPN back on  ·  ${loc.label}", 3_000); return@launch }
                    kotlinx.coroutines.delay(3_000)
                }
                Notice.done(if (Store.vpnKillSwitch) "The VPN couldn't reconnect  ·  turn it off in Settings → VPN to watch without it" else "The VPN couldn't reconnect", 10_000)
            } finally { fixing = false }
        }
    }

    // ------------------------------------------------------------------ ping and speed

    /** How quickly each place answers (ms), measured with ping; null where it doesn't. Several at once. */
    suspend fun pings(list: List<Location>): Map<String, Int> = withContext(Dispatchers.IO) {
        val out = java.util.concurrent.ConcurrentHashMap<String, Int>()
        val pool = java.util.concurrent.Executors.newFixedThreadPool(8)
        for (l in list.filter { it.host.isNotEmpty() }) pool.execute { ping(l.host)?.let { out[l.id] = it } }
        pool.shutdown(); pool.awaitTermination(6, java.util.concurrent.TimeUnit.SECONDS); pool.shutdownNow()
        Ui.test("vpn-pings ${out.size}/${list.size}")
        out
    }

    private val pingRx = Regex("""time[=<]([\d.]+)""")
    private fun ping(host: String): Int? = try {
        val p = ProcessBuilder("ping", "-c", "1", "-W", "2", host).redirectErrorStream(true).start()
        val text = p.inputStream.bufferedReader().readText(); p.waitFor()
        pingRx.find(text)?.groupValues?.get(1)?.toFloatOrNull()?.toInt()
    } catch (_: Exception) { null }

    /** Downloads a test file for up to ~10 seconds (through the VPN when it's on); Mbps. */
    suspend fun speedTest(progress: (Float) -> Unit): Float = withContext(Dispatchers.IO) {
        val c = java.net.URL("https://speed.cloudflare.com/__down?bytes=200000000").openConnection() as java.net.HttpURLConnection
        c.connectTimeout = 8_000; c.readTimeout = 8_000
        var bytes = 0L
        val t0 = System.nanoTime()
        try {
            c.inputStream.use { s ->
                val buf = ByteArray(64 * 1024)
                while (true) {
                    val n = s.read(buf); if (n < 0) break
                    bytes += n
                    val secs = (System.nanoTime() - t0) / 1e9f
                    if (secs > 0.5f) progress(bytes * 8 / secs / 1e6f)
                    if (secs >= 10f) break
                }
            }
        } finally { c.disconnect() }
        val secs = ((System.nanoTime() - t0) / 1e9f).coerceAtLeast(0.1f)
        (bytes * 8 / secs / 1e6f).also { Ui.test("vpn-speed $it") }
    }

    /** At start: if "connect when Dingus starts" is on, connect first (so the playlist loads through the VPN). */
    suspend fun startup() {
        if (!auto || status == Status.ON || !ready) return
        // (The connecting carries on by itself: a timeout can't cut short a slow download or the tunnel starting, so
        // only the waiting stops after 15 seconds, and the playlist loads.)
        val job = kotlinx.coroutines.CoroutineScope(Dispatchers.IO).async { connect() }
        withTimeoutOrNull(15_000) { job.await() }
    }
}
