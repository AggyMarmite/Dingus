package app.dingus.tv

import android.content.Context
import android.util.Log
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.io.IOException
import java.io.FileInputStream
import java.io.FileOutputStream

/**
 * The channels, categories and TV guide. Starts straight from what was saved last time (so the app opens instantly),
 * then refreshes from the provider in the background.
 */
object Repo {
    const val FAV = "\u0001fav"
    const val RECENT = "\u0001recent"
    const val ALL = "\u0001all"

    @Volatile var categories: List<Category> = emptyList(); private set
    /** The playlist's own categories (before matching ones are merged): what's saved, and what a settings change starts from. */
    @Volatile private var rawCategories: List<Category> = emptyList()
    /** Every channel, with your edits (new names/numbers) applied – hidden ones included. */
    @Volatile var channels: List<Channel> = emptyList(); private set
    /** The channels exactly as the playlist has them (what's saved). */
    @Volatile private var raw: List<Channel> = emptyList()
    @Volatile private var hiddenIds: Set<String> = emptySet()
    @Volatile private var renumbered = false
    private val listCache = HashMap<String, List<Channel>>()
    @Volatile private var byId: Map<String, Channel> = emptyMap()
    @Volatile private var byCategory: Map<String, List<Channel>> = emptyMap()
    @Volatile private var guide: Map<String, Array<Programme>> = emptyMap()
    @Volatile var guideUrl: String? = null; private set
    val hasGuide: Boolean get() = guide.isNotEmpty()

    private val listeners = mutableListOf<() -> Unit>()
    fun onChanged(l: () -> Unit) { synchronized(listeners) { listeners.add(l) } }
    fun removeListener(l: () -> Unit) { synchronized(listeners) { listeners.remove(l) } }
    /** Goes up every time the channels or the guide change (for things worked out from them, like search). */
    @Volatile var version = 0; private set
    private fun changed() { version++; synchronized(listeners) { listeners.toList() }.forEach { it() } }

    private lateinit var base: File
    /** Each playlist keeps its own saved channels and guide. */
    private val dir: File get() = File(base, "p_${Store.activeId}").apply { mkdirs() }
    fun init(ctx: Context) { base = ctx.filesDir }
    /** This playlist has been loaded before (its channels are saved on the device). */
    fun hasSaved(): Boolean = File(dir, "channels.bin").exists()

    /** After switching playlist: forget what's in memory (its saved copies stay, so switching back is instant). */
    fun reset() {
        set(emptyList(), emptyList(), null)
        guide = emptyMap()
        Store.switched()
        changed()
    }

    fun channel(id: String?) = id?.let { byId[it] }

    /** The categories down the side: Favourites and Recently watched first, then the playlist's. */
    fun sideCategories(): List<Category> {
        // (No "All channels": not needed – the categories are the way in. Kept only when there are no categories.)
        val out = ArrayList<Category>()
        if (Store.favourites.any { byId.containsKey(it) }) out.add(Category(FAV, "Favourites"))
        if (Store.recent.any { byId.containsKey(it) }) out.add(Category(RECENT, "Recently watched"))
        out.addAll(Store.ordered("live", categories.filter { byCategory[it.id]?.isNotEmpty() == true && !Store.isHidden("live:${it.id}") }) { it.id })
        if (out.none { it.id != FAV && it.id != RECENT }) out.add(0, Category(ALL, "All channels"))
        return out
    }

    /** A category's channels as the guide shows them: hidden ones left out, your numbers, duplicates merged (if on). */
    fun channelsIn(categoryId: String): List<Channel> {
        if (categoryId == FAV || categoryId == RECENT) {
            val ids = if (categoryId == FAV) Store.favourites else Store.recent
            return ids.mapNotNull { byId[it] }.filter { it.id !in hiddenIds }
        }
        synchronized(listCache) { listCache[categoryId]?.let { return it } }
        // (All channels leaves out the categories you've hidden.)
        var list = if (categoryId == ALL) hiddenCategoryIds(rawCategories).let { h -> if (h.isEmpty()) channels else channels.filter { it.categoryId !in h } }
            else byCategory[categoryId] ?: emptyList()
        if (hiddenIds.isNotEmpty()) list = list.filter { it.id !in hiddenIds }
        if (renumbered) list = list.sortedBy { it.number }
        if (Store.mergeChannels) list = Merge.fold(list)
        synchronized(listCache) { listCache[categoryId] = list }
        return list
    }

