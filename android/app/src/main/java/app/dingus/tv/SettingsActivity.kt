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
import java.io.File

/** Settings: playlists, TV guide, playback, look, updates. */
class SettingsActivity : BaseActivity() {
    private lateinit var side: SideMenu
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var list: LinearLayout
    private var refreshing = false
    /** Each row's subtitle is worked out again whenever something changes. */
    private val subtitles = ArrayList<Pair<View, () -> String>>()
    private var refreshRow: View? = null
    private var updateRow: View? = null
    private var accountRow: View? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings)
        side = SideMenu.attach(this, SideMenu.Page.SETTINGS)
        list = findViewById(R.id.list)

        header("Playlist & TV guide")
        val first = row("Playlists", { playlistsSummary() }) { playlists() }
        accountRow = first
        row("TV guide sources", { guideSummary() }) { guideSources() }
        refreshRow = row("Update channels and TV guide now", { refreshSummary() }) { refresh() }
        row("Playlist update interval", { "Every ${hoursName(Store.refreshHours)}  ·  also when you choose \"Update now\"" }) { refreshInterval() }
        row("How much TV guide to keep", { "${hoursName(Store.guideHours)} ahead  ·  less is lighter on memory" }) { guideLength() }

        header("Live TV")
        toggle("Watch in the guide first", "OK on a channel plays it in the guide's window, OK again for full screen (off: straight to full screen)", { Store.preview }) { Store.preview = it }
        toggle("Channel numbers", "Show each channel's number beside its name", { Store.showNumbers }) { Store.showNumbers = it }
        toggle("Colour-coded programmes", "A coloured line for sport, films, news, kids…", { Store.genreColours }) { Store.genreColours = it }
        toggle("Merge duplicate channels", "Show a channel's HD, FHD and backup versions as one, and try the next if one fails",
            { Store.mergeChannels }) { Store.mergeChannels = it; Repo.reapply() }
        row("Edit channels", { "Rename, renumber or hide channels" }) { startActivity(Intent(this, ChannelEditorActivity::class.java)) }
        row("Hidden categories", { hiddenSummary() }) { unhide() }
        row("Category order", {
            if (listOf("live", "movies", "series").any { Store.categoryOrder(it).isNotEmpty() }) "Your own order  ·  press OK to go back to the playlist's"
            else "The playlist's order  ·  press ≡ on a category to move it"
        }) {
            listOf("live", "movies", "series").forEach { Store.setCategoryOrder(it, emptyList()) }
            Repo.reapply(); update()
        }

        header("VPN")
        row("Built-in VPN", { vpnSummary() }) { startActivity(Intent(this, VpnActivity::class.java)) }

        header("Playback")
        toggle("Pause & rewind live TV", "Keeps the last 15 minutes while you watch (MPEG-TS channels)", { Store.timeshift }) { Store.timeshift = it }
        row("Stream format", { if (Store.streamFormat == "hls") "HLS" else "MPEG-TS (recommended)" }) { streamFormat() }
        toggle("Subtitles automatically", "On: subtitles in your language whenever there are some. Off: subtitles start off (films, shows and live TV)",
            { Store.autoSubtitles }) { Store.autoSubtitles = it }
        row("Subtitle language", { java.util.Locale(Store.subtitleLanguage).displayLanguage.ifEmpty { Store.subtitleLanguage } }) { subtitleLanguage() }
        toggle("Match the TV's frame rate", "Switches the TV to suit the video (50 Hz for UK TV, 24 Hz for films) for smoother motion",
            { Store.matchFrameRate }) { Store.matchFrameRate = it }

        header("Look")
        row("Open Dingus on", { if (Store.startScreen == "live") "Live TV" else "Home" }) {
            choose("Open Dingus on", listOf("Home", "Live TV"), if (Store.startScreen == "live") 1 else 0) {
                Store.startScreen = if (it == 1) "live" else "home"; update()
            }
        }
        toggle("Lite mode", "A simpler Home and fewer animations, if Dingus feels slow", { Store.homeLite }) { Store.homeLite = it }
        toggle("Title logos for films and shows", "The official logo in place of the plain title, where TMDB has one", { Store.titleLogos }) { Store.titleLogos = it }
        row("Theme", { "${Themes.background().name}  ·  ${Themes.accent().name}" }) { theme() }

        header("Dingus")
        updateRow = row("Check for updates", { "You have version ${BuildConfig.VERSION_NAME}" }) {
            updateRow?.let { sub(it, "Checking…") }
            Updater.check(this, manual = true) { msg -> updateRow?.let { sub(it, msg) } }
        }
        row("About Dingus", { "Version ${BuildConfig.VERSION_NAME}  ·  Film and show logos from TMDB (not endorsed or certified by TMDB)" }) {}
        first.requestFocus()
    }

    override fun onResume() {
        super.onResume(); update(); Ui.test("settings-shown")
        // Expiry, connections in use and whether the server's online, fresh each time (at most every 20 seconds).
        val p = Store.playlist
        if (p != null && System.currentTimeMillis() - Account.lastCheck > 20_000L)
            scope.launch { Account.check(p); update() }
    }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun update() { for ((v, f) in subtitles) sub(v, f()) }

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

    private fun toggle(title: String, about: String, get: () -> Boolean, set: (Boolean) -> Unit): View =
        row(title, { (if (get()) "On" else "Off") + "  ·  " + about }) { set(!get()); update() }

    private fun sub(v: View, s: String) { v.findViewById<TextView>(R.id.subtitle).text = s }

    private fun choose(title: String, options: List<String>, current: Int, done: (Int) -> Unit) {
        Dialogs.builder(this).setTitle(title)
            .setSingleChoiceItems(options.toTypedArray(), current) { d, i -> d.dismiss(); done(i) }
            .show()
    }

    private fun vpnSummary(): String = when (Vpn.status) {
        Vpn.Status.ON -> "On  ·  ${Vpn.connectedTo?.label ?: ""}  ·  Dingus only"
        Vpn.Status.CONNECTING -> "Connecting…"
        else -> if (Vpn.ready) "Off  ·  ${if (Vpn.provider == Vpn.SURFSHARK) "Surfshark" else "${Vpn.configs.size} config files"}" else "Off  ·  Surfshark or any WireGuard VPN, for Dingus only"
    }

    // ------------------------------------------------------------------ playlists

    private fun typeName(p: Playlist) = if (p.type == "xtream") "Xtream Codes login" else "M3U link"

    private fun playlistsSummary(): String {
        val all = Store.playlists
        val p = Store.playlist ?: return "Add your playlist"
        return "${p.name}  ·  ${typeName(p)}" + (if (all.size > 1) "  ·  ${all.size} playlists" else "") +
            (Account.saved()?.let { "\n" + it.summary() } ?: "") +
            when (Account.online(p)) { true -> "\nStatus: online"; false -> "\nStatus: offline"; null -> "" }
    }

    private fun playlists() {
        val all = Store.playlists
        val names = all.map { (if (it.id == Store.activeId) "✓  " else "     ") + it.name + "   (" + typeName(it) + ")" } + "+  Add a playlist"
        Dialogs.builder(this).setTitle("Playlists")
            .setItems(names.toTypedArray()) { _, i ->
                if (i == all.size) startActivity(Intent(this, AddPlaylistActivity::class.java).putExtra(AddPlaylistActivity.NEW, true))
                else playlistOptions(all[i])
            }
            .show()
    }

    private fun playlistOptions(p: Playlist) {
        val active = p.id == Store.activeId
        val opts = listOfNotNull(if (!active) "Use this playlist" else null, if (active) "Check subscription" else null, "Edit", "Remove")
        Dialogs.builder(this).setTitle(p.name)
            .setItems(opts.toTypedArray()) { _, i ->
                when (opts[i]) {
                    "Use this playlist" -> { Playlists.switchTo(p.id); update(); checkAccount() }
                    "Check subscription" -> checkAccount()
                    "Edit" -> startActivity(Intent(this, AddPlaylistActivity::class.java).putExtra(AddPlaylistActivity.ID, p.id))
                    "Remove" -> Dialogs.builder(this).setTitle("Remove “${p.name}”?")
                        .setMessage("Its channels, favourites and history on this device are removed too.")
                        .setPositiveButton("Remove") { _, _ -> Playlists.remove(this, p.id); update() }
                        .setNegativeButton("Cancel", null).show()
                }
            }
            .show()
    }

    private fun checkAccount() {
        val p = Store.playlist ?: return
        val row = accountRow ?: return
        sub(row, "${p.name}\nChecking your subscription…")
        scope.launch {
            val info = Account.check(p)
            val msg = info?.summary() ?: if (Account.online(p) == false) "Couldn't reach the server (offline)" else "Your provider doesn't share an expiry date for this playlist"
            sub(row, "${p.name}  ·  ${typeName(p)}\n$msg")
        }
    }

    // ------------------------------------------------------------------ TV guide

    private fun guideSummary(): String {
        val p = Store.playlist ?: return "Add a playlist first"
        val own = p.epgUrls.size
        return when {
            own == 0 && p.useProviderGuide -> "The provider's guide  ·  add your own guide address here"
            own == 0 -> "None"
            p.useProviderGuide -> "$own of your own, then the provider's"
            else -> "$own of your own"
        }
    }

    private fun savePlaylist(p: Playlist) {
        Store.playlist = p
        Store.guideLoadedAt = 0
        update()
        refreshGuideOnly()
    }

    private fun guideSources() {
        val p = Store.playlist ?: return
        val urls = p.epgUrls
        val items = urls.mapIndexed { i, u -> "${i + 1}.  $u" } +
            ("Provider's guide (used after yours): " + if (p.useProviderGuide) "On" else "Off") + "+  Add a guide address"
        Dialogs.builder(this).setTitle("TV guide sources (first is used first)")
            .setItems(items.toTypedArray()) { _, i ->
                when {
                    i < urls.size -> guideSourceOptions(p, i)
                    i == urls.size -> savePlaylist(p.copy(useProviderGuide = !p.useProviderGuide))
                    else -> Dialogs.input(this, "Add a TV guide address", "", "http://…/guide.xml") { u ->
                        if (u.isNotEmpty()) savePlaylist(p.copy(epgUrl = (urls + u).joinToString("\n")))
                    }
                }
            }
            .show()
    }

    private fun guideSourceOptions(p: Playlist, i: Int) {
        val urls = p.epgUrls.toMutableList()
        val opts = listOfNotNull(if (i > 0) "Move up (use sooner)" else null, if (i < urls.size - 1) "Move down" else null, "Edit", "Remove")
        Dialogs.builder(this).setTitle(urls[i])
            .setItems(opts.toTypedArray()) { _, k ->
                when (opts[k]) {
                    "Move up (use sooner)" -> { urls.add(i - 1, urls.removeAt(i)); savePlaylist(p.copy(epgUrl = urls.joinToString("\n"))) }
                    "Move down" -> { urls.add(i + 1, urls.removeAt(i)); savePlaylist(p.copy(epgUrl = urls.joinToString("\n"))) }
                    "Edit" -> Dialogs.input(this, "TV guide address", urls[i], "http://…/guide.xml") { u ->
                        if (u.isEmpty()) urls.removeAt(i) else urls[i] = u
                        savePlaylist(p.copy(epgUrl = urls.joinToString("\n")))
                    }
                    "Remove" -> { urls.removeAt(i); savePlaylist(p.copy(epgUrl = urls.joinToString("\n"))) }
                }
            }
            .show()
    }

    private fun refreshInterval() {
        val opts = listOf(4, 8, 12, 24, 48, 72)
        choose("Update the playlist and guide every…", opts.map { hoursName(it) + if (it == 24) "  (recommended)" else "" }, opts.indexOf(Store.refreshHours)) {
            Store.refreshHours = opts[it]
            update()
        }
    }

    private fun hoursName(h: Int) = when { h % 24 == 0 && h > 24 -> "${h / 24} days"; h == 24 -> "1 day"; else -> "$h hours" }

    private fun guideLength() {
        val opts = listOf(12, 24, 48, 72)
        choose("How much TV guide to keep", opts.map { hoursName(it) + if (it == 24) "  (recommended)" else "" }, opts.indexOf(Store.guideHours)) {
            Store.guideHours = opts[it]
            update()
            refreshGuideOnly()
        }
    }

    private fun refreshSummary(): String {
        val at = Store.guideLoadedAt
        return "${Repo.channels.size} channels" + if (at > 0) "  ·  guide updated ${Ui.day(at).trim().ifEmpty { "today" }} at ${Ui.time(at)}" else ""
    }

    private fun refresh() {
        val p = Store.playlist ?: return
        if (refreshing) return
        refreshing = true
        scope.launch {
            val row = refreshRow ?: return@launch
            try {
                sub(row, "Updating channels…")
                Notice.working("Updating playlist & TV guide…")
                Repo.refreshChannels(p)
                sub(row, "Updating the TV guide…")
                Repo.refreshGuide()
                Vod.reset()
                refreshing = false
                Notice.done("Playlist & TV guide updated")
                update()
            } catch (e: Exception) {
                refreshing = false
                Notice.done("Couldn't update: ${Net.friendly(e)}", 6_000)
                sub(row, Net.friendly(e))
            }
        }
    }

    private fun refreshGuideOnly() {
        if (refreshing || Repo.channels.isEmpty()) return
        refreshing = true
        scope.launch {
            val row = refreshRow ?: return@launch
            try {
                sub(row, "Updating the TV guide…")
                Repo.refreshGuide()
                refreshing = false
                update()
            } catch (e: Exception) {
                refreshing = false
                sub(row, "TV guide: ${Net.friendly(e)}")
            }
        }
    }

    // ------------------------------------------------------------------ categories, playback, look

    private fun hiddenSummary(): String {
        val n = Store.hidden.size
        return if (n == 0) "None  ·  press ≡ on a category to hide it" else "$n hidden  ·  choose to show them again"
    }

    private fun unhide() {
        val keys = Store.hidden.toList()
        if (keys.isEmpty()) return
        val names = keys.map { k ->
            val kind = k.substringBefore(':'); val id = k.substringAfter(':')
            val name = when (kind) {
                "live" -> Repo.categories.firstOrNull { it.id == id }?.name
                else -> Vod.cached(kind)?.categories?.firstOrNull { it.id == id }?.name
            } ?: "Category $id"
            name + when (kind) { "movies" -> "  (Films)"; "series" -> "  (TV Shows)"; else -> "  (Live TV)" }
        }
        val show = BooleanArray(keys.size)
        Dialogs.builder(this).setTitle("Show again")
            .setMultiChoiceItems(names.toTypedArray(), show) { _, i, on -> show[i] = on }
            .setPositiveButton("Done") { _, _ ->
                keys.forEachIndexed { i, k -> if (show[i]) Store.setHidden(k, false) }
                update()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun streamFormat() {
        choose("Stream format (Xtream logins)", listOf("MPEG-TS  (recommended – needed for pause & rewind)", "HLS  (try this if channels stutter or won't play)"),
            if (Store.streamFormat == "hls") 1 else 0) { Store.streamFormat = if (it == 1) "hls" else "ts"; update() }
    }

    private fun subtitleLanguage() {
        val codes = listOf("en", "es", "fr", "de", "it", "pt", "nl", "pl", "sv", "da", "no", "fi", "el", "tr", "ro", "hu", "cs",
            "ru", "uk", "ar", "he", "hi", "ur", "bn", "pa", "ta", "zh", "ja", "ko", "th", "vi", "id", "ms", "fa")
        val names = codes.map { java.util.Locale(it).displayLanguage.ifEmpty { it } }
        choose("Subtitle language", names, codes.indexOf(Store.subtitleLanguage)) { Store.subtitleLanguage = codes[it]; update() }
    }

    private fun theme() {
        val bgs = Themes.backgrounds
        choose("Background", bgs.map { it.name }, bgs.indexOf(Themes.background())) { b ->
            val acs = Themes.accents
            choose("Accent colour", acs.map { it.name }, acs.indexOf(Themes.accent())) { a ->
                Store.background = bgs[b].key
                Store.accent = acs[a].key
                Themes.version++
                Ui.test("theme ${bgs[b].key} ${acs[a].key}")
                recreate()
            }
        }
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean = side.onKey(event) || super.dispatchKeyEvent(event)
}

/** Switching between and removing playlists. */
object Playlists {
    fun switchTo(id: String) {
        if (id == Store.activeId) return
        Store.activeId = id
        Repo.reset()
        Vod.reset()
        Ui.test("playlist ${Store.playlist?.name}")
    }

    fun remove(a: android.app.Activity, id: String) {
        val wasActive = id == Store.activeId
        if (wasActive) Store.playlist = null else Store.playlists = Store.playlists.filter { it.id != id }
        File(a.filesDir, "p_$id").deleteRecursively()
        if (android.os.Build.VERSION.SDK_INT >= 24) a.deleteSharedPreferences("progress_$id")
        if (wasActive) { Repo.reset(); Vod.reset() }
    }

}
