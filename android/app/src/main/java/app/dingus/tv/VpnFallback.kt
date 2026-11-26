package app.dingus.tv

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Settings → VPN → Find a location that works: when none of the playlist's servers (main or backups) answer through
 * the VPN location in use, try other locations one at a time until the playlist answers – ones that have worked for
 * this playlist before first, then the same country, then nearby, then the rest; six tries at most. Only when nothing
 * answers: while a backup works, TV plays and the location you chose stays.
 */
object VpnFallback {
    @Volatile var running = false; private set
    private const val TRIES = 6
    private const val PATIENCE_MS = 8_000

    var enabled: Boolean
        get() = Store.prefsString("vpnFindWorking", "1") == "1"
        set(v) = Store.putString("vpnFindWorking", if (v) "1" else "0")

    /** Locations the playlist has answered through (newest first). */
    fun goodFor(p: Playlist): List<String> = good(p)
    private fun good(p: Playlist): List<String> = Store.prefsString("vpnGood_${p.id}", "").split('\n').filter { it.isNotBlank() }
    fun remember(p: Playlist, loc: Vpn.Location?) {
        loc ?: return
        val l = (listOf(loc.id) + good(p).filter { it != loc.id }).take(12)
        Store.putString("vpnGood_${p.id}", l.joinToString("\n"))
    }

    /** Any of the playlist's addresses answering (through the VPN as it is now)? */
    private fun anyAnswers(p: Playlist) = Failover.addresses(p).any { it.isNotBlank() && Failover.answers(p, it, PATIENCE_MS) }

    /** Called when nothing answered: off it goes, in the background (once at a time). */
    fun start(p: Playlist) {
        if (running || !enabled || Vpn.status != Vpn.Status.ON || Vpn.searching) return
        running = true
        Background.scope.launch(Dispatchers.IO) {
            val from = Vpn.connectedTo
            try {
                if (anyAnswers(p)) { remember(p, from); return@launch } // (it came back by itself)
                val all = try { Vpn.locations() } catch (e: Exception) { emptyList() }
                val order = order(all, from, good(p)).take(TRIES)
                Ui.test("vpn-fallback ${order.size}")
                for (loc in order) {
                    if (Store.playlist?.id != p.id || !Vpn.wanted) return@launch // (you changed something meanwhile)
                    Notice.working("Playlist not answering through ${from?.label ?: "this location"}  ·  trying ${loc.label}…")
                    if (Vpn.connect(loc) != null) continue
                    delay(1_500)
                    if (anyAnswers(p)) {
                        remember(p, loc)
                        Ui.test("vpn-fallback found ${loc.label}")
                        Notice.done("⛨  Now through ${loc.label}  ·  playlist online", 6_000)
                        Failover.networkChanged()
                        return@launch
                    }
                }
                // Nothing yet: back where it was (the VPN stays on), and it keeps looking by itself.
                if (from != null && Vpn.wanted) Vpn.connect(from, needPlaylist = false)
                Notice.done("Your playlist isn't answering through this VPN location  ·  still looking for one that works", 7_000)
                if (from != null) Vpn.keepSearching(from, order.map { it.id }.toSet())
            } catch (e: Exception) {
                android.util.Log.w("Dingus", "vpn fallback", e)
            } finally {
                running = false
                // (Left without the VPN while it's meant to be on: it keeps trying by itself.)
                if (Vpn.wanted && Vpn.status != Vpn.Status.ON) Vpn.keepTrying()
            }
        }
    }

    /** Ones that worked before, then the same country, then the same part of the world, then the rest (least busy first). */
    fun order(all: List<Vpn.Location>, from: Vpn.Location?, good: List<String>): List<Vpn.Location> {
        val rest = all.filter { it.id != from?.id }
        val worked = good.mapNotNull { id -> rest.firstOrNull { it.id == id } }
        val cc = from?.countryCode?.uppercase() ?: ""
        val area = areaOf(cc)
        fun busy(l: Vpn.Location) = if (l.load >= 0) l.load else 50
        val others = rest.filter { it !in worked }
        val same = others.filter { cc.isNotEmpty() && it.countryCode.equals(cc, true) }.sortedBy(::busy)
        val near = others.filter { it !in same && area != null && areaOf(it.countryCode.uppercase()) == area }.sortedBy(::busy)
        val far = others.filter { it !in same && it !in near }.sortedBy(::busy)
        // One per country among the far ones first, so six tries cover more of the world.
        val spread = far.distinctBy { it.countryCode.uppercase() } + far
        return (worked + same + near + spread).distinctBy { it.id }
    }

    private fun areaOf(cc: String): Int? = when (cc) {
        "GB", "UK", "IE", "FR", "DE", "NL", "BE", "LU", "CH", "AT", "ES", "PT", "IT", "DK", "NO", "SE", "FI", "IS", "PL", "CZ", "SK",
        "HU", "RO", "BG", "GR", "HR", "SI", "RS", "BA", "AL", "MK", "ME", "EE", "LV", "LT", "UA", "MD", "CY", "MT" -> 1
        "US", "CA", "MX" -> 2
        "BR", "AR", "CL", "CO", "PE", "VE", "UY", "PY", "BO", "EC", "CR", "PA" -> 3
        "CI", "NG", "GH", "ZA", "KE", "EG", "MA", "TN", "DZ", "SN", "ET", "TZ", "UG", "AO", "CM", "MZ" -> 4
        "AE", "SA", "QA", "IL", "TR", "JO", "KW", "BH", "OM", "IR", "IQ" -> 5
        "IN", "PK", "BD", "LK", "NP", "SG", "MY", "TH", "VN", "ID", "PH", "HK", "TW", "JP", "KR", "CN", "KH", "MM" -> 6
        "AU", "NZ" -> 7
        else -> null
    }
}