    /** All versions of a channel (best first) when duplicates are merged; just the channel otherwise. */
    fun versions(c: Channel): List<Channel> {
        if (!Store.mergeChannels) return listOf(c)
        val k = Merge.key(c).ifEmpty { return listOf(c) }
        val all = Merge.order(channels.filter { it.id !in hiddenIds && Merge.key(it) == k }).ifEmpty { return listOf(c) } // (a hidden channel still plays itself)
        // The version you chose (≡ → Choose version) first; the rest stay in order for when one won't play.
        val pick = Store.preferredVersion(k)?.let { id -> all.firstOrNull { it.id == id } } ?: return all
        return listOf(pick) + all.filter { it !== pick }
    }

    // ------------------------------------------------------------------ sources: the same channel in other categories

    private val sourcePrefixRx = Regex("""^\s*[^|:\s]{1,6}\s*[|:]\s*""")
    private val sourceQualityRx = Regex("""\b(uhd|fhd|hd|sd|4k|hevc|h265|1080p|720p|50fps|backup|raw)\b""")
    private val sourceSmallRx = Regex("[\u1D00-\u1DBF\u02B0-\u02FF\u2070-\u209F]+")
    private val sourceKeys = java.util.concurrent.ConcurrentHashMap<String, String>()

    /** "UK| Really", "D+| Really HD" → "really": the name without its label in front or quality tags. */
    fun sourceKey(name: String): String = sourceKeys.getOrPut(name) {
        var x = name.lowercase().replace(sourcePrefixRx, "")
        x = x.replace(sourceQualityRx, " ").replace(sourceSmallRx, "")
        x.filter { it.isLetterOrDigit() || it == '+' }
    }

    /** Every channel with the same name in any category (this one included, first). */
    fun sources(c: Channel): List<Channel> {
        val k = sourceKey(c.name)
        if (k.length < 2) return listOf(c)
        val h = hiddenCategoryIds(rawCategories)
        return listOf(c) + channels.filter { it.id != c.id && it.id !in hiddenIds && it.categoryId !in h && sourceKey(it.name) == k }
    }

    /**
     * Sources showing exactly the same thing (the same guide listing): safe to switch to by themselves. Any channel
     * with exactly the same guide id counts, whatever it's called ("UK| DISCOVERY TURBO" and "D+| DISCOVERY TURBO"),
     * including ones in hidden categories.
     */
    fun sameAs(c: Channel): List<Channel> {
        val epg = c.epgId?.takeIf { it.isNotBlank() }
        val gk = c.guideKey
        if (epg == null && gk == null) return emptyList()
        val h = hiddenCategoryIds(rawCategories)
        val sameName = sources(c).drop(1).filter { (epg != null && it.epgId == epg) || (gk != null && it.guideKey == gk) }
        val sameGuide = if (epg == null) emptyList() else channels.filter { it.id != c.id && it.id !in hiddenIds && it.categoryId !in h && it.epgId == epg }
        val stand = if (epg == null) emptyList() else synchronized(backupsLock) { if (backupsFor == Store.activeId) backups[epg] else null } ?: emptyList()
        return (sameName + sameGuide + stand).filter { it.id != c.id }.distinctBy { it.id }
    }

    // ------------------------------------------------------------------ stand-ins from hidden categories

    private val backupsLock = Any()
    private var backups: Map<String, List<Channel>> = emptyMap()
    private var backupsRead = false
    /** Which playlist the stand-ins belong to. */
    private var backupsFor = ""

    private fun setBackups(list: List<Channel>) {
        synchronized(backupsLock) { backups = list.groupBy { it.epgId!! }; backupsRead = true; backupsFor = Store.activeId }
        try {
            val a = org.json.JSONArray()
            for (c in list) a.put(org.json.JSONObject().put("i", c.id).put("n", c.name).put("c", c.categoryId).put("e", c.epgId).put("u", c.url).put("a", c.archiveDays).put("l", c.playlistLogo ?: ""))
            File(dir, "standins.json").writeText(a.toString())
        } catch (e: Exception) { Log.w("Dingus", "save stand-ins", e) }
    }

    /** The saved stand-ins, read once (after a restart, before the next playlist update). */
    fun loadBackups() {
        if (synchronized(backupsLock) { backupsRead && backupsFor == Store.activeId }) return
        try {
            val f = File(dir, "standins.json")
            val list = if (!f.exists()) emptyList() else org.json.JSONArray(f.readText()).let { a -> (0 until a.length()).map { i -> a.getJSONObject(i) }.map { o ->
                Channel(o.getString("i"), o.optString("n"), 0, o.optString("c"), o.optString("l").ifEmpty { null }, o.optString("e"), o.optString("u"), o.optInt("a"))
            } }
            synchronized(backupsLock) { backups = list.filter { !it.epgId.isNullOrBlank() }.groupBy { it.epgId!! }; backupsRead = true; backupsFor = Store.activeId }
        } catch (e: Exception) { synchronized(backupsLock) { backups = emptyMap(); backupsRead = true; backupsFor = Store.activeId } }
    }

