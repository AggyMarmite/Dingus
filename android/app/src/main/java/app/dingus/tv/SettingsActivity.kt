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

    private val sections = listOf(
        "Playlists & EPG" to { playlistSection() },
        "Live TV" to { liveSection() },
        "Playback" to { playbackSection() },
        "Look" to { lookSection() },
        "VPN" to { vpnSection() },
        "About" to { aboutSection() },
    )
    private var section = -1
    private val sectionViews = ArrayList<TextView>()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_settings_split)
        side = SideMenu.attach(this, SideMenu.Page.SETTINGS)
        list = findViewById(R.id.list)
        val holder = findViewById<LinearLayout>(R.id.sections)
        for ((i, s) in sections.withIndex()) {
            val t = LayoutInflater.from(this).inflate(R.layout.item_settings_section, holder, false) as TextView
            t.text = s.first
            t.id = View.generateViewId()
            // Moving onto a section shows its options; OK (or RIGHT) goes into them.
            t.setOnFocusChangeListener { _, has -> if (has) showSection(i) }
            t.setOnClickListener { list.getChildAt(firstRow())?.requestFocus() }
            t.nextFocusRightId = View.NO_ID
            holder.addView(t)
            sectionViews.add(t)
        }
        showSection(0)
        sectionViews[0].requestFocus()
    }

    private fun firstRow(): Int { for (i in 0 until list.childCount) if (list.getChildAt(i).isFocusable) return i; return 0 }

    private fun showSection(i: Int) {
        if (i == section) return
        section = i
        sectionViews.forEachIndexed { n, v -> v.isActivated = n == i }
        list.removeAllViews(); subtitles.clear(); values.clear(); switches.clear()
        refreshRow = null; updateRow = null; accountRow = null
        header(sections[i].first)
        sections[i].second()
        findViewById<View>(R.id.pane).scrollTo(0, 0)
        update()
        Ui.test("settings-section ${sections[i].first}")
    }

    private fun playlistSection() {
        accountRow = row("Playlists", { playlistsSummary() }, { Store.playlist?.name ?: "" }) { playlists() }
        refreshRow = row("Update now", { refreshSummary() }) { refresh() }
        row("Update every", { "" }, { hoursName(Store.refreshHours) }) { refreshInterval() }
        row("TV guide sources", { guideSummary() }) { guideSources() }
        row("Keep TV guide for", { "Less is lighter on memory" }, { "${hoursName(Store.guideHours)} ahead" }) { guideLength() }
    }

    private fun liveSection() {
        toggle("Watch in the guide first", "OK plays in the guide window, OK again for full screen", { Store.preview }) { Store.preview = it }
        toggle("Channel numbers", "", { Store.showNumbers }) { Store.showNumbers = it }
        toggle("Colour-coded programmes", "Sport, films, news, kids…", { Store.genreColours }) { Store.genreColours = it }
        toggle("Merge duplicate channels", "HD, FHD and backups shown as one", { Store.mergeChannels }) { Store.mergeChannels = it; Repo.reapply() }
        row("Edit channels", { "Rename, renumber or hide" }, { "›" }) { startActivity(Intent(this, ChannelEditorActivity::class.java)) }
        row("Categories", { hiddenSummary() + "  ·  " + (if (listOf("live", "movies", "series").any { Store.categoryOrder(it).isNotEmpty() }) "your own order" else "the playlist's order") }, { "›" }) {
            val opts = listOf("Show hidden categories", "Put them back in the playlist's order")
            choose("Categories", opts, -1) { n ->
                if (n == 0) unhide()
                else { listOf("live", "movies", "series").forEach { Store.setCategoryOrder(it, emptyList()) }; Repo.reapply(); update() }
            }
        }
    }

    private fun playbackSection() {
        toggle("Pause & rewind live TV", "Keeps the last 15 minutes (MPEG-TS channels)", { Store.timeshift }) { Store.timeshift = it }
        row("Stream format", { "" }, { if (Store.streamFormat == "hls") "HLS" else "MPEG-TS" }) { streamFormat() }
        toggle("Subtitles automatically", "In your language, when there are some", { Store.autoSubtitles }) { Store.autoSubtitles = it }
        row("Subtitle language", { "" }, { java.util.Locale(Store.subtitleLanguage).displayLanguage.ifEmpty { Store.subtitleLanguage } }) { subtitleLanguage() }
        toggle("Match the TV's frame rate", "Smoother motion (50 Hz for UK TV, 24 Hz for films)", { Store.matchFrameRate }) { Store.matchFrameRate = it }
    }

    private fun lookSection() {
        row("Open Dingus on", { "" }, { if (Store.startScreen == "live") "Live TV" else "Home" }) {
            choose("Open Dingus on", listOf("Home", "Live TV"), if (Store.startScreen == "live") 1 else 0) {
                Store.startScreen = if (it == 1) "live" else "home"; update()
            }
        }
        toggle("Lite mode", "Simpler Home, fewer animations", { Store.homeLite }) { Store.homeLite = it }
        toggle("Title logos", "Official film and show logos, from TMDB", { Store.titleLogos }) { Store.titleLogos = it }
        row("Theme", { "" }, { "${Themes.background().name}  ·  ${Themes.accent().name}" }) { theme() }
    }

    private fun vpnSection() {
        row("Built-in VPN", { vpnSummary() }, { "›" }) { startActivity(Intent(this, VpnActivity::class.java)) }
    }

    private fun aboutSection() {
        updateRow = row("Check for updates", { "" }, { "Version ${BuildConfig.VERSION_NAME}" }) {
            updateRow?.let { sub(it, "Checking…") }
            Updater.check(this, manual = true) { msg -> updateRow?.let { sub(it, msg) } }
        }
        row("About Dingus", { "Film and show logos from TMDB (not endorsed or certified by TMDB)" }, { "Version ${BuildConfig.VERSION_NAME}" }) {}
    }

    override fun onResume() {
        super.onResume(); update(); Ui.test("settings-shown")
        // Expiry, connections in use and whether the server's online, fresh each time (at most every 20 seconds).
        val p = Store.playlist
        if (p != null && System.currentTimeMillis() - Account.lastCheck > 20_000L)
            scope.launch { Account.check(p); update() }
    }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun update() {
        for ((v, f) in subtitles) sub(v, f())
        for ((v, f) in values) if (v.parent != null) v.findViewById<TextView>(R.id.value).text = f()
        for ((v, f) in switches) if (v.parent != null) v.findViewById<android.widget.ImageView>(R.id.switchIcon).setImageResource(if (f()) R.drawable.switch_on else R.drawable.switch_off)
    }

    // ------------------------------------------------------------------ rows

    private fun header(text: String) {
        val v = LayoutInflater.from(this).inflate(R.layout.item_heading, list, false) as TextView
        v.text = text.uppercase()
        list.addView(v)
    }

    private val values = HashMap<View, () -> String>()
    private val switches = HashMap<View, () -> Boolean>()

    private fun row(title: String, subtitle: () -> String, value: (() -> String)? = null, click: () -> Unit): View {
        val v = LayoutInflater.from(this).inflate(R.layout.item_setting_row, list, false)
        v.findViewById<TextView>(R.id.title).text = title
        v.setOnClickListener { click() }
        list.addView(v)
        if (section >= 0) v.nextFocusLeftId = sectionViews[section].id // LEFT: back to this section, not a neighbour
        subtitles.add(v to subtitle)
        if (value != null) values[v] = value
        return v
    }

    private fun toggle(title: String, about: String, get: () -> Boolean, set: (Boolean) -> Unit): View {
        val v = row(title, { about }) { set(!get()); update() }
        v.findViewById<View>(R.id.switchIcon).visibility = View.VISIBLE
        switches[v] = get
        return v
    }

    private fun sub(v: View, s: String) {
        val t = v.findViewById<TextView>(R.id.subtitle)
        t.text = s
        t.visibility = if (s.isEmpty()) View.GONE else View.VISIBLE
    }

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
                    "Use this playlist" -> { Playlists.switchTo(p.id); update(); checkAccount(); PlaylistUpdate.ask(this) }
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

    /** BACK from a section's options goes back to the sections first. */
    @Deprecated("Deprecated in Java")
    override fun onBackPressed() {
        var v: Any? = currentFocus
        while (v is View) { if (v === list) { sectionViews.getOrNull(section)?.requestFocus(); return }; v = v.parent }
        super.onBackPressed()
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
