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
    const val MULLVAD = "mullvad"
    const val IVPN = "ivpn"
    const val NORD = "nord"
    /** The providers with a built-in list of places (the rest use config files). */
    val listed = listOf(SURFSHARK, PROTON, MULLVAD, IVPN, NORD)
    fun providerName(p: String) = when (p) { SURFSHARK -> "Surfshark"; PROTON -> "Proton VPN"; MULLVAD -> "Mullvad"; IVPN -> "IVPN"; NORD -> "NordVPN"; else -> "WireGuard config files" }

    enum class Status { OFF, CONNECTING, ON, ERROR }

    /** A place to connect to. For Surfshark: host + public key; for config files: the file itself. */
    class Location(
        val id: String, val country: String, val city: String, val countryCode: String,
        val host: String = "", val publicKey: String = "", val load: Int = -1, val conf: String? = null, val port: Int = 51820,
    ) {
        /** "US – Boston" (the country's short code when there's a city too), or just the country. */
        val label: String get() = if (city.isNotEmpty() && city != country) "${countryCode.uppercase().ifEmpty { country }} – $city" else country
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

    fun init(ctx: Context) { app = ctx.applicationContext; listenForNetwork() }

    fun onChanged(l: () -> Unit) { synchronized(listeners) { listeners.add(l) } }
    fun removeListener(l: () -> Unit) { synchronized(listeners) { listeners.remove(l) } }
    /** Where it was last connected (for "switched to …"). */
    private var lastOnLabel: String? = null

    @Volatile private var lastCheckedLabel: String? = null

    private fun set(s: Status, msg: String) {
        val was = status
        status = s; message = msg
        // A small note on whatever screen you're on: connecting, connected (or switched), off.
        if (s != was || s == Status.ON) main.post {
            when (s) {
                Status.CONNECTING -> Notice.working("⛨  VPN connecting${if (msg.isNotBlank()) " to $msg" else ""}…")
                Status.ON -> {
                    Notice.endWorking("⛨")
                    val here = connectedTo?.label ?: ""
                    if (here != lastOnLabel || was != Status.ON)
                        Notice.aside(if (lastOnLabel != null && was != Status.OFF && here != lastOnLabel) "⛨  VPN switched  ·  $here" else "⛨  VPN connected  ·  $here")
                    lastOnLabel = here
                }
                Status.OFF -> { Notice.endWorking("⛨"); if (was == Status.ON || was == Status.CONNECTING) Notice.aside("⛨  VPN off"); lastOnLabel = null }
                Status.ERROR -> Notice.clear() // (the screen that asked says why)
            }
        }
        Ui.test("vpn ${s.name.lowercase()} $msg")
        // A new way out to the internet (on, off or another location): servers that answered before may not now, and
        // the other way round – so the playlist's status and the server in use are checked again straight away.
        if ((s == Status.ON || s == Status.OFF) && (s != was || connectedTo?.label != lastCheckedLabel)) {
            lastCheckedLabel = if (s == Status.ON) connectedTo?.label else null
            Failover.networkChanged()
        }
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
    /** Each provider's own WireGuard key and tunnel address (Surfshark's and Proton's have their own names, above). */
    fun keyFor(p: String = provider): String = when (p) { SURFSHARK -> privateKey; PROTON -> protonKey; else -> Store.prefsString("vpnKey_$p", "") }
    fun setKey(p: String, v: String) = when (p) { SURFSHARK -> privateKey = v; PROTON -> protonKey = v; else -> Store.putString("vpnKey_$p", v.trim()) }
    fun addressFor(p: String = provider): String = when (p) {
        SURFSHARK -> address; PROTON -> protonAddress
        NORD -> Store.prefsString("vpnAddress_$p", "").ifEmpty { "10.5.0.2/32" } // (the same for every NordLynx account)
        else -> Store.prefsString("vpnAddress_$p", "")
    }
    fun setAddress(p: String, v: String) = when (p) { SURFSHARK -> address = v; PROTON -> protonAddress = v; else -> Store.putString("vpnAddress_$p", v.trim()) }

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
        MULLVAD, IVPN -> keyFor().isNotEmpty() && addressFor().isNotEmpty()
        NORD -> keyFor().isNotEmpty()
        else -> configs.isNotEmpty()
    }

    // ------------------------------------------------------------------ locations

    @Volatile private var surfshark: List<Location>? = null
    @Volatile private var proton: List<Location>? = null
    @Volatile private var others = HashMap<String, List<Location>>()

    /** The locations for the chosen provider (the server lists are fetched once, then kept for a day). */
    suspend fun locations(): List<Location> = withContext(Dispatchers.IO) {
        when (provider) {
            SURFSHARK -> surfsharkList()
            MULLVAD -> others[MULLVAD] ?: mullvadList().also { others[MULLVAD] = it }
            IVPN -> others[IVPN] ?: ivpnList().also { others[IVPN] = it }
            NORD -> others[NORD] ?: nordList().also { others[NORD] = it }
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

    /** Mullvad's public list: one place per city (the first server that's up there). */
    private fun mullvadList(): List<Location> {
        val a = JSONArray(cached("mullvad.json", "https://api.mullvad.net/www/relays/wireguard/"))
        val best = LinkedHashMap<String, Location>()
        for (i in 0 until a.length()) {
            val o = a.getJSONObject(i)
            if (!o.optBoolean("active", true)) continue
            val key = o.optString("pubkey"); val ip = o.optString("ipv4_addr_in")
            if (key.isEmpty() || ip.isEmpty()) continue
            val cc = o.optString("country_code").uppercase(); val city = o.optString("city_name")
            best.putIfAbsent("$cc|$city", Location(o.optString("hostname"), o.optString("country_name"), city, cc, ip, key))
        }
        if (best.isEmpty()) throw IOException("No Mullvad servers in the list")
        return best.values.sortedWith(compareBy({ it.country }, { it.city }))
    }

    /** IVPN's public list: one place per city (its least busy server); IVPN's WireGuard answers on port 2049. */
    private fun ivpnList(): List<Location> {
        val a = JSONObject(cached("ivpn.json", "https://api.ivpn.net/v5/servers.json")).getJSONArray("wireguard")
        val out = ArrayList<Location>()
        for (i in 0 until a.length()) {
            val o = a.getJSONObject(i)
            val hosts = o.optJSONArray("hosts") ?: continue
            val h = (0 until hosts.length()).map { hosts.getJSONObject(it) }.filter { it.optString("public_key").isNotEmpty() }
                .minByOrNull { it.optDouble("load", 50.0) } ?: continue
            out.add(Location(o.optString("gateway"), o.optString("country"), o.optString("city"), o.optString("country_code").uppercase(),
                h.optString("host"), h.optString("public_key"), h.optDouble("load", -1.0).toInt(), port = 2049))
        }
        if (out.isEmpty()) throw IOException("No IVPN servers in the list")
        return out.sortedWith(compareBy({ it.country }, { it.city }))
    }

    /** NordVPN's public list (big: read as it arrives): one place per city, its least busy NordLynx server. */
    private fun nordList(): List<Location> {
        val file = File(app.filesDir, "nord.json")
        if (!file.exists() || System.currentTimeMillis() - file.lastModified() > 86_400_000L) try {
            Net.open("https://api.nordvpn.com/v1/servers?limit=16384&filters%5Bservers_technologies%5D%5Bidentifier%5D=wireguard_udp", 60_000)
                .use { s -> file.outputStream().use { s.copyTo(it) } }
        } catch (e: Exception) { if (!file.exists()) throw e }
        val best = HashMap<String, Location>()
        android.util.JsonReader(file.bufferedReader()).use { r ->
            r.beginArray()
            while (r.hasNext()) {
                var host = ""; var ip = ""; var load = -1; var key = ""; var country = ""; var cc = ""; var city = ""
                r.beginObject()
                while (r.hasNext()) when (r.nextName()) {
                    "hostname" -> host = r.nextString()
                    "station" -> ip = r.nextString()
                    "load" -> load = r.nextInt()
                    "locations" -> { r.beginArray(); while (r.hasNext()) { r.beginObject(); while (r.hasNext()) if (r.nextName() == "country") {
                        r.beginObject(); while (r.hasNext()) when (r.nextName()) {
                            "name" -> country = r.nextString(); "code" -> cc = r.nextString()
                            "city" -> { r.beginObject(); while (r.hasNext()) if (r.nextName() == "name") city = r.nextString() else r.skipValue(); r.endObject() }
                            else -> r.skipValue() }; r.endObject() } else r.skipValue(); r.endObject() }; r.endArray() }
                    "technologies" -> { r.beginArray(); while (r.hasNext()) { var id = ""; var pk = ""; r.beginObject(); while (r.hasNext()) when (r.nextName()) {
                        "identifier" -> id = r.nextString()
                        "metadata" -> { r.beginArray(); while (r.hasNext()) { var n = ""; var v = ""; r.beginObject(); while (r.hasNext()) when (r.nextName()) { "name" -> n = r.nextString(); "value" -> v = if (r.peek() == android.util.JsonToken.STRING) r.nextString() else { r.skipValue(); "" }; else -> r.skipValue() }; r.endObject(); if (n == "public_key") pk = v }; r.endArray() }
                        else -> r.skipValue() }; r.endObject(); if (id == "wireguard_udp" && pk.isNotEmpty()) key = pk }; r.endArray() }
                    else -> r.skipValue()
                }
                r.endObject()
                if (key.isEmpty() || ip.isEmpty()) continue
                val k = "$cc|$city"
                val old = best[k]
                if (old == null || load in 0 until old.load) best[k] = Location(host, country, city, cc.uppercase(), ip, key, load)
            }
            r.endArray()
        }
        if (best.isEmpty()) throw IOException("No NordVPN servers in the list")
        return best.values.sortedWith(compareBy({ it.country }, { it.city }))
    }

    /** NordVPN: your NordLynx private key, from an access token (Nord account → Manual setup → access token). */
    suspend fun nordKeyFromToken(token: String): String = withContext(Dispatchers.IO) {
        val c = java.net.URL("https://api.nordvpn.com/v1/users/services/credentials").openConnection() as java.net.HttpURLConnection
        c.setRequestProperty("Authorization", "Basic " + android.util.Base64.encodeToString("token:${token.trim()}".toByteArray(), android.util.Base64.NO_WRAP))
        c.connectTimeout = 15_000; c.readTimeout = 15_000
        try {
            if (c.responseCode == 401) throw IOException("NordVPN didn't accept that access token")
            JSONObject(c.inputStream.bufferedReader().readText()).optString("nordlynx_private_key").ifEmpty { throw IOException("NordVPN sent no key") }
        } finally { c.disconnect() }
    }

    /**
     * A pasted config for a provider with a built-in list (Mullvad, IVPN, NordVPN…): its private key and address are
     * all that's needed – the places come from the list. True if they were taken from [text].
     */
    fun takeKeyFrom(text: String): Boolean {
        if (provider !in listOf(MULLVAD, IVPN, NORD)) return false
        val key = Regex("""(?im)^\s*PrivateKey\s*=\s*(\S+)""").find(text)?.groupValues?.get(1) ?: return false
        val addr = Regex("""(?im)^\s*Address\s*=\s*([^,\s]+)""").find(text)?.groupValues?.get(1)
        setKey(provider, key); addr?.let { setAddress(provider, it) }
        return true
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

    /** Settings → VPN → DNS: "" for the VPN company's own, else the addresses to use instead ("1.1.1.1, 1.0.0.1"). */
    var dns: String
        get() = Store.prefsString("vpnDns", "")
        set(v) = Store.putString("vpnDns", v.trim())

    private fun configFor(loc: Location): Config {
        val own = when (provider) { PROTON -> "10.2.0.1"; MULLVAD -> "10.64.0.1"; IVPN -> "172.16.0.1"; NORD -> "103.86.96.100"; else -> "162.252.172.57, 149.154.159.92" }
        val dns = dns.ifEmpty { own }
        val text = loc.conf ?: """
            [Interface]
            PrivateKey = ${keyFor()}
            Address = ${addressFor()}
            DNS = $dns

            [Peer]
            PublicKey = ${loc.publicKey}
            Endpoint = ${loc.host}:${loc.port}
            AllowedIPs = 0.0.0.0/0
            PersistentKeepalive = 25
        """.trimIndent()
        return Config.parse(withDns(onlyDingus(text)).byteInputStream())
    }

    /** A chosen DNS (Settings → VPN → DNS) replaces whatever a config file had. */
    private fun withDns(text: String): String {
        val chosen = dns
        if (chosen.isEmpty()) return text
        val lines = text.lines().filterNot { it.trim().startsWith("DNS", true) && it.contains('=') }
        val i = lines.indexOfFirst { it.trim().equals("[Interface]", true) }
        if (i < 0) return text
        return (lines.subList(0, i + 1) + "DNS = $chosen" + lines.subList(i + 1, lines.size)).joinToString("\n")
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

    /** The ending of the message for a location your playlist can't be reached through (see [connect]). */
    const val NO_PLAYLIST = "can't reach your playlist"

    /**
     * Whether any of the playlist's addresses (main or backups) answers through the tunnel now – all asked at once,
     * eight seconds at most. True with no playlist yet.
     */
    private fun playlistAnswers(): Boolean {
        val p = Store.playlist ?: return true
        val all = Failover.addresses(p).filter { it.isNotBlank() }
        if (all.isEmpty()) return true
        val done = java.util.concurrent.CountDownLatch(1)
        val ok = java.util.concurrent.atomic.AtomicBoolean(false)
        val left = java.util.concurrent.atomic.AtomicInteger(all.size)
        for (a in all) Thread {
            if (Failover.answers(p, a, 8_000)) { ok.set(true); done.countDown() }
            if (left.decrementAndGet() == 0) done.countDown()
        }.start()
        done.await(9, java.util.concurrent.TimeUnit.SECONDS)
        return ok.get()
    }

    /**
     * Connects to [loc] (or the saved location). Returns an error message, or null when connected. With [needPlaylist]
     * (the usual), a location your playlist can't be reached through doesn't count as connected: it's let go and the
     * error ends with [NO_PLAYLIST], so another is tried.
     */
    suspend fun connect(loc: Location? = null, auto: Boolean = false, needPlaylist: Boolean = true, reopen: Boolean = true): String? = lock.withLock {
        withContext(Dispatchers.IO) {
            try {
                // (A try of its own that waited its turn while you turned the VPN off: not wanted any more.)
                if (auto && !wanted) return@withContext "VPN off"
                if (!ready) return@withContext "Set up your VPN first".also { set(Status.ERROR, it) }
                if (permissionIntent(app) != null) return@withContext "Dingus needs permission to use a VPN – open Settings → VPN".also { set(Status.ERROR, it) }
                val target = loc ?: current(locations()) ?: return@withContext "No VPN locations".also { set(Status.ERROR, it) }
                set(Status.CONNECTING, target.label)
                // Close what's playing first, so the provider sees it end instead of counting it for minutes.
                Streams.closeAll("vpn connecting")
                // Already connected somewhere (switching location, or a drop being put right): close that tunnel and
                // wait for the VPN service to finish closing before opening the new one. Opened straight away, the old
                // service's closing came a moment later and shut the new tunnel down with it – so changing location
                // didn't stick (it hung, said the place didn't answer, or ended up somewhere else).
                if (try { backend.getState(tunnel) == Tunnel.State.UP } catch (_: Throwable) { false }) {
                    try { backend.setState(tunnel, Tunnel.State.DOWN, null) } catch (e: Throwable) { Log.w("Dingus", "vpn down for switch", e) }
                    waitServiceClosed(4_000)
                }
                val config = configFor(target)
                lastRx = -1L
                tunnelDns = try { config.`interface`.dnsServers.firstOrNull() } catch (_: Throwable) { null }
                // (What was playing starts again only once this location is known to work – see the end. Started
                // straight away, it went out through a location not yet checked, and while a channel was being tried
                // through each place it took the one connection a plan allows, so the try was refused.)
                backend.setState(tunnel, Tunnel.State.UP, config)
                // Only "connected" once the VPN server has actually answered (its first handshake). The tunnel being
                // set up proves nothing: it showed connected while nothing got through – no IP address, and the
                // playlist offline.
                // …and only once something has actually come back through it: a DNS question answered via the tunnel.
                // A server can say hello and still pass nothing on.
                if (!answered(10_000) || !works()) {
                    try { backend.setState(tunnel, Tunnel.State.DOWN, null) } catch (_: Throwable) {}
                    Ui.test("vpn-no-answer ${target.label}")
                    return@withContext "${target.label} didn't answer".also { set(Status.ERROR, it); afterFailure() }
                }
                // …and only if your playlist can actually be reached through it: a VPN server that's fine but that
                // your provider won't answer through is no use – it looked like the servers were offline.
                if (needPlaylist && !playlistAnswers()) {
                    try { backend.setState(tunnel, Tunnel.State.DOWN, null) } catch (_: Throwable) {}
                    Ui.test("vpn-no-playlist ${target.label}")
                    return@withContext "${target.label} $NO_PLAYLIST".also { set(Status.ERROR, it); afterFailure() }
                }
                if (needPlaylist) Store.playlist?.let { VpnFallback.remember(it, target) } // (tried first next time)
                locationId = target.id
                connectedTo = target
                wanted = true
                set(Status.ON, target.label)
                if (reopen) Streams.reopenAll() // (held while switching: the kill switch keeps them until it's on)
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
                afterFailure()
                msg
            }
        }
    }

    /**
     * A try that didn't work. Part of a search ([keepTrying], [keepSearching], [connectOrNearby], Find a location
     * that works): the search carries on. On its own, with the VPN meant to be on: [keepTrying] takes over. Either way,
     * with the kill switch off, what was playing starts again meanwhile (without the VPN); with it on, it waits.
     */
    private fun afterFailure() {
        val searchingNow = fixing || quiet || VpnFallback.running || searcher?.isActive == true
        if (wanted && !searchingNow) keepTrying()
        if (!searchingNow) Streams.reopenAll()
    }

    /** Waits (up to [ms]) until the VPN service has stopped, after the tunnel was taken down. */
    @Suppress("DEPRECATION")
    private fun waitServiceClosed(ms: Long) {
        val am = app.getSystemService(Context.ACTIVITY_SERVICE) as android.app.ActivityManager
        val until = System.currentTimeMillis() + ms
        while (System.currentTimeMillis() < until) {
            val running = try {
                am.getRunningServices(50).any { it.service.packageName == app.packageName && it.service.className.contains("VpnService") }
            } catch (_: Throwable) { false }
            if (!running) break
            Thread.sleep(100)
        }
        Thread.sleep(300) // (and a moment for it to finish tidying up)
        Ui.test("vpn-service-closed")
    }

    /** When the VPN server last answered (0: not yet). */
    private fun handshakeAt(): Long = try {
        val st = backend.getStatistics(tunnel)
        st.peers().firstOrNull()?.let { st.peer(it)?.latestHandshakeEpochMillis } ?: 0L
    } catch (_: Throwable) { 0L }

    /** The DNS server the tunnel in use was given (asked by [works]). */
    @Volatile private var tunnelDns: java.net.InetAddress? = null

    /** A real DNS question ("who runs the internet's root?"): small, and every DNS server answers it. */
    private fun dnsQuestion(): ByteArray = byteArrayOf(0x44, 0x47, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0x00, 0x00, 0x02, 0x00, 0x01)

    /** Asks [to] that question through the tunnel (Dingus's own traffic goes through it); true when it answers. */
    private fun ask(to: java.net.InetAddress, timeoutMs: Int): Boolean = try {
        java.net.DatagramSocket().use { s ->
            s.soTimeout = timeoutMs
            val q = dnsQuestion()
            s.send(java.net.DatagramPacket(q, q.size, to, 53))
            val buf = ByteArray(512)
            s.receive(java.net.DatagramPacket(buf, buf.size))
            true
        }
    } catch (_: Throwable) { false }

    @Volatile private var lastRx = -1L
    private fun received(): Long = try { backend.getStatistics(tunnel).totalRx() } catch (_: Throwable) { -1L }

    /**
     * Whether anything actually gets through the tunnel right now (not just "connected"): something received since
     * the last check (a stream playing, say) settles it; otherwise a DNS question, two tries, a few seconds.
     */
    private fun works(): Boolean {
        val rx = received()
        val before = lastRx; lastRx = rx
        if (rx > 0 && before >= 0 && rx > before) return true
        val one = tunnelDns
        val cf = try { java.net.InetAddress.getByAddress(byteArrayOf(1, 1, 1, 1)) } catch (_: Throwable) { null }
        repeat(2) {
            if (one != null && ask(one, 3_000)) return true
            if (cf != null && cf != one && ask(cf, 3_000)) return true
        }
        return false
    }

    /** Waits up to [ms] for the server's first answer, nudging it with a small DNS question through the tunnel. */
    private fun answered(ms: Long): Boolean {
        val until = System.currentTimeMillis() + ms
        var nudged = 0L
        while (System.currentTimeMillis() < until) {
            if (handshakeAt() > 0) return true
            if (System.currentTimeMillis() - nudged > 2_000) {
                nudged = System.currentTimeMillis()
                // (Any traffic makes WireGuard say hello to the server.)
                val to = tunnelDns ?: try { java.net.InetAddress.getByAddress(byteArrayOf(1, 1, 1, 1)) } catch (_: Throwable) { null }
                if (to != null) try { java.net.DatagramSocket().use { val q = dnsQuestion(); it.send(java.net.DatagramPacket(q, q.size, to, 53)) } } catch (_: Throwable) {}
            }
            Thread.sleep(200)
        }
        return handshakeAt() > 0
    }

    /**
     * [connect], and if that location doesn't answer, the least busy others in the same country (two at most), by
     * itself – with a note saying where it ended up.
     */
    /** Set while [connectOrNearby] tries the others itself (a failed one isn't handed to [keepTrying] meanwhile). */
    @Volatile private var quiet = false

    suspend fun connectOrNearby(loc: Location? = null): String? {
        quiet = true
        val err = try { nearbyTries(loc) } finally { quiet = false }
        // Still not on, though it's meant to be (switching location, or at start-up): keeps trying by itself.
        if (err != null && wanted) keepTrying()
        return err
    }

    private suspend fun nearbyTries(loc: Location?): String? {
        val err = connect(loc) ?: return null
        if (!err.endsWith("didn't answer") && !err.endsWith(NO_PLAYLIST)) return err
        val from = loc ?: try { current(locations()) } catch (_: Exception) { null } ?: return err
        val others = try { locations() } catch (_: Exception) { emptyList() }
            .filter { it.countryCode == from.countryCode && it.id != from.id }.sortedBy { if (it.load < 0) 100 else it.load }.take(2)
        for (next in others) {
            val why = if (err.endsWith(NO_PLAYLIST)) "your playlist doesn't answer through ${from.label}" else "${from.label} didn't answer"
            Notice.working("⛨  $why  ·  trying ${next.label}…")
            if (connect(next) == null) { Notice.endWorking("⛨"); Notice.done("⛨  Now through ${next.label}  ·  $why", 6_000); return null }
        }
        Notice.endWorking("⛨")
        return err
    }

    suspend fun disconnect() = lock.withLock {
        withContext(Dispatchers.IO) {
            // Turned off on purpose: not "wanted" before the tunnel goes down, so that isn't taken for a drop.
            wanted = false
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
     * While connected: every 10 seconds, does anything actually get through (and is the server still answering)?
     * Nothing getting through (two quick tries inside one check), or no handshake for over three minutes (WireGuard
     * renews it every two while there's traffic, and Dingus sends a keep-alive every 25 seconds), means it isn't
     * connected – it stops saying "connected" there and then, and reconnects. A tunnel can stay "connected" while
     * carrying nothing, which looked like the playlist's servers being offline.
     */
    private fun watch() {
        watcher?.cancel()
        watcher = scope.launch {
            while (true) {
                kotlinx.coroutines.delay(10_000)
                if (status != Status.ON || fixing) continue
                val last = try { backend.getStatistics(tunnel).peers().firstOrNull()?.let { backend.getStatistics(tunnel).peer(it)?.latestHandshakeEpochMillis } } catch (_: Throwable) { null } ?: continue
                val stale = last <= 0 || System.currentTimeMillis() - last > 180_000
                if (stale || !works()) {
                    if (status != Status.ON || fixing) continue // (changed meanwhile: switched, or turned off)
                    Ui.test("vpn-not-working"); set(Status.ERROR, "VPN dropped"); dropped()
                }
            }
        }
    }

    /** The VPN dropped: hold streams (kill switch), then put it right – see [keepTrying]. */
    private fun dropped() {
        if (!wanted || fixing) return
        Ui.test("vpn-dropped")
        if (Store.vpnKillSwitch) { Streams.closeAll("vpn dropped"); Notice.working("VPN dropped  ·  reconnecting…") }
        if (!Store.vpnReconnect) { if (Store.vpnKillSwitch) Notice.done("VPN dropped  ·  turn it on again in Settings → VPN", 8_000); return }
        keepTrying()
    }

    private var keeper: kotlinx.coroutines.Job? = null
    /** Set by a change of network: the next try happens now rather than after the wait. */
    @Volatile private var tryNow = false

    /**
     * The VPN is meant to be on but isn't (it dropped, didn't come on at start-up, or a new location didn't answer):
     * it keeps trying by itself until it's back – where it was, then the least busy nearby, then the best overall –
     * quickly at first, then every two minutes, and straight away when the network comes back. It never gives up
     * while you want it on; you're told once that it's still trying.
     */
    fun keepTrying() {
        if (!wanted || !Store.vpnReconnect) return
        if (keeper?.isActive == true) { tryNow = true; return }
        if (searcher?.isActive == true && fixing) return // (in the middle of trying places: it parks back on one itself)
        keeper = scope.launch {
            fixing = true
            try {
                val waits = longArrayOf(0, 3_000, 10_000, 30_000, 60_000)
                var round = 0
                // Locations tried that connected but your playlist didn't answer through. Each round reaches further
                // (other cities, then other countries): a provider often blocks many of one VPN company's servers at
                // once, so a handful failing nearby doesn't mean they all will.
                val noPlaylist = LinkedHashSet<String>()
                var onlyPlaylistRounds = 0
                while (wanted && status != Status.ON) {
                    val wait = waits.getOrElse(round) { 120_000L }
                    var waited = 0L
                    while (waited < wait && !tryNow && wanted) { kotlinx.coroutines.delay(1_000); waited += 1_000 }
                    tryNow = false
                    if (!wanted || status == Status.ON) break
                    val all = try { locations() } catch (_: Exception) { emptyList() }
                    val here = connectedTo ?: all.firstOrNull { it.id == locationId }
                    val nearby = all.filter { here != null && it.countryCode == here.countryCode && it.id != here.id }.sortedBy { if (it.load < 0) 100 else it.load }
                    val good = Store.playlist?.let { VpnFallback.goodFor(it) }.orEmpty()
                    val tries = if (round == 0 || noPlaylist.isEmpty()) {
                        val worked = good.mapNotNull { id -> all.firstOrNull { it.id == id } }
                        (listOfNotNull(here) + nearby.take(2) + worked.take(2) + listOfNotNull(best(all))).distinctBy { it.id }
                            .let { if (here != null) listOf(here) + it else it }
                    } else {
                        // Further afield: ones not tried yet – worked before, same country, same part of the world,
                        // then one per country elsewhere.
                        VpnFallback.order(all, here, good).filter { it.id !in noPlaylist }.take(5)
                    }
                    var onlyPlaylist = true
                    for (loc in tries) {
                        if (!wanted || status == Status.ON) break
                        val err = connect(loc, auto = true)
                        if (err != null && !err.endsWith(NO_PLAYLIST)) onlyPlaylist = false
                        if (err != null && err.endsWith(NO_PLAYLIST)) noPlaylist.add(loc.id)
                        if (err == null) {
                            Ui.test("vpn-reconnected ${loc.label}"); Streams.reopenAll()
                            Notice.done(if (loc.id == here?.id || here == null) "VPN back on  ·  ${loc.label}" else "VPN back on  ·  now through ${loc.label}", 3_000)
                            break
                        }
                    }
                    // Every one connected but your playlist answered through none of them: most likely your
                    // provider's down for now, not the VPN – so connected anyway (where it was) rather than leaving
                    // the VPN off; its own check moves it once the playlist answers somewhere.
                    if (status != Status.ON && onlyPlaylist && tries.isNotEmpty()) onlyPlaylistRounds++
                    // Only after trying widely (three rounds – around fifteen places across several countries) with
                    // every one connecting but none reaching the playlist: on anyway (where it was) rather than off,
                    // saying why it could be. It carries on looking from there by itself (Find a location that works).
                    if (status != Status.ON && onlyPlaylistRounds >= 3 && wanted) {
                        val back = here ?: tries.first()
                        if (connect(back, auto = true, needPlaylist = false) == null) {
                            Ui.test("vpn-on-without-playlist ${back.label} tried ${noPlaylist.size}")
                            Notice.done("⛨  VPN on  ·  ${back.label}  ·  your playlist isn't answering through it  ·  still looking for a location that works", 8_000)
                            searchAfter = back to noPlaylist.toSet()
                        }
                    }
                    // (Kill switch off: what was playing carries on without the VPN until the next round.)
                    if (status != Status.ON && !Store.vpnKillSwitch) Streams.reopenAll()
                    if (status != Status.ON && round == 1)
                        Notice.done(if (Store.vpnKillSwitch) "The VPN hasn't reconnected yet  ·  still trying (Settings → VPN to watch without it)" else "The VPN hasn't reconnected yet  ·  still trying", 8_000)
                    round++
                }
            } finally {
                fixing = false
                searchAfter?.let { (park, tried) -> searchAfter = null; keepSearching(park, tried) }
            }
        }
    }

    /** Whether [url] (a channel or film) starts coming in through the VPN as it is now: its first bytes, then closed. */
    private fun streamPlays(url: String): Boolean = try {
        Net.open(url, 8_000).use { it.read() >= 0 }
    } catch (_: Throwable) { false }

    /**
     * A channel or film was refused (or couldn't reach the server) through this VPN location: looks for one it plays
     * through, straight away and then without end like [keepSearching], and starts it again once found.
     */
    fun streamBlocked(url: String) {
        if (!wanted || status != Status.ON) return
        if (searcher?.isActive == true) return // (already looking)
        Ui.test("vpn-stream-blocked")
        Notice.done("⛨  That didn't play through ${connectedTo?.label ?: "this VPN location"}  ·  looking for one it plays through…", 6_000)
        keepSearching(connectedTo, setOfNotNull(connectedTo?.id), url)
    }

    /** Handed from [keepTrying] to [keepSearching] once it's on (without the playlist) somewhere. */
    @Volatile private var searchAfter: Pair<Location, Set<String>>? = null
    private var searcher: kotlinx.coroutines.Job? = null
    /** Looking for a location your playlist answers through, in the background (see [keepSearching]). */
    val searching: Boolean get() = searcher?.isActive == true

    /**
     * Connected, but your playlist doesn't answer through this location (your provider blocks it, or blocks that VPN
     * company's servers around there): keeps looking, by itself and without end, until it finds one that works –
     * five places not tried yet every minute (further and further afield, every location in the end, then round
     * again, as blocks come and go), parking back on [park] in between so the VPN stays on. It stops as soon as the
     * playlist answers, wherever it is, or the VPN's turned off. Trying costs nothing to watch: the playlist isn't
     * working through the location it's parked on anyway.
     */
    fun keepSearching(park: Location? = connectedTo, alreadyTried: Set<String> = emptySet(), stream: String? = null) {
        if (park == null || !wanted || searcher?.isActive == true || !VpnFallback.enabled) return // (Settings → VPN → Find a location that works)
        // With a [stream]: it's that (a channel or film that was refused) that has to work, not just the playlist.
        val works: () -> Boolean = if (stream != null) { { playlistAnswers() && streamPlays(stream) } } else { { playlistAnswers() } }
        searcher = scope.launch {
            val tried = LinkedHashSet(alreadyTried)
            var home = park
            var first = stream != null // (a refused stream: the first look straight away)
            Ui.test("vpn-search-start ${tried.size}${if (stream != null) " (stream)" else ""}")
            while (wanted) {
                if (!first) kotlinx.coroutines.delay(60_000)
                first = false
                if (!wanted) break
                if (fixing || keeper?.isActive == true) continue // (something else is putting it right)
                // Looking for a place a refused channel plays through, but something's playing fine now (you picked
                // another channel, say): no need to carry on for that one.
                if (stream != null && Streams.flowing) { Ui.test("vpn-search-stop (playing)"); break }
                // …or nothing's open to play any more (you left the channel or film): stop looking for it.
                if (stream != null && !Streams.any) { Ui.test("vpn-search-stop (closed)"); break }
                if (status == Status.ON && works()) {
                    Ui.test("vpn-search-done (works now)")
                    if (stream == null) Notice.done("⛨  Your playlist's answering again  ·  ${connectedTo?.label ?: ""}", 5_000)
                    Streams.reopenAll(force = true)
                    break
                }
                val all = try { locations() } catch (_: Exception) { emptyList() }
                if (all.isEmpty()) continue
                val good = Store.playlist?.let { VpnFallback.goodFor(it) }.orEmpty()
                var batch = VpnFallback.order(all, home, good).filter { it.id !in tried }.take(5)
                if (batch.isEmpty()) { tried.clear(); batch = VpnFallback.order(all, home, good).take(5) } // (all tried: round again)
                fixing = true
                var found: Location? = null
                try {
                    for (loc in batch) {
                        if (!wanted) break
                        tried.add(loc.id)
                        if (connect(loc, auto = true, reopen = stream == null) == null && (stream == null || streamPlays(stream))) { found = loc; break }
                    }
                    // (Parked back where it was. Looking for a place a refused channel plays through: it isn't
                    // started again there – it'd only be refused again, using up the connection each minute.)
                    if (found == null && wanted) connect(home, auto = true, needPlaylist = false, reopen = stream == null)
                } finally { fixing = false }
                if (found != null) {
                    Ui.test("vpn-search-found ${found.label} after ${tried.size}")
                    Notice.done("⛨  Found one that works  ·  now through ${found.label}", 6_000)
                    Failover.networkChanged()
                    Streams.reopenAll(force = true) // (what was refused starts again)
                    break
                }
                Ui.test("vpn-search-tried ${tried.size}")
            }
        }
    }

    /**
     * Wi-Fi back, a different network, or the Fire TV waking up: the VPN is checked straight away (and put right if
     * it isn't carrying anything) instead of waiting minutes for the next check to notice.
     */
    private fun listenForNetwork() {
        val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as? android.net.ConnectivityManager ?: return
        val req = android.net.NetworkRequest.Builder()
            .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build()
        try {
            cm.registerNetworkCallback(req, object : android.net.ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: android.net.Network) { networkBack() }
            })
        } catch (e: Throwable) { Log.w("Dingus", "vpn network callback", e) }
    }

    @Volatile private var lastNetworkCheck = 0L
    private fun networkBack() {
        if (!wanted) return
        val now = System.currentTimeMillis()
        if (now - lastNetworkCheck < 5_000) return
        lastNetworkCheck = now
        scope.launch {
            kotlinx.coroutines.delay(2_000) // (the new network settling)
            if (!wanted) return@launch
            when {
                status == Status.ON && !fixing -> if (!works()) { Ui.test("vpn-network-check failed"); set(Status.ERROR, "VPN dropped"); dropped() } else Ui.test("vpn-network-check ok")
                status != Status.ON -> keepTrying().also { tryNow = true }
            }
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
    /**
     * Downloads for about 10 seconds and returns Mbps. Cloudflare first, in 25 MB pieces (it turns down very large
     * single requests, and some VPN servers' addresses), then other public test files if that's refused.
     */
    suspend fun speedTest(progress: (Float) -> Unit): Float = withContext(Dispatchers.IO) {
        val sources = listOf(
            "https://speed.cloudflare.com/__down?bytes=25000000",
            "https://fsn1-speed.hetzner.com/100MB.bin",
            "https://proof.ovh.net/files/100Mb.dat",
            "http://speedtest.tele2.net/100MB.zip",
        )
        var bytes = 0L
        var t0 = 0L
        var last: Exception? = null
        for (src in sources) {
            try {
                while (true) {
                    val c = java.net.URL(src).openConnection() as java.net.HttpURLConnection
                    c.connectTimeout = 8_000; c.readTimeout = 8_000
                    c.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android) Dingus")
                    try {
                        if (c.responseCode !in 200..299) throw java.io.IOException("the test server answered ${c.responseCode}")
                        if (t0 == 0L) t0 = System.nanoTime()
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
                    if ((System.nanoTime() - t0) / 1e9f >= 10f) break   // (otherwise that piece finished: another)
                }
                break
            } catch (e: Exception) {
                last = e
                Ui.test("vpn-speed-source failed $src ${e.message}")
                if (bytes > 2_000_000) break    // it was going: use what was measured
            }
        }
        if (bytes == 0L || t0 == 0L) throw last ?: java.io.IOException("no test server answered")
        val secs = ((System.nanoTime() - t0) / 1e9f).coerceAtLeast(0.1f)
        (bytes * 8 / secs / 1e6f).also { Ui.test("vpn-speed $it") }
    }

    /** At start: if "connect when Dingus starts" is on, connect first (so the playlist loads through the VPN). */
    suspend fun startup() {
        if (!auto || status == Status.ON || !ready) return
        if (permissionIntent(app) != null) return // (asked for in Settings → VPN)
        wanted = true // (on from the start: if it can't connect yet, it keeps trying by itself)
        // (The connecting carries on by itself: a timeout can't cut short a slow download or the tunnel starting, so
        // only the waiting stops after 30 seconds, and the playlist loads.)
        val job = kotlinx.coroutines.CoroutineScope(Dispatchers.IO).async { connectOrNearby() }
        withTimeoutOrNull(30_000) { job.await() } // (30 s: the check that your playlist answers through it takes a few more)
    }
}