    fun categoryName(id: String) = categories.firstOrNull { it.id == id }?.name ?: ""

    /** How many sources "Choose source…" would list for a channel (its versions and copies elsewhere). */
    fun sourceCount(c: Channel): Int = (versions(c) + sources(c)).distinctBy { it.id }.size

    /**
     * ≡ / hold OK → Choose source…: the channel's versions (FHD, HEVC… best first, with what they've played at) and the
     * same channel in other categories (with what's on). A version is remembered as the one to use; OK plays it.
     */
    /**
     * Choose source, as a page of the mini menu: the channel's versions (best first, a quality tag on each, the one
     * playing marked) and the same channel in other categories (with what's on, so you can see they match).
     */
    fun sourcesPage(c: Channel, playing: Channel?, pickVersion: (Channel) -> Unit, pickOther: (Channel) -> Unit): MiniMenu.Page {
        val vs = versions(c)
        val others = sources(c).filter { s -> vs.none { it.id == s.id } }
        val items = ArrayList<MiniMenu.Item>()
        if (others.isNotEmpty()) items.add(MiniMenu.Item("VERSIONS", heading = true))
        vs.forEach { v ->
            items.add(MiniMenu.Item(v.name, chips = MiniMenu.qualityChips(v), playing = v.id == playing?.id, action = {
                Ui.test("source-chosen ${v.name}"); Store.setPreferredVersion(Merge.key(c), v.id); pickVersion(v)
            }))
        }
        if (others.isNotEmpty()) {
            items.add(MiniMenu.Item("ALSO IN", heading = true))
            others.forEach { o ->
                val on = nowNext(o).first?.title
                items.add(MiniMenu.Item(o.name, sub = listOfNotNull(categoryName(o.categoryId).ifEmpty { null }, on).joinToString("  ·  ").ifEmpty { null },
                    chips = MiniMenu.qualityChips(o).take(1), playing = o.id == playing?.id, action = { Ui.test("source-chosen ${o.name}"); pickOther(o) }))
            }
        }
        Ui.test("source-list ${vs.size + others.size}")
        return MiniMenu.Page("Sources", c.name.replace(sourcePrefixRx, ""), items, "OK plays it  ·  ◀ back")
    }

    /** Choose source on its own (no menu around it). */
    fun chooseSource(a: android.app.Activity, c: Channel, playing: Channel?, anchor: android.graphics.Rect?, pickVersion: (Channel) -> Unit, pickOther: (Channel) -> Unit) {
        if (sourceCount(c) < 2) return
        val pg = sourcesPage(c, playing, pickVersion, pickOther)
        MiniMenu(a).show(MiniMenu.Page(pg.title, pg.subtitle, pg.items, "OK plays it  ·  ◀ closes"), anchor)
    }


    /**
     * The playlist's categories you've hidden (Live TV), by their own ids – when categories with the same name are
     * merged, hiding the merged one hides each of them.
     */
    fun hiddenCategoryIds(cats: List<Category>): Set<String> {
        if (cats.isEmpty()) return emptySet()
        if (!Store.mergeCategories) return cats.mapNotNullTo(HashSet()) { c -> c.id.takeIf { Store.isHidden("live:$it") } }
        val out = HashSet<String>()
        cats.groupBy { categoryKey(it.name).ifEmpty { "\u0000" + it.id } }.values.forEach { g -> if (Store.isHidden("live:${g[0].id}")) g.forEach { out.add(it.id) } }
        return out
    }

    /** A category was hidden: All channels (and the other lists) drop it straight away. */
    fun hiddenChanged() { synchronized(listCache) { listCache.clear() }; changed() }

    /** Every channel including hidden ones, for the channel editor. */
    fun allForEditing(): List<Channel> = channels
    fun isHiddenChannel(id: String) = id in hiddenIds

    /** Settings that change what the lists hold (merging, channel edits) call this. */
    fun reapply() {
        val keys = channels.associate { it.id to it.guideKey }
        raw.forEach { r -> keys[r.id]?.let { r.guideKey = it } }
        set(rawCategories, raw, guideUrl)
        changed()
    }

    /** Every programme known for a channel, in time order. */
    fun programmes(c: Channel): Array<Programme> = c.guideKey?.let { guide[it] } ?: NONE
    private val NONE = emptyArray<Programme>()

