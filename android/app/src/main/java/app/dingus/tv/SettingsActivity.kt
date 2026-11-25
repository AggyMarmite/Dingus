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
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
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
        "Playlist" to { playlistSection() },
        "TV guide" to { guideSection() },
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
            t.setOnFocusChangeListener { _, has -> if (has) showSection(i); CatPill.style(t) }
            t.setOnClickListener { list.getChildAt(firstRow())?.requestFocus() }
            t.nextFocusRightId = View.NO_ID
            holder.addView(t)
            sectionViews.add(t)
        }
        showSection(0)
        // Open on a section by name (after changing the text size; the self-test too).
        (intent.getStringExtra("section") ?: if (BuildConfig.DEBUG) intent.getStringExtra("dingus_test_section") else null)?.let { n ->
            sections.indexOfFirst { it.first == n }.takeIf { it >= 0 }?.let { i -> sectionViews[i].requestFocus(); showSection(i) }
        }
        sectionViews[0].requestFocus()
        val accent = Themes.color(this, R.attr.dAccent)
        findViewById<View>(R.id.settingsGlow).background = android.graphics.drawable.GradientDrawable().apply {
            gradientType = android.graphics.drawable.GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf((accent and 0x00FFFFFF) or (0x30 shl 24), (accent and 0x00FFFFFF) or (0x0C shl 24), 0)
            gradientRadius = resources.displayMetrics.widthPixels * 0.5f
            setGradientCenter(0.85f, -0.05f)
        }
    }

    /** The time, top right. */
    private val tick = object : Runnable {
        override fun run() {
            val c = findViewById<TextView>(R.id.clock) ?: return
            c.text = Ui.time(System.currentTimeMillis())
            c.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L + 50)
        }
    }
    override fun onStart() { super.onStart(); findViewById<View>(R.id.clock)?.removeCallbacks(tick); tick.run() }
    override fun onStop() { super.onStop(); findViewById<View>(R.id.clock)?.removeCallbacks(tick) }

    private fun firstRow(): Int { for (i in 0 until list.childCount) if (list.getChildAt(i).isFocusable) return i; return 0 }

    private fun showSection(i: Int) {
        if (i == section) return
        section = i
        sectionViews.forEachIndexed { n, v -> v.isActivated = n == i; CatPill.style(v) }
        list.removeAllViews(); subtitles.clear(); values.clear(); switches.clear()
        refreshRow = null; updateRow = null; accountRow = null; vpnRow = null; guideRow = null
        header(sections[i].first)
        sections[i].second()
        findViewById<View>(R.id.pane).scrollTo(0, 0)
        update()
        Ui.test("settings-section ${sections[i].first}")
    }

    private fun playlistSection() {
        accountRow = row("Playlists", { playlistsSummary() }, { Store.playlist?.name ?: "" }) { playlists() }
        row("Backup servers", { backupSummary() }) { backupServers() }
        refreshRow = row("Update now", { refreshSummary() }) { refresh() }
        row("Update every", { "" }, { hoursName(Store.refreshHours) }) { refreshInterval() }
        row("Channel logo priority", { "Which logo to use when the playlist and the TV guide both have one" }, { if (Store.guideLogosFirst) "TV guide first" else "Playlist first" }) {
            choose("Channel logo priority", listOf("Playlist first", "TV guide first"), if (Store.guideLogosFirst) 1 else 0) { n ->
                Store.guideLogosFirst = n == 1; Repo.reapply(); update(); Ui.test("logo-priority ${if (n == 1) "guide" else "playlist"}")
            }
        }
        row("Full refresh", { "Clears the saved channels, films, shows and TV guide, then downloads everything again" }, { "›" }) { fullRefresh() }
    }

    private fun guideSection() {
        guideRow = row("Update TV guide now", { guideNowSummary() }) { refreshGuideOnly(force = true) }
        row("Source priority", { priorityLine() }, { "›" }) { sourcePriority() }
        row("TV guide sources", { guideSummary() }, { "›" }) { guideSources() }
        row("Keep TV guide for", { "Less is lighter on memory" }, { "${hoursName(Store.guideHours)} ahead" }) { guideLength() }
        row("Clear TV guide", { "Deletes the saved listings (they come back with the next update)" }, { "›" }) {
            Dialogs.confirm(this, "Clear the TV guide?", "The saved listings are deleted. They come back the next time the guide updates (or press Update TV guide now).", "Clear TV guide") {
                Repo.clearGuide(); update(); Notice.done("TV guide cleared", 2_500); Ui.test("guide-cleared")
            }
        }
    }

    private var guideRow: View? = null

    private fun guideNowSummary(): String {
        val at = Store.guideLoadedAt
        return if (at > 0) "Updated ${Ui.day(at).trim().ifEmpty { "today" }} at ${Ui.time(at)}  ·  ${hoursName(Store.guideHours)} of listings" else "Not downloaded yet"
    }

    private fun guideName(u: String) = if (u == Playlist.PROVIDER_GUIDE) "Provider's guide" else u.substringAfterLast('/').substringBefore('?').ifEmpty { u }

    private fun priorityLine(): String {
        val p = Store.playlist ?: return ""
        val order = p.guideOrder
        return if (order.isEmpty()) "No guides  ·  add one in TV guide sources" else order.mapIndexed { i, u -> "${i + 1}. ${guideName(u)}" }.joinToString("  ·  ")
    }

    /** The guides in the order they're used: the first wins where two have the same channel; the next fills gaps. */
    private fun sourcePriority() {
        val p = Store.playlist ?: return
        val order = p.guideOrder
        if (order.size < 2) { Notice.done(if (order.isEmpty()) "Add a guide in TV guide sources first" else "There's only one guide, so nothing to order", 3_000); return }
        Dialogs.builder(this).setTitle("Source priority  ·  top is used first")
            .setItems(order.mapIndexed { i, u -> "${i + 1}.  ${guideName(u)}" }.toTypedArray()) { _, i ->
                val opts = listOfNotNull(if (i > 0) "Move up" else null, if (i < order.size - 1) "Move down" else null)
                Dialogs.builder(this).setTitle(guideName(order[i]))
                    .setItems(opts.toTypedArray()) { _, k ->
                        // Swap it with its neighbour in the saved list (which keeps the provider guide's place even
                        // while that's switched off).
                        val to = if (opts[k] == "Move up") i - 1 else i + 1
                        val list = rawGuides(p).also { if (Playlist.PROVIDER_GUIDE !in it) it.add(Playlist.PROVIDER_GUIDE) }
                        val a = list.indexOf(order[i]); val b = list.indexOf(order[to])
                        if (a >= 0 && b >= 0) { list[a] = order[to]; list[b] = order[i] }
                        savePlaylist(p.copy(epgUrl = list.joinToString("\n")))
                        Ui.test("guide-priority ${list.joinToString(",") { guideName(it) }}")
                        sourcePriority()
                    }
                    .show()
            }
            .show()
    }

    /** Clears everything saved for the playlist and downloads it all again (favourites, My List, reminders and edits stay). */
    private fun fullRefresh() {
        val p = Store.playlist ?: return
        Dialogs.confirm(this, "Full refresh?", "Dingus clears everything it has saved for ${p.name} and downloads it again: channels, films, shows and the TV guide. This can take a few minutes.\n\nYour favourites, My List, reminders and channel edits are kept.", "Full refresh") {
            if (refreshing) return@confirm
            refreshing = true
            Ui.test("full-refresh")
            // (Carries on if you leave Settings: everything saved has already been cleared.)
            Background.scope.launch {
                try {
                    Notice.working("Full refresh: clearing…")
                    Repo.clear(); Vod.clear(); Store.guideLoadedAt = 0
                    // Films and shows download at the same time as the channels and guide.
                    val vod = if (Vod.available) listOf(Vod.MOVIES, Vod.SERIES).map { k -> async(Dispatchers.IO) { try { Vod.library(k, fresh = true) } catch (_: Exception) { null } } } else emptyList()
                    Repo.refreshChannels(p)
                    Repo.refreshGuide()
                    vod.forEach { it.await() }
                    Notice.done("Full refresh done  ·  ${Repo.channels.size} channels")
                    Ui.test("full-refresh done ${Repo.channels.size}")
                } catch (e: kotlinx.coroutines.CancellationException) { throw e
                } catch (e: Exception) {
                    Notice.done("Full refresh didn't finish: ${Net.friendly(e)}", 6_000)
                } finally { refreshing = false; if (!isDestroyed) update() }
            }
        }
    }

    private fun liveSection() {
        subhead("The guide")
        toggle("Watch in the guide first", "OK plays in the guide window, OK again for full screen", { Store.preview }) { Store.preview = it }
        toggle("Channel numbers", "", { Store.showNumbers }) { Store.showNumbers = it }
        toggle("Colour-coded programmes", "Sport, films, news, kids…", { Store.genreColours }) { Store.genreColours = it }
        subhead("Watching")
        toggle("Pause & rewind live TV", "Keeps the last 15 minutes (MPEG-TS channels)", { Store.timeshift }) { Store.timeshift = it }
        row("Stream format", { "" }, { if (Store.streamFormat == "hls") "HLS" else "MPEG-TS" }) { streamFormat() }
        subhead("Channels")
        toggle("Merge duplicate channels", "HD, FHD and backups shown as one", { Store.mergeChannels }) { Store.mergeChannels = it; reapplySoon() }
        toggle("Merge matching categories", "e.g. “UK| GENERAL” and “UK| GENERAL HEVC” as one, each channel beside its match", { Store.mergeCategories }) { Store.mergeCategories = it; reapplySoon() }
        toggle("Remove divider channels", "Leave out “##### UK #####” and the like", { Store.removeDividers }) { Store.removeDividers = it; reapplySoon() }
        row("Edit channels", { "Rename, renumber or hide" }, { "›" }) { startActivity(Intent(this, ChannelEditorActivity::class.java)) }
        row("Categories", { hiddenSummary() + "  ·  " + (if (listOf("live", "movies", "series").any { Store.categoryOrder(it).isNotEmpty() }) "your own order" else "the playlist's order") }, { "›" }) {
            val opts = listOf("Show hidden categories", "Put them back in the playlist's order")
            choose("Categories", opts, -1) { n ->
                if (n == 0) unhide()
                else Dialogs.confirm(this, "Use the playlist's order?", "Your own order for live TV, films and TV shows categories will be cleared.", "Use the playlist's order") {
                    listOf("live", "movies", "series").forEach { Store.setCategoryOrder(it, emptyList()) }; Repo.reapply(); update()
                }
            }
        }
        subhead("Reminders")
        row("Remind me", { "How long before a programme its reminder comes up" }, { reminderLeadName(Store.reminderLead) }) {
            val opts = listOf(30, 60, 120, 300)
            choose("Remind me", opts.map { reminderLeadName(it) + " before" }, opts.indexOf(Store.reminderLead)) { n ->
                Store.reminderLead = opts[n]; Reminders.rescheduleAll(); update()
            }
        }
        toggle("Switch for reminders", "Change to the channel when a reminded programme starts", { Store.reminderSwitch }) { Store.reminderSwitch = it }
        row("Series reminders", { Reminders.series().size.let { if (it == 0) "None yet  ·  OK on a programme in the guide → Every episode" else "$it programme${if (it == 1) "" else "s"}  ·  OK to see or cancel them" } }, { "›" }) {
            val all = Reminders.series()
            if (all.isEmpty()) return@row
            val names = all.map { s -> "${Repo.channel(s.channelId)?.let { c -> Reminders.all().firstOrNull { it.channelId == c.id && Reminders.seriesTitle(it.title) == s.title }?.title } ?: s.title.replaceFirstChar { it.uppercase() }}  ·  ${s.channelName}" }
            val cancel = BooleanArray(all.size)
            Dialogs.builder(this).setTitle("Series reminders  ·  tick to cancel")
                .setMultiChoiceItems(names.toTypedArray(), cancel) { _, i, on -> cancel[i] = on }
                .setPositiveButton("Cancel ticked") { _, _ -> all.forEachIndexed { i, s -> if (cancel[i]) Reminders.cancelSeries(s.channelId, s.title) }; update() }
                .setNegativeButton("Close", null)
                .show()
        }
    }

    private fun playbackSection() {
        toggle("Subtitles automatically", "In your language, when there are some", { Store.autoSubtitles }) { Store.autoSubtitles = it }
        row("Subtitle language", { "" }, { java.util.Locale(Store.subtitleLanguage).displayLanguage.ifEmpty { Store.subtitleLanguage } }) { subtitleLanguage() }
        toggle("Play the next episode automatically", "Otherwise “Up next” waits for you near the end", { Store.autoNext }) { Store.autoNext = it }
        toggle("Match the TV's frame rate", "Smoother motion (50 Hz for UK TV, 24 Hz for films)", { Store.matchFrameRate }) { Store.matchFrameRate = it }
        toggle("Match the TV's dynamic range", "HDR channels in full screen switch the TV to HDR", { Store.matchDynamicRange }) { Store.matchDynamicRange = it }
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
        row("Text size", { "Bigger text is easier to read from the sofa" }, { when (Store.textSize) { "large" -> "Large"; "larger" -> "Extra large"; else -> "Normal" } }) {
            val ids = listOf("normal", "large", "larger")
            choose("Text size", listOf("Normal", "Large", "Extra large"), ids.indexOf(Store.textSize).coerceAtLeast(0)) { n ->
                if (ids[n] == Store.textSize) return@choose
                Store.textSize = ids[n]
                Themes.version++ // every screen redraws at the new size (this one straight away)
                Ui.test("text-size ${ids[n]}")
                intent.putExtra("section", "Look")
                recreate()
            }
        }
        row("Home rows", { "Choose which rows Home shows, and their order" }, { if (HomeRows.customised) "Your own" else "As usual" }) { homeRows() }
        row("VPN indicator", { "Shown under the clock while the VPN is on" }, { if (Store.vpnIndicator == "text") "“VPN”" else "Shield" }) {
            choose("VPN indicator", listOf("Shield", "The word “VPN”"), if (Store.vpnIndicator == "text") 1 else 0) { n ->
                Store.vpnIndicator = if (n == 1) "text" else "shield"; update()
                findViewById<TextView>(R.id.sideVpn)?.let { VpnIndicator.bind(it) }
            }
        }
    }

    // ------------------------------------------------------------------ cache

    private var cacheSize: Long? = null
    private fun mb(b: Long) = if (b < 1024 * 1024) "${b / 1024} KB" else "${b / (1024 * 1024)} MB"

    /** Saved pictures plus anything else in the cache folder (not the live TV rewind buffer, which is in use). */
    private fun cacheBytes(): Long = Logos.diskBytes() + Posters.diskBytes() +
        (cacheDir.listFiles() ?: emptyArray()).filter { it.name !in setOf("logos", "posters", "timeshift") }.sumOf { f -> f.walkTopDown().filter { it.isFile }.sumOf { it.length() } }

    private fun clearCache() {
        Dialogs.confirm(this, "Clear the cache?", "Saved pictures, logos and posters are deleted to free up space. They come back as you browse.\n\nYour playlist, TV guide, favourites, My List and settings aren't touched.", "Clear cache") {
            scope.launch {
                val before = withContext(Dispatchers.IO) { cacheBytes() }
                withContext(Dispatchers.IO) {
                    Logos.clearAll(); Posters.clearAll()
                    (cacheDir.listFiles() ?: emptyArray()).filter { it.name !in setOf("logos", "posters", "timeshift") }.forEach { it.deleteRecursively() }
                }
                cacheSize = withContext(Dispatchers.IO) { cacheBytes() }
                update()
                Notice.done("Cache cleared  ·  ${mb((before - (cacheSize ?: 0)).coerceAtLeast(0))} freed", 3_500)
                Ui.test("cache-cleared")
            }
        }
    }

    // ------------------------------------------------------------------ Home rows

    private fun homeRows() {
        val order = HomeRows.order()
        val hidden = HomeRows.hidden()
        val items = order.mapIndexed { i, n -> "${i + 1}.  $n   ·   ${if (n in hidden) "Hidden" else "Shown"}" } + "↺  Put Home back as it was"
        Dialogs.builder(this).setTitle("Home rows  ·  top is shown first")
            .setItems(items.toTypedArray()) { _, i ->
                if (i == order.size) { HomeRows.reset(); update(); Ui.test("home-rows reset"); return@setItems }
                val name = order[i]
                val opts = listOfNotNull(if (name in hidden) "Show it" else "Hide it", if (i > 0) "Move up" else null, if (i < order.size - 1) "Move down" else null)
                Dialogs.builder(this).setTitle(name)
                    .setItems(opts.toTypedArray()) { _, k ->
                        when (opts[k]) {
                            "Show it" -> HomeRows.setHidden(name, false)
                            "Hide it" -> HomeRows.setHidden(name, true)
                            else -> { val l = order.toMutableList(); l.add(if (opts[k] == "Move up") i - 1 else i + 1, l.removeAt(i)); HomeRows.setOrder(l) }
                        }
                        Ui.test("home-rows ${opts[k]} $name")
                        update(); homeRows()
                    }
                    .show()
            }
            .show()
    }

    // ------------------------------------------------------------------ VPN

    private val vpn: VpnControls by lazy { VpnControls(this, scope, { update(); styleVpnRow() }, { vpn.openSetup(phone = true) }) }
    private var vpnRow: View? = null
    private val vpnListener: () -> Unit = { update(); styleVpnRow(); vpn.onVpnChanged() }

    private fun styleVpnRow() { vpnRow?.let { vpn.styleStatus(it, it.findViewById(R.id.title)) } }

    /** On/off with who you're connected to, Quick Connect, Location and the IP address; setting it up is a step further in. */
    private fun vpnSection() {
        vpnRow = row("VPN", { vpn.statusLine() }) { vpn.toggle() }
        styleVpnRow()
        row("⚡  Quick Connect", { vpn.quickLine() }) { vpn.quickConnect() }
        row("Location", { vpn.locationLine() }, { "›" }) { vpn.chooseLocation() }
        row("IP address", { vpn.ipLine() }, { vpn.ipValue() }) { vpn.checkIp() }
        row("Speed test", { "How fast streams come through the VPN right now" }, { Store.vpnLastSpeed.ifEmpty { "" }.let { if (it.isEmpty()) "" else "Last: $it" } }) { vpn.speedTest { update() } }
        toggle("Reconnect by itself", "If the VPN drops, connect again: the same place, or the least busy one nearby", { Store.vpnReconnect }) { Store.vpnReconnect = it }
        toggle("Kill switch", "If the VPN drops, stop playing until it's back (nothing goes out without it)", { Store.vpnKillSwitch }) { Store.vpnKillSwitch = it }
        toggle("Switch server if slow", "Offer a quicker server when streams keep buffering", { Store.vpnSwitchSlow }) { Store.vpnSwitchSlow = it }
        row("More VPN settings", { "Provider, set up from your phone, keys, config files, connect when Dingus starts" }, { "›" }) { vpn.openSetup() }
        row("Only Dingus uses this VPN", { "Other apps on your Fire TV aren't affected." }) {}
        if (vpn.locations.isEmpty()) scope.launch { vpn.loadLocations() }
        if (!VpnControls.ipCurrent) scope.launch { VpnControls.lookUpIp(); update() }
        Ui.test("vpn-section")
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        vpn.onActivityResult(requestCode, resultCode)
    }

    override fun onPause() { Vpn.removeListener(vpnListener); super.onPause() }

    private fun aboutSection() {
        row("Clear cache", { "Pictures, logos and posters saved to load faster. They come back as you browse." }, { cacheSize?.let { mb(it) } ?: "…" }) { clearCache() }
        if (cacheSize == null) scope.launch { cacheSize = withContext(Dispatchers.IO) { cacheBytes() }; update() }
        updateRow = row("Check for updates", { Updater.available?.takeIf { Updater.newer(it.version, BuildConfig.VERSION_NAME) }?.let { "Dingus ${it.version} is ready  ·  OK to update" } ?: "" }, { "Version ${BuildConfig.VERSION_NAME}" }) {
            updateRow?.let { sub(it, "Checking…") }
            Updater.check(this, manual = true) { msg -> updateRow?.let { sub(it, msg) } }
        }
        row("About Dingus", { "Film and show logos from TMDB (not endorsed or certified by TMDB)" }, { "Version ${BuildConfig.VERSION_NAME}" }) {}
    }

    override fun onResume() {
        super.onResume(); update(); styleVpnRow(); Vpn.onChanged(vpnListener); Ui.test("settings-shown")
        // Expiry, connections in use and whether the server's online, fresh each time (at most every 20 seconds).
        val p = Store.playlist
        if (p != null && System.currentTimeMillis() - Account.lastCheck > 20_000L)
            scope.launch { Account.check(p); update() }
    }
    override fun onDestroy() { scope.cancel(); super.onDestroy() }

    private fun update() {
        for ((v, f) in subtitles) sub(v, f())
        for ((v, f) in values) if (v.parent != null) v.findViewById<TextView>(R.id.value).apply {
            text = f()
            // A value sits in a small chip; "›" (opens a screen) and nothing at all don't.
            val plain = text == "›" || text.isEmpty()
            if (plain) { background = null; setPadding(0, 0, 0, 0); textSize = 18f }
            visibility = if (text.isEmpty()) View.GONE else View.VISIBLE
        }
        findViewById<TextView>(R.id.settingsSub)?.text = listOfNotNull("Dingus ${BuildConfig.VERSION_NAME}", Store.playlist?.name).joinToString("  ·  ")
        for ((v, f) in switches) if (v.parent != null) v.findViewById<android.widget.ImageView>(R.id.switchIcon).setImageResource(if (f()) R.drawable.switch_on else R.drawable.switch_off)
    }

    // ------------------------------------------------------------------ rows

    /** Works the channel list out again with a changed setting – off the main thread (it can be big). */
    private fun reapplySoon() { Background.scope.launch(Dispatchers.Default) { Repo.reapply() } }

    /** A small heading inside a section ("THE GUIDE", "REMINDERS"), so a long list is easy to scan. */
    private fun subhead(text: String) {
        val v = LayoutInflater.from(this).inflate(R.layout.item_heading, list, false) as TextView
        v.text = text.uppercase()
        v.textSize = 12f
        v.setPadding(v.paddingLeft, (14 * resources.displayMetrics.density).toInt(), v.paddingRight, v.paddingBottom)
        list.addView(v)
    }

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

    private fun reminderLeadName(secs: Int) = when (secs) { 30 -> "30 seconds"; 60 -> "1 minute"; else -> "${secs / 60} minutes" }

    private fun choose(title: String, options: List<String>, current: Int, done: (Int) -> Unit) {
        Dialogs.builder(this).setTitle(title)
            .setSingleChoiceItems(options.toTypedArray(), current) { d, i -> d.dismiss(); done(i) }
            .show()
    }

    // ------------------------------------------------------------------ playlists

    private fun typeName(p: Playlist) = if (p.type == "xtream") "Xtream Codes login" else "M3U link"

    private fun playlistsSummary(): String {
        val all = Store.playlists
        val p = Store.playlist ?: return "Add your playlist"
        return "${p.name}  ·  ${typeName(p)}" + (if (all.size > 1) "  ·  ${all.size} playlists" else "") +
            (Failover.status(p)?.let { "\n$it" } ?: "") +
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

    // ------------------------------------------------------------------ backup servers

    private fun backupSummary(): String {
        val p = Store.playlist ?: return "Add a playlist first"
        val n = p.backupUrls.size
        return if (n == 0) "None  ·  add another address to fall back on if the main server is down"
        else "$n set  ·  tried in order if the main server is down" + (Failover.status(p)?.let { "\n$it" } ?: "")
    }

    private fun saveBackups(p: Playlist, list: List<String>) {
        Repo.settingsChanged()
        Store.playlist = p.copy(backups = list.map { it.trim() }.filter { it.isNotEmpty() }.distinct().joinToString("\n"))
        update()
    }

    private fun backupServers() {
        val p = Store.playlist ?: return
        val urls = p.backupUrls
        val items = urls.mapIndexed { i, u -> "Backup ${i + 1}:  $u" } + "+  Add a backup server"
        val hint = if (p.type == "xtream") "http://server:port" else "http://…/playlist.m3u"
        Dialogs.builder(this).setTitle("Backup servers for “${p.name}” (tried in this order)")
            .setItems(items.toTypedArray()) { _, i ->
                if (i < urls.size) backupOptions(p, i)
                else Dialogs.input(this, "Add a backup server", "", hint) { u -> if (u.isNotEmpty()) saveBackups(p, urls + u) }
            }
            .show()
    }

    private fun backupOptions(p: Playlist, i: Int) {
        val urls = p.backupUrls.toMutableList()
        val opts = listOfNotNull(if (i > 0) "Move up (try sooner)" else null, if (i < urls.size - 1) "Move down" else null, "Edit", "Remove")
        Dialogs.builder(this).setTitle(urls[i])
            .setItems(opts.toTypedArray()) { _, k ->
                when (opts[k]) {
                    "Move up (try sooner)" -> { urls.add(i - 1, urls.removeAt(i)); saveBackups(p, urls) }
                    "Move down" -> { urls.add(i + 1, urls.removeAt(i)); saveBackups(p, urls) }
                    "Edit" -> Dialogs.input(this, "Backup server", urls[i], "http://server:port") { u ->
                        if (u.isEmpty()) urls.removeAt(i) else urls[i] = u
                        saveBackups(p, urls)
                    }
                    "Remove" -> Dialogs.confirm(this, "Remove this backup server?", urls[i], "Remove") { urls.removeAt(i); saveBackups(p, urls) }
                }
            }
            .show()
    }

    private fun savePlaylist(p: Playlist) {
        Repo.settingsChanged()
        Store.playlist = p
        Store.guideLoadedAt = 0
        update()
        refreshGuideOnly()
    }

    private fun rawGuides(p: Playlist) = p.epgUrl.split('\n').map { it.trim() }.filter { it.isNotEmpty() }.toMutableList()

    private fun guideSources() {
        val p = Store.playlist ?: return
        val urls = p.epgUrls
        val items = urls.mapIndexed { i, u -> "${i + 1}.  $u" } +
            ("Provider's guide: " + if (p.useProviderGuide) "On" else "Off") + "+  Add a guide address"
        Dialogs.builder(this).setTitle("TV guide sources  ·  order them in Source priority")
            .setItems(items.toTypedArray()) { _, i ->
                when {
                    i < urls.size -> guideSourceOptions(p, urls[i])
                    i == urls.size -> savePlaylist(p.copy(useProviderGuide = !p.useProviderGuide))
                    else -> Dialogs.input(this, "Add a TV guide address", "", "http://…/guide.xml") { u ->
                        if (u.isNotEmpty()) { val raw = rawGuides(p); val at = raw.indexOf(Playlist.PROVIDER_GUIDE).let { if (it < 0) raw.size else it }; raw.add(at, u); savePlaylist(p.copy(epgUrl = raw.joinToString("\n"))) }
                    }
                }
            }
            .show()
    }

    private fun guideSourceOptions(p: Playlist, url: String) {
        val raw = rawGuides(p)
        val i = raw.indexOf(url).takeIf { it >= 0 } ?: return
        val opts = listOf("Edit", "Remove")
        Dialogs.builder(this).setTitle(url)
            .setItems(opts.toTypedArray()) { _, k ->
                when (opts[k]) {
                    "Edit" -> Dialogs.input(this, "TV guide address", url, "http://…/guide.xml") { u ->
                        if (u.isEmpty()) raw.removeAt(i) else raw[i] = u
                        savePlaylist(p.copy(epgUrl = raw.joinToString("\n")))
                    }
                    "Remove" -> Dialogs.confirm(this, "Remove this TV guide source?", url, "Remove") { raw.removeAt(i); savePlaylist(p.copy(epgUrl = raw.joinToString("\n"))) }
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
                Vod.refreshInBackground() // films and shows download alongside, not afterwards
                Repo.refreshChannels(p)
                sub(row, "Updating the TV guide…")
                Repo.refreshGuide()
                refreshing = false
                Notice.updateDone("Playlist & TV guide updated")
                update()
            } catch (e: Exception) {
                refreshing = false
                Notice.updateDone("Couldn't update: ${Net.friendly(e)}", 6_000)
                sub(row, Net.friendly(e))
            }
        }
    }

    private fun refreshGuideOnly(force: Boolean = false) {
        if (refreshing || Repo.channels.isEmpty()) return
        refreshing = true
        if (force) Store.guideLoadedAt = 0
        scope.launch {
            val row = guideRow ?: refreshRow
            try {
                row?.let { sub(it, "Updating the TV guide…") }
                if (force) Notice.working("Updating the TV guide…")
                Repo.refreshGuide()
                refreshing = false
                if (force) { Notice.done("TV guide updated"); Ui.test("guide-updated") }
                update()
            } catch (e: Exception) {
                refreshing = false
                if (force) Notice.done("Couldn't update the TV guide: ${Net.friendly(e)}", 6_000)
                row?.let { sub(it, "TV guide: ${Net.friendly(e)}") }
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

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean {
        if (backHold(event)) return true
        if (side.onKey(event)) return true
        // RIGHT from a section: its first option (not whichever one is level with the section).
        if (event.action == android.view.KeyEvent.ACTION_DOWN && event.keyCode == android.view.KeyEvent.KEYCODE_DPAD_RIGHT &&
            sectionViews.any { it === currentFocus }) {
            findViewById<View>(R.id.pane).scrollTo(0, 0)
            val first = list.getChildAt(firstRow())
            first?.requestFocus()
            Ui.test("settings-into ${sections[section].first}: ${first?.findViewById<TextView>(R.id.title)?.text}")
            return true
        }
        return super.dispatchKeyEvent(event)
    }
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
