package app.dingus.tv

import android.content.Intent
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Check everything (Settings → About): the internet, the playlist login, the server (and backups), connections, each
 * TV guide source, films and shows, the VPN, the speed and Dingus itself – each a tick, a warning or a cross with a
 * plain-English fix, which OK does where it can. A short code at the bottom sums it up for help over the phone.
 */
class CheckActivity : BaseActivity() {
    private enum class State(val mark: String, val colour: Int, val code: Char) {
        RUN("…", 0xFF64748B.toInt(), '.'), OK("✓", 0xFF16A34A.toInt(), 'Y'), WARN("!", 0xFFD97706.toInt(), 'W'), BAD("✕", 0xFFDC2626.toInt(), 'X'), SKIP("–", 0xFF475569.toInt(), '-')
    }

    private class Row(val name: String, val view: View) {
        var state = State.RUN; var line = "Waiting…"; var fix: String? = null; var action: (() -> Unit)? = null
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var list: LinearLayout
    private lateinit var head: TextView
    private val rows = ArrayList<Row>()
    private var running = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val d = resources.displayMetrics.density
        val scroll = ScrollView(this).apply { isFillViewport = true; setBackgroundColor(Themes.color(this@CheckActivity, R.attr.dBg)) }
        val col = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding((64 * d).toInt(), (36 * d).toInt(), (64 * d).toInt(), (36 * d).toInt()) }
        col.addView(TextView(this).apply { text = "Check everything"; textSize = 28f; setTypeface(typeface, android.graphics.Typeface.BOLD) })
        head = TextView(this).apply { textSize = 15f; setTextColor(Themes.color(this@CheckActivity, R.attr.dMuted)); setPadding(0, (4 * d).toInt(), 0, (16 * d).toInt()) }
        col.addView(head)
        list = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        col.addView(list)
        scroll.addView(col)
        setContentView(scroll)
        for (n in listOf("Internet", "Playlist login", "Server", "Connections", "TV guide", "Films and TV Shows", "VPN", "Speed", "Dingus")) {
            val v = LayoutInflater.from(this).inflate(R.layout.item_setting_row, list, false)
            v.findViewById<TextView>(R.id.title).text = n
            list.addView(v)
            val r = Row(n, v)
            v.setOnClickListener { val fix = r.action; if (fix != null) fix() else if (!running) runAll() }
            rows.add(r)
            draw(r)
        }
        rows[0].view.requestFocus()
        runAll()
    }

    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun draw(r: Row) {
        val icon = r.view.findViewById<TextView>(R.id.rowIcon)
        icon.visibility = View.VISIBLE
        icon.typeface = android.graphics.Typeface.DEFAULT_BOLD
        icon.text = r.state.mark
        icon.setTextColor(r.state.colour)
        val sub = r.view.findViewById<TextView>(R.id.subtitle)
        sub.visibility = View.VISIBLE
        sub.text = r.line + (r.fix?.let { "\n→ $it" } ?: "")
        r.view.findViewById<TextView>(R.id.value).apply {
            visibility = if (r.action != null) View.VISIBLE else View.GONE
            text = "OK to fix"
        }
    }

    private fun set(i: Int, s: State, line: String, fix: String? = null, action: (() -> Unit)? = null) {
        val r = rows[i]; r.state = s; r.line = line; r.fix = fix; r.action = action; draw(r)
        if (rows.none { it.state == State.RUN }) summary()
    }

    private fun summary() {
        running = false
        val ok = rows.count { it.state == State.OK }; val warn = rows.count { it.state == State.WARN }; val bad = rows.count { it.state == State.BAD }
        val code = "D${BuildConfig.VERSION_NAME.replace(".", "")}-" + rows.joinToString("") { it.state.code.toString() }
        head.text = "Finished ${Ui.time(System.currentTimeMillis())}  ·  ✓ $ok fine" + (if (warn > 0) "  ·  ! $warn to look at" else "") + (if (bad > 0) "  ·  ✕ $bad problem${if (bad == 1) "" else "s"}" else "") +
            "\nCode for help: $code  ·  OK on a row without a fix checks again"
        Ui.test("check-done $code")
    }

    private fun runAll() {
        if (running) return
        running = true
        head.text = "Checking…  (10–20 seconds)"
        rows.forEach { it.state = State.RUN; it.line = "Waiting…"; it.fix = null; it.action = null; draw(it) }
        val p = Store.playlist
        scope.launch {
            // Internet
            val online = withContext(Dispatchers.IO) { try { Net.open("https://www.gstatic.com/generate_204", 8_000).close(); true } catch (e: Exception) { false } }
            if (online) set(0, State.OK, "Connected") else set(0, State.BAD, "Can't reach the internet", "Check the Fire TV's Wi-Fi or cable (Fire TV Settings → Network)")

            // Playlist login and connections
            if (p == null) {
                set(1, State.BAD, "No playlist added", "OK to add one") { startActivity(Intent(this@CheckActivity, AddPlaylistActivity::class.java)) }
                set(2, State.SKIP, "No playlist"); set(3, State.SKIP, "No playlist")
            } else {
                val info = withContext(Dispatchers.IO) { Account.check(p) }
                when {
                    info == null && Account.online(p) == false -> set(1, State.BAD, "The provider's server didn't answer", if (Failover.hasBackups(p)) "Dingus will try your backup servers" else "Add a backup server address in Settings → Playlist")
                    info == null -> set(1, State.OK, "${p.name}  ·  answering")
                    info.expires in 1 until System.currentTimeMillis() -> set(1, State.BAD, info.summary(), "The subscription has expired: renew it with your provider")
                    info.status.isNotBlank() && !info.status.equals("active", true) -> set(1, State.WARN, info.summary(), "The provider says \"${info.status}\": check with them")
                    else -> set(1, State.OK, "${p.name}  ·  ${info.summary().substringBefore("  ·  Active connections")}")
                }
                if (info != null && info.maxConnections > 0) {
                    if (info.activeConnections >= info.maxConnections) set(3, State.WARN, "${info.activeConnections} of ${info.maxConnections} in use (all of them)", "Stop watching on another device, or wait a minute for a closed one to clear")
                    else set(3, State.OK, "${info.activeConnections} of ${info.maxConnections} in use")
                } else set(3, State.SKIP, "Your provider doesn't say")

                // Server (and backups)
                val all = Failover.addresses(p)
                val at = Failover.index(p)
                // Each address: how quickly it answered, or why it didn't (shown for the main server).
                val why = arrayOfNulls<String>(all.size)
                val times = withContext(Dispatchers.IO) {
                    all.mapIndexed { i, a -> if (a.isBlank()) null else try {
                        val t0 = System.currentTimeMillis()
                        val url = if (p.type == "xtream") "${Sources.xtreamBase(a)}/player_api.php?username=${Sources.enc(p.user)}&password=${Sources.enc(p.pass)}" else a.trim()
                        Net.open(url, Failover.PATIENCE_MS).close(); System.currentTimeMillis() - t0
                    } catch (e: Exception) { why[i] = Failover.reason(e); null } }
                }
                val mainWhy = why[0]?.let { "  ·  main server: $it" } ?: ""
                val backups = if (all.size > 1) "  ·  backups: " + (1 until all.size).joinToString(", ") { i -> if (times[i] != null) "$i ✓" else "$i ✕" } else ""
                when {
                    times[at] == null && times.any { it != null } -> set(2, State.BAD, "The server in use isn't answering$mainWhy$backups", "OK to switch to one that is") {
                        scope.launch { val ok = withContext(Dispatchers.IO) { Failover.recover() }; Notice.done(if (ok) "Switched server" else "No other server answered", 3_000); runAll() }
                    }
                    times[at] == null -> set(2, State.BAD, "No server is answering$mainWhy$backups", "The provider may be down: try again later")
                    at > 0 -> {
                        val back: (() -> Unit)? = if (times[0] != null) ({ scope.launch { withContext(Dispatchers.IO) { Failover.recover() }; runAll() }; Unit }) else null
                        set(2, State.WARN, "Using backup server $at (${times[at]} ms)$mainWhy$backups", if (back != null) "OK to go back to the main server (it's answering again)" else "Fine for now: Dingus goes back to the main server when it answers", back)
                    }
                    else -> set(2, State.OK, "Main server  ·  ${times[at]} ms$backups")
                }
            }

            // TV guide
            if (Repo.channels.isEmpty()) set(4, State.SKIP, "No channels yet")
            else {
                val sources = (p?.guideOrder ?: emptyList()).size
                val have = Repo.channels.count { Repo.nowNext(it).first != null || Repo.programmes(it).isNotEmpty() }
                val age = (System.currentTimeMillis() - Store.guideLoadedAt) / 3_600_000L
                val line = "$have of ${Repo.channels.size} channels have listings  ·  ${if (sources > 1) "$sources sources  ·  " else ""}updated ${if (Store.guideLoadedAt == 0L) "never" else if (age < 1) "within the hour" else "$age h ago"}"
                val fixGuide = { scope.launch { Notice.working("Updating TV guide…"); try { Repo.refreshGuide(); Notice.done("TV guide updated") } catch (e: Exception) { Notice.done("TV guide: ${Net.friendly(e)}", 6_000) }; runAll() }; Unit }
                when {
                    have == 0 -> set(4, State.BAD, line, "OK to update the TV guide now", fixGuide)
                    have < Repo.channels.size / 2 || age >= 24 -> set(4, State.WARN, line, "OK to update the TV guide now (some channels may simply have no listings)", fixGuide)
                    else -> set(4, State.OK, line)
                }
            }

            // Films and TV Shows
            if (!Vod.available) set(5, State.SKIP, "Only with an Xtream Codes login")
            else {
                val m = Vod.cached(Vod.MOVIES)?.items?.size; val sh = Vod.cached(Vod.SERIES)?.items?.size
                if (m == null && sh == null) set(5, State.WARN, "Not loaded yet", "Open Films or TV Shows to load them")
                else {
                    // How the films list was put together: the provider's whole list, then each category's own.
                    val how = listOfNotNull(Vod.fetchInfo["${Vod.MOVIES}-whole"], Vod.fetchInfo[Vod.MOVIES]).joinToString("  ·  ")
                    val hidden = Vod.cached(Vod.MOVIES)?.categories?.count { Store.isHidden("${Vod.MOVIES}:${it.id}") } ?: 0
                    set(5, State.OK, "${Ui.count(m ?: 0)} films  ·  ${Ui.count(sh ?: 0)} shows" + (if (hidden > 0) "  ·  $hidden film categories hidden" else ""),
                        how.ifEmpty { null })
                }
            }

            // VPN
            when (Vpn.status) {
                Vpn.Status.ON -> { val ip = withContext(Dispatchers.IO) { VpnControls.lookUpIp() }; set(6, State.OK, "On  ·  ${Vpn.connectedTo?.label ?: ""}${ip?.let { "  ·  $it" } ?: ""}") }
                Vpn.Status.CONNECTING -> set(6, State.WARN, "Connecting…")
                Vpn.Status.ERROR -> set(6, State.BAD, "Couldn't connect: ${Vpn.message}", "OK to open the VPN settings") { startActivity(Intent(this@CheckActivity, SettingsActivity::class.java).putExtra("dingus_test_settings", "VPN")) }
                Vpn.Status.OFF -> set(6, State.SKIP, if (Vpn.ready) "Off" else "Not set up")
            }

            // Speed
            set(7, State.RUN, "Testing… (about 10 seconds)")
            val mbps = try { withContext(Dispatchers.IO) { Vpn.speedTest { } } } catch (e: Exception) { null }
            when {
                mbps == null -> set(7, State.WARN, "Couldn't run the test", if (online) "Try again in a minute" else "Fix the internet connection first")
                mbps < 8 -> set(7, State.BAD, "%.0f Mbps  ·  too slow for HD (8 Mbps needed)".format(mbps), "Move the Fire TV closer to the router, or use a cable")
                mbps < 25 -> set(7, State.OK, "%.0f Mbps  ·  good for HD (4K needs 25 Mbps)".format(mbps))
                else -> set(7, State.OK, "%.0f Mbps  ·  good for HD and 4K".format(mbps))
            }

            // Dingus
            val newer = Updater.available?.takeIf { Updater.newer(it.version, BuildConfig.VERSION_NAME) }
            if (newer != null) set(8, State.WARN, "Version ${BuildConfig.VERSION_NAME}  ·  ${newer.version} is ready", "OK to update") { Updater.install(this@CheckActivity, newer) }
            else set(8, State.OK, "Version ${BuildConfig.VERSION_NAME}  ·  up to date")
        }
    }
}