    /** What's on now and next on a channel (either can be null). */
    fun nowNext(c: Channel, at: Long = System.currentTimeMillis()): Pair<Programme?, Programme?> {
        val list = c.guideKey?.let { guide[it] } ?: return null to null
        var lo = 0; var hi = list.size - 1; var idx = -1
        while (lo <= hi) { // the last programme starting at or before now
            val mid = (lo + hi) ushr 1
            if (list[mid].start <= at) { idx = mid; lo = mid + 1 } else hi = mid - 1
        }
        val now = if (idx >= 0 && list[idx].stop > at) list[idx] else null
        val next = list.getOrNull(idx + 1)
        return now to next
    }

    // ------------------------------------------------------------------ loading

    private fun set(cats: List<Category>, chans: List<Channel>, url: String?) {
        // Divider "channels" ("##### UK #####") left out (Settings → Live TV → Remove divider channels).
        val edited = ChannelEdits.apply(chans, includeHidden = true).let { l -> if (Store.removeDividers) l.filterNot { isDivider(it.name) } else l }
        val edits = ChannelEdits.all()
        raw = chans
        rawCategories = cats
        channels = edited
        hiddenIds = edits.filterValues { it.hidden }.keys
        renumbered = edits.values.any { it.number != null }
        byId = edited.associateBy { it.id }
        val grouped = edited.groupBy { it.categoryId }
        if (Store.mergeCategories) {
            val (c, b) = mergeCategories(cats, grouped)
            categories = c; byCategory = b
        } else { categories = cats; byCategory = grouped }
        guideUrl = url
        synchronized(listCache) { listCache.clear() }
        Merge.clear()
        if (Store.mergeChannels) edited.forEach { Merge.key(it) } // worked out now (in the background when loading)
    }

    // ------------------------------------------------------------------ dividers and matching categories

    private val dividerRx = Regex("""#{3,}|={3,}|\*{3,}|~{3,}|▬{2,}|━{2,}|—{3,}|-{5,}|_{4,}|•{3,}|★{3,}""")
    /** "##### UK #####", "===== SPORT =====" or a name with no letters or numbers at all: a divider, not a channel. */
    fun isDivider(name: String) = dividerRx.containsMatchIn(name) || name.none { it.isLetterOrDigit() }

    private val catQualityRx = Regex("""\b(?:hevc|h\.?265|x265|fhd|uhd|4k|hd|sd|50\s*fps|60\s*fps|raw|backup|b/u|vip|plus|\+)\b""", RegexOption.IGNORE_CASE)
    /** "UK| GENERAL HEVC" → "uk|general": categories that differ only by quality are the same category. */
    fun categoryKey(name: String) = catQualityRx.replace(name, " ").lowercase().replace(Regex("""[\s\-–:|/]+"""), " ").trim()

    /**
     * "UK| GENERAL" and "UK| GENERAL HEVC" become one category (the first one's name and place), keeping its own
     * order, each channel from the other with the channel it matches ("Comedy Central FHD" and "Comedy Central HEVC
     * FHD" side by side); ones that match nothing go at the end.
     */
    private fun mergeCategories(cats: List<Category>, grouped: Map<String, List<Channel>>): Pair<List<Category>, Map<String, List<Channel>>> {
        val groups = LinkedHashMap<String, MutableList<Category>>()
        for (c in cats) groups.getOrPut(categoryKey(c.name).ifEmpty { "\u0000" + c.id }) { ArrayList(1) }.add(c)
        if (groups.size == cats.size) return cats to grouped
        val out = HashMap<String, List<Channel>>(grouped)
        val keptCats = ArrayList<Category>()
        for (g in groups.values) {
            val first = g[0]
            keptCats.add(first)
            if (g.size == 1) continue
            // Each channel joins the first one with the same name, wherever that is, so every version sits together
            // ("Comedy Central FHD" too, even when the first category has it apart from "Comedy Central"). The first
            // category's order decides where each group goes; ones from the others that match nothing go at the end.
            val byKey = LinkedHashMap<String, MutableList<Channel>>()
            var solo = 0
            for (cat in g) {
                if (cat !== first) out.remove(cat.id)
                for (ch in grouped[cat.id] ?: continue) byKey.getOrPut(Merge.key(ch).ifEmpty { "\u0000${solo++}" }) { ArrayList(2) }.add(ch)
            }
            val list = byKey.values.flatten()
            out[first.id] = list
        }
        return keptCats to out
    }

