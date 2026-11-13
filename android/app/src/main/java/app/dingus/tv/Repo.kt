package app.dingus.tv

import android.content.Context
import android.util.Log
import kotlinx.coroutines.Dispatchers
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
        val out = ArrayList<Category>()
        out.add(Category(ALL, "All channels"))
        if (Store.favourites.any { byId.containsKey(it) }) out.add(Category(FAV, "Favourites"))
        if (Store.recent.any { byId.containsKey(it) }) out.add(Category(RECENT, "Recently watched"))
        out.addAll(Store.ordered("live", categories.filter { byCategory[it.id]?.isNotEmpty() == true && !Store.isHidden("live:${it.id}") }) { it.id })
        return out
    }

    /** A category's channels as the guide shows them: hidden ones left out, your numbers, duplicates merged (if on). */
    fun channelsIn(categoryId: String): List<Channel> {
        if (categoryId == FAV || categoryId == RECENT) {
            val ids = if (categoryId == FAV) Store.favourites else Store.recent
            return ids.mapNotNull { byId[it] }.filter { it.id !in hiddenIds }
        }
        synchronized(listCache) { listCache[categoryId]?.let { return it } }
        var list = (if (categoryId == ALL) channels else byCategory[categoryId] ?: emptyList())
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
        return Merge.order(channels.filter { it.id !in hiddenIds && Merge.key(it) == k })
    }

    /** Every channel including hidden ones, for the channel editor. */
    fun allForEditing(): List<Channel> = channels
    fun isHiddenChannel(id: String) = id in hiddenIds

    /** Settings that change what the lists hold (merging, channel edits) call this. */
    fun reapply() {
        val keys = channels.associate { it.id to it.guideKey }
        raw.forEach { r -> keys[r.id]?.let { r.guideKey = it } }
        set(categories, raw, guideUrl)
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
        val edited = ChannelEdits.apply(chans, includeHidden = true)
        val edits = ChannelEdits.all()
        categories = cats
        raw = chans
        channels = edited
        hiddenIds = edits.filterValues { it.hidden }.keys
        renumbered = edits.values.any { it.number != null }
        byId = edited.associateBy { it.id }
        byCategory = edited.groupBy { it.categoryId }
        guideUrl = url
        synchronized(listCache) { listCache.clear() }
        Merge.clear()
        if (Store.mergeChannels) edited.forEach { Merge.key(it) } // worked out now (in the background when loading)
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
        (try { readChannels() } catch (e: Exception) { Log.w("Dingus", "saved channels", e); false }).also { if (it) channelsIn(ALL) }
    }

    /** Then the saved guide (can take a moment for a big one). */
    suspend fun loadSavedGuide() = withContext(Dispatchers.IO) {
        try { readGuide(); changed() } catch (e: Exception) { Log.w("Dingus", "saved guide", e) }
    }

    /** Fetches the playlist from the provider (throws with a message to show if it can't). */
    suspend fun refreshChannels(p: Playlist, already: Sources.Result? = null) = withContext(Dispatchers.IO) {
        val r = already ?: Sources.load(p)
        Sources.accountInfo?.let { Account.remember(it); Sources.accountInfo = null }
        // Keep guide matches from before, so now/next shows straight away until the guide's read again.
        val oldKeys = channels.associate { it.id to it.guideKey }
        r.channels.forEach { it.guideKey = oldKeys[it.id] }
        set(r.categories, r.channels, r.guideUrl)
        channelsIn(ALL) // (worked out here, in the background, not when the guide first draws)
        try { writeChannels() } catch (e: Exception) { Log.w("Dingus", "save channels", e) }
        changed()
    }

    /** Reads the TV guide (in the background; can take a while for big guides). */
    suspend fun refreshGuide() = withContext(Dispatchers.IO) {
        val p = Store.playlist
        // Your own guide addresses first (in your order), then the provider's: each fills in channels the earlier ones missed.
        val sources = (p?.epgUrls ?: emptyList()) + listOfNotNull(guideUrl.takeIf { p?.useProviderGuide != false })
        if (sources.isEmpty()) return@withContext
        val chans = channels
        val keys = HashMap<String, String>()
        val programmes = HashMap<String, Array<Programme>>()
        var lastError: Exception? = null
        var anyWorked = false
        for ((n, url) in sources.withIndex()) {
            val todo = chans.filter { !keys.containsKey(it.id) }
            if (todo.isEmpty()) break
            try {
                val parsed = Net.open(url, 30_000).use { Guide.parse(it, todo, hoursAhead = Store.guideHours, hoursBack = backHours) }
                anyWorked = true
                // Keys from different guides could clash, so each guide's are kept apart.
                for ((k, v) in parsed.programmes) programmes["$n|$k"] = v
                for ((id, k) in parsed.keysByChannelId) keys[id] = "$n|$k"
            } catch (e: Exception) {
                Log.w("Dingus", "guide $n", e)
                lastError = e
            }
        }
        if (!anyWorked) throw lastError ?: IOException("No TV guide")
        for (c in chans) c.guideKey = keys[c.id]
        for (c in raw) c.guideKey = keys[c.id]
        guide = programmes
        Store.guideLoadedAt = System.currentTimeMillis()
        try { writeGuide() } catch (e: Exception) { Log.w("Dingus", "save guide", e) }
        changed()
    }

    val guideChannels: Int get() = guide.size

    /** How far back the guide keeps programmes: a day when any channel has catch-up, otherwise an hour. */
    val backHours: Int get() = if (channels.any { it.archiveDays > 0 }) 24 else 1

    /** Can [p] on [c] be watched again now (catch-up)? */
    fun canCatchUp(c: Channel, p: Programme, now: Long = System.currentTimeMillis()): Boolean =
        c.archiveDays > 0 && p.stop <= now && p.start >= now - c.archiveDays * 86_400_000L && Store.playlist?.type == "xtream"

    /** The address of a past programme on a catch-up channel (the provider expects its own time zone). */
    fun catchUpUrl(c: Channel, p: Programme): String? {
        val pl = Store.playlist ?: return null
        val tz = Store.serverTimeZone?.let { java.util.TimeZone.getTimeZone(it) } ?: java.util.TimeZone.getDefault()
        val f = java.text.SimpleDateFormat("yyyy-MM-dd:HH-mm", java.util.Locale.US).apply { timeZone = tz }
        val mins = ((p.stop - p.start) / 60_000).coerceAtLeast(1)
        return "${Sources.xtreamBase(pl.server)}/timeshift/${Sources.enc(pl.user)}/${Sources.enc(pl.pass)}/$mins/${f.format(java.util.Date(p.start))}/${c.id}.ts"
    }

    fun clear() {
        set(emptyList(), emptyList(), null)
        guide = emptyMap()
        File(dir, "channels.bin").delete()
        File(dir, "guide.bin").delete()
        changed()
    }

    // ------------------------------------------------------------------ saved copies (compact, quick to read)

    private fun out(name: String) = DataOutputStream(BufferedOutputStream(FileOutputStream(File(dir, "$name.tmp")), 1 shl 16))
    private fun commit(name: String) { File(dir, "$name.tmp").renameTo(File(dir, name)) }
    private fun DataOutputStream.str(s: String?) = writeUTF(s ?: "\u0000")
    private fun DataInputStream.str(): String? = readUTF().let { if (it == "\u0000") null else it }

    private fun writeChannels() {
        out("channels.bin").use { o ->
            o.writeInt(3)
            o.str(guideUrl)
            o.writeInt(categories.size)
            for (c in categories) { o.writeUTF(c.id); o.writeUTF(c.name) }
            o.writeInt(raw.size)
            for (c in raw) {
                c.guideKey = byId[c.id]?.guideKey ?: c.guideKey
                o.writeUTF(c.id); o.writeUTF(c.name); o.writeInt(c.number); o.writeUTF(c.categoryId)
                o.str(c.logo); o.str(c.epgId); o.writeUTF(c.url); o.str(c.guideKey); o.writeShort(c.archiveDays)
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
                    (if (days > 0) Channel(c.id, c.name, c.number, c.categoryId, c.logo, c.epgId, c.url, days) else c).also { it.guideKey = key }
                }
            }
            set(cats, chans, url)
        }
        return channels.isNotEmpty()
    }

    private fun writeGuide() {
        out("guide.bin").use { o ->
            o.writeInt(2)
            o.writeInt(guide.size)
            for ((k, list) in guide) {
                o.writeUTF(k)
                o.writeInt(list.size)
                for (p in list) { o.writeLong(p.start); o.writeLong(p.stop); o.writeUTF(p.title); o.str(p.desc); o.writeByte(p.genre.toInt()) }
            }
        }
        commit("guide.bin")
    }

    private fun readGuide() {
        val f = File(dir, "guide.bin")
        if (!f.exists()) return
        val cutoff = System.currentTimeMillis() - backHours * 60 * 60_000L
        val g = HashMap<String, Array<Programme>>()
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            val version = i.readInt()
            if (version != 1 && version != 2) return
            repeat(i.readInt()) {
                val k = i.readUTF()
                val list = ArrayList<Programme>()
                repeat(i.readInt()) {
                    val p = Programme(i.readLong(), i.readLong(), i.readUTF(), i.str(), if (version >= 2) i.readByte() else 0)
                    if (p.stop > cutoff) list.add(p)
                }
                if (list.isNotEmpty()) g[k] = list.toTypedArray()
            }
        }
        // (A fresh guide from the provider may have arrived while this was being read: keep that one.)
        if (guide.isEmpty()) guide = g
    }
}