    /** What was saved last time (instant start). False when there's nothing yet. */
    suspend fun loadSaved(): Boolean = withContext(Dispatchers.IO) {
        val ok = try { readChannels() } catch (e: Exception) { Log.w("Dingus", "saved channels", e); false }
        if (ok) {
            try { readGuide() } catch (e: Exception) { Log.w("Dingus", "saved guide", e) }
        }
        ok
    }

    /** Just the saved channels (quick), so a screen can show something before the saved guide's read. */
    suspend fun loadSavedChannels(): Boolean = withContext(Dispatchers.IO) {
        (try { readChannels() } catch (e: Exception) { Log.w("Dingus", "saved channels", e); false }).also { if (it) channelsIn(ALL); loadBackups() }
    }

    /** Then the saved guide (can take a moment for a big one). */
    suspend fun loadSavedGuide() = withContext(Dispatchers.IO) {
        try { readGuide(); readGuideLogos(); changed() } catch (e: Exception) { Log.w("Dingus", "saved guide", e) }
    }

    /** The guide's channel logos (channel id → address), for Channel logo priority. */
    @Volatile private var guideLogos: Map<String, String> = emptyMap()

    private fun readGuideLogos() {
        val o = try { org.json.JSONObject(File(dir, "guide-logos.json").readText()) } catch (e: Exception) { return }
        val m = HashMap<String, String>(); o.keys().forEach { m[it] = o.getString(it) }
        guideLogos = m
        for (c in channels) c.guideLogo = m[c.id]
        for (c in raw) c.guideLogo = m[c.id]
    }

    /** Settings → TV guide → Clear TV guide: the saved listings go (they come back with the next update). */
    fun clearGuide() {
        guide = emptyMap(); guideLogos = emptyMap()
        for (c in channels) { c.guideKey = null; c.guideLogo = null }
        for (c in raw) { c.guideKey = null; c.guideLogo = null }
        File(dir, "guide.bin").delete(); File(dir, "guide-logos.json").delete()
        Store.guideLoadedAt = 0
        guideDone = 0L to ""
        changed()
    }

    /** Fetches the playlist from the provider (throws with a message to show if it can't). */
    // One update at a time: two screens asking at once share it (the second waits, then finds it just done).
    private val channelsLock = kotlinx.coroutines.sync.Mutex()
    private val guideLock = kotlinx.coroutines.sync.Mutex()
    @Volatile private var channelsDone = 0L to ""
    @Volatile private var guideDone = 0L to ""
    /** Bumped when a setting changes what an update fetches (guide sources, backup servers): an update already
     *  running started with the old ones, so the next request runs again instead of sharing it. */
    @Volatile private var settingsVersion = 0
    @Volatile private var channelsVersion = -1
    @Volatile private var guideVersion = -1
    fun settingsChanged() { settingsVersion++ }

    /** The last playlist update failed (and nothing's updated it since): tried again once the VPN connects. */
    @Volatile var updateFailed = false

    suspend fun refreshChannels(p: Playlist, already: Sources.Result? = null) = withContext(Dispatchers.IO) {
        val asked = System.currentTimeMillis()
        channelsLock.withLock {
            if (already == null && channelsDone.first >= asked && channelsDone.second == p.id && channelsVersion == settingsVersion) return@withLock
            val v = settingsVersion
            try { refreshChannelsNow(p, already); updateFailed = false } catch (e: Exception) { updateFailed = true; throw e }
            channelsDone = System.currentTimeMillis() to p.id; channelsVersion = v
            Store.channelsLoadedAt = System.currentTimeMillis()
        }
    }

    private fun refreshChannelsNow(p: Playlist, already: Sources.Result?) {
        val t0 = System.currentTimeMillis()
        val r = already ?: Progress.step(Progress.LIVE) { Sources.load(p) }
        val t1 = System.currentTimeMillis()
        Sources.accountInfo?.let { Account.remember(it); Sources.accountInfo = null }
        // Hidden categories' channels aren't kept at all (no saving, matching or guide listings for them): showing a
        // category again updates the playlist to bring them back.
        val hid = hiddenCategoryIds(r.categories)
        val kept = if (hid.isEmpty()) r.channels else r.channels.filter { it.categoryId !in hid }
        // …except as stand-ins: one with exactly the same guide id as a kept channel can still take over when that
        // one keeps buffering (it's the same channel, filed under a category you've hidden).
        val wanted = kept.mapNotNullTo(HashSet()) { it.epgId?.takeIf { e -> e.isNotBlank() } }
        setBackups(r.channels.filter { it.categoryId in hid && it.epgId?.let { e -> e in wanted } == true })
        // Keep guide matches from before, so now/next shows straight away until the guide's read again.
        val oldKeys = channels.associate { it.id to it.guideKey }
        kept.forEach { it.guideKey = oldKeys[it.id]; it.guideLogo = guideLogos[it.id] }
        set(r.categories, kept, r.guideUrl)
        channelsIn(ALL) // (worked out here, in the background, not when the guide first draws)
        try { Progress.saving { writeChannels() } } catch (e: Exception) { Log.w("Dingus", "save channels", e) }
        Log.i("Dingus", "Update: ${r.channels.size} channels, loaded in ${t1 - t0} ms, saved in ${System.currentTimeMillis() - t1} ms")
        changed()
    }

    /** Reads the TV guide (in the background; can take a while for big guides). */
    /**
     * The TV guide keeps itself topped up: the saved listings only reach so far ahead, so once they're over
     * [GUIDE_FRESH_HOURS] old it updates by itself in the background (no need to update the guide by hand).
     */
    const val GUIDE_FRESH_HOURS = 6
    @Volatile private var keeping = false

    fun keepGuideFresh() {
        if (keeping || channels.isEmpty() || Store.playlist == null) return
        if (System.currentTimeMillis() - Store.guideLoadedAt < GUIDE_FRESH_HOURS * 3600_000L) return
        keeping = true
        Background.scope.launch {
            try { refreshGuide(); Ui.test("guide-kept-fresh $guideChannels") } catch (e: Exception) { Log.w("Dingus", "guide top-up", e) }
            finally { keeping = false }
        }
    }

    suspend fun refreshGuide() = withContext(Dispatchers.IO) {
        val asked = System.currentTimeMillis()
        val id = Store.activeId
        guideLock.withLock {
            if (guideDone.first >= asked && guideDone.second == id && guideVersion == settingsVersion) return@withLock
            val v = settingsVersion
            refreshGuideNow()
            guideDone = System.currentTimeMillis() to id; guideVersion = v
        }
    }

    /**
     * A guide's stream. The provider's own guide is on the playlist's server, so it follows a switch to a backup
     * server, and if the server it's on doesn't answer (or says "not found") the others are tried in turn.
     */
    private fun openGuide(url: String, provider: Boolean, p: Playlist?): java.io.InputStream {
        if (!provider || p == null || p.type != "xtream" || p.backupUrls.isEmpty()) return Net.open(url, 30_000)
        val now = Failover.rewrite(url, p)
        val base = Failover.base(p)
        val tries = listOf(now) + Failover.addresses(p).map { Sources.xtreamBase(it) }.filter { it != base && now.startsWith("$base/", true) }
            .map { it + now.substring(base.length) }
        var first: Exception? = null
        for (u in tries.distinct()) {
            try { return Net.open(u, 30_000).also { if (u != now) Ui.test("guide-from-backup") } }
            catch (e: Exception) { if (!Failover.isDown(e)) throw e; if (first == null) first = e; Log.w("Dingus", "guide on ${try { java.net.URI(u).host } catch (_: Exception) { "?" }}", e) }
        }
        throw first ?: IOException("No TV guide")
    }

    private fun refreshGuideNow() {
        val p = Store.playlist
        // The guides in your order (Settings → TV guide → Source priority; yours first, then the provider's, unless
        // you've moved it): each fills in channels the earlier ones missed.
        val order = p?.guideOrder ?: listOf(Playlist.PROVIDER_GUIDE)
        val sources = order.mapNotNull { if (it == Playlist.PROVIDER_GUIDE) guideUrl else it }
        val providerAt = order.indexOf(Playlist.PROVIDER_GUIDE).takeIf { guideUrl != null } ?: -1
        if (sources.isEmpty()) return
        val chans = channels
        val keys = HashMap<String, String>()
        val programmes = HashMap<String, Array<Programme>>()
        val logos = HashMap<String, String>()
        var lastError: Exception? = null
        var anyWorked = false
        val failed = ArrayList<String>()
        Progress.begin(Progress.GUIDE)
        try { for ((n, url) in sources.withIndex()) {
            val todo = chans.filter { !keys.containsKey(it.id) }
            if (todo.isEmpty()) break
            try {
                val t0 = System.currentTimeMillis()
                val parsed = openGuide(url, n == providerAt, p).use { Guide.parse(it, todo, hoursAhead = Store.guideHours, hoursBack = backHours) }
                Log.i("Dingus", "Update: guide $n read and matched in ${System.currentTimeMillis() - t0} ms (${parsed.keysByChannelId.size} channels)")
                anyWorked = true
                // Keys from different guides could clash, so each guide's are kept apart.
                for ((k, v) in parsed.programmes) programmes["$n|$k"] = v
                // A channel only counts as done when this guide actually has listings for it from now on – one it
                // names but with nothing current (out of date, or empty) is left for the next guide to fill in.
                val now = System.currentTimeMillis()
                for ((id, k) in parsed.keysByChannelId) if (parsed.programmes[k]?.any { it.stop > now } == true) keys[id] = "$n|$k"
                for ((id, l) in parsed.logosByChannelId) logos.putIfAbsent(id, l)
            } catch (e: Exception) {
                Log.w("Dingus", "guide $n", e)
                val where = try { java.net.URI(url).host ?: url } catch (_: Exception) { url }
                lastError = IOException("$where: ${Net.friendly(e)}", e)
                failed.add("$where (${Net.friendly(e)})")
            }
        } } finally { Progress.end(Progress.GUIDE) }
        if (!anyWorked) throw lastError ?: IOException("No TV guide")
        // Some guides worked and one didn't: say which (after the "updated" note), so a broken link gets noticed.
        if (failed.isNotEmpty()) android.os.Handler(android.os.Looper.getMainLooper()).postDelayed({
            Notice.done("TV guide: couldn't read ${failed.joinToString(", ")}  ·  the others updated", 7_000)
        }, 4_500)
        for (c in chans) { c.guideKey = keys[c.id]; c.guideLogo = logos[c.id] }
        for (c in raw) { c.guideKey = keys[c.id]; c.guideLogo = logos[c.id] }
        guide = programmes
        guideLogos = logos
        try { File(dir, "guide-logos.json").writeText(org.json.JSONObject(logos as Map<*, *>).toString()) } catch (e: Exception) { Log.w("Dingus", "save guide logos", e) }
        Store.guideLoadedAt = System.currentTimeMillis()
        val t2 = System.currentTimeMillis()
        try { Progress.saving { writeGuide() } } catch (e: Exception) { Log.w("Dingus", "save guide", e) }
        // Which listing each channel uses is kept with the channels: save them again too, or after a restart the
        // channels come back without their listings ("No information") until the guide's next update.
        try { writeChannels() } catch (e: Exception) { Log.w("Dingus", "save channels", e) }
        Log.i("Dingus", "Update: guide saved in ${System.currentTimeMillis() - t2} ms")
        changed()
    }

    val guideChannels: Int get() = guide.size

    /** How far back the guide keeps programmes: a day when any channel has catch-up, otherwise an hour. */
    val backHours: Int get() = if (channels.any { it.archiveDays > 0 }) 24 else 1

    /** Can [p] on [c] be watched again now (catch-up)? */
    fun canCatchUp(c: Channel, p: Programme, now: Long = System.currentTimeMillis()): Boolean =
        c.archiveDays > 0 && p.stop <= now && p.start >= now - c.archiveDays * 86_400_000L && Store.playlist?.type == "xtream"

    /** Start over: what's on now, from its beginning, on a catch-up channel. */
    fun canStartOver(c: Channel, p: Programme?, now: Long = System.currentTimeMillis()): Boolean =
        p != null && c.archiveDays > 0 && p.start <= now && p.stop > now && now - p.start > 60_000 && Store.playlist?.type == "xtream"

    /** The address of a past programme on a catch-up channel (the provider expects its own time zone). */
    fun catchUpUrl(c: Channel, p: Programme): String? {
        val pl = Store.playlist ?: return null
        val tz = Store.serverTimeZone?.let { java.util.TimeZone.getTimeZone(it) } ?: java.util.TimeZone.getDefault()
        val f = java.text.SimpleDateFormat("yyyy-MM-dd:HH-mm", java.util.Locale.US).apply { timeZone = tz }
        val mins = ((p.stop - p.start) / 60_000).coerceAtLeast(1)
        return "${Failover.base(pl)}/timeshift/${Sources.enc(pl.user)}/${Sources.enc(pl.pass)}/$mins/${f.format(java.util.Date(p.start))}/${c.id}.ts"
    }

    fun clear() {
        set(emptyList(), emptyList(), null)
        guide = emptyMap()
        synchronized(backupsLock) { backups = emptyMap(); backupsRead = false }
        File(dir, "channels.bin").delete()
        File(dir, "guide.bin").delete()
        File(dir, "guide-logos.json").delete()
        channelsDone = 0L to ""; guideDone = 0L to ""
        changed()
    }

    // ------------------------------------------------------------------ saved copies (compact, quick to read)

    private fun out(name: String) = DataOutputStream(BufferedOutputStream(FileOutputStream(File(dir, "$name.tmp")), 1 shl 16))
    private fun commit(name: String) { File(dir, "$name.tmp").renameTo(File(dir, name)) }
    private fun DataOutputStream.str(s: String?) = writeUTF(s ?: "\u0000")
    private fun DataInputStream.str(): String? = readUTF().let { if (it == "\u0000") null else it }

    // (The channel and guide updates can both save the channels: one at a time, so the file never gets mixed up.)
    private val channelsFileLock = Any()
    private fun writeChannels() = synchronized(channelsFileLock) { writeChannelsNow() }
    private fun writeChannelsNow() {
        out("channels.bin").use { o ->
            o.writeInt(3)
            o.str(guideUrl)
            o.writeInt(rawCategories.size)
            for (c in rawCategories) { o.writeUTF(c.id); o.writeUTF(c.name) }
            o.writeInt(raw.size)
            for (c in raw) {
                c.guideKey = byId[c.id]?.guideKey ?: c.guideKey
                o.writeUTF(c.id); o.writeUTF(c.name); o.writeInt(c.number); o.writeUTF(c.categoryId)
                o.str(c.playlistLogo); o.str(c.epgId); o.writeUTF(c.url); o.str(c.guideKey); o.writeShort(c.archiveDays)
            }
        }
        commit("channels.bin")
    }

    private fun readChannels(): Boolean {
        val f = File(dir, "channels.bin")
        if (!f.exists()) return false
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            val version = i.readInt()
            if (version != 2 && version != 3) return false
            val url = i.str()
            val cats = List(i.readInt()) { Category(i.readUTF(), i.readUTF()) }
            val chans = List(i.readInt()) {
                Channel(i.readUTF(), i.readUTF(), i.readInt(), i.readUTF(), i.str(), i.str(), i.readUTF()).let { c ->
                    val key = i.str()
                    val days = if (version >= 3) i.readShort().toInt() else 0
                    (if (days > 0) Channel(c.id, c.name, c.number, c.categoryId, c.playlistLogo, c.epgId, c.url, days) else c).also { it.guideKey = key }
                }
            }
            set(cats, chans, url)
        }
        return channels.isNotEmpty()
    }

    /**
     * The guide on the device, version 3: each programme's times, title and genre, with the descriptions kept in one
     * block at the end – so reading it at start doesn't make thousands of description strings nobody's looked at.
     */
    private fun writeGuide() {
        val descs = java.io.ByteArrayOutputStream(1 shl 20)
        out("guide.bin").use { o ->
            o.writeInt(3)
            o.writeInt(guide.size)
            for ((k, list) in guide) {
                o.writeUTF(k)
                o.writeInt(list.size)
                for (p in list) {
                    o.writeLong(p.start); o.writeLong(p.stop); o.writeUTF(p.title); o.writeByte(p.genre.toInt())
                    val b = p.desc?.takeIf { it.isNotEmpty() }?.toByteArray(Charsets.UTF_8)
                    o.writeInt(if (b == null) 0 else descs.size()); o.writeInt(b?.size ?: 0)
                    if (b != null) descs.write(b)
                }
            }
            o.writeInt(descs.size())
            descs.writeTo(o)
        }
        commit("guide.bin")
    }

    private fun readGuide() {
        val f = File(dir, "guide.bin")
        if (!f.exists()) return
        val cutoff = System.currentTimeMillis() - backHours * 60 * 60_000L
        val g = HashMap<String, Array<Programme>>()
        val titles = HashMap<String, String>() // the same titles over and over: one copy each
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 17)).use { i ->
            val version = i.readInt()
            if (version !in 1..3) return
            val blob = DescBlob()
            repeat(i.readInt()) {
                val k = i.readUTF()
                val list = ArrayList<Programme>()
                repeat(i.readInt()) {
                    val start = i.readLong(); val stop = i.readLong()
                    val t = i.readUTF().let { titles.getOrPut(it) { it } }
                    val p = if (version >= 3) {
                        val genre = i.readByte(); val off = i.readInt(); val len = i.readInt()
                        Programme(start, stop, t, DescRef(blob, off, len), genre)
                    } else Programme(start, stop, t, i.str(), if (version >= 2) i.readByte() else 0)
                    if (p.stop > cutoff) list.add(p)
                }
                if (list.isNotEmpty()) g[k] = list.toTypedArray()
            }
            if (version >= 3) { val n = i.readInt(); val b = ByteArray(n); i.readFully(b); blob.bytes = b }
        }
        // (A fresh guide from the provider may have arrived while this was being read: keep that one.)
        if (guide.isEmpty()) guide = g
    }
}
