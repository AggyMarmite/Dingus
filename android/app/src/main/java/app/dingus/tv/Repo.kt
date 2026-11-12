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
    @Volatile var channels: List<Channel> = emptyList(); private set
    @Volatile private var byId: Map<String, Channel> = emptyMap()
    @Volatile private var byCategory: Map<String, List<Channel>> = emptyMap()
    @Volatile private var guide: Map<String, Array<Programme>> = emptyMap()
    @Volatile var guideUrl: String? = null; private set

    private val listeners = mutableListOf<() -> Unit>()
    fun onChanged(l: () -> Unit) { synchronized(listeners) { listeners.add(l) } }
    fun removeListener(l: () -> Unit) { synchronized(listeners) { listeners.remove(l) } }
    private fun changed() { synchronized(listeners) { listeners.toList() }.forEach { it() } }

    private lateinit var dir: File
    fun init(ctx: Context) { dir = ctx.filesDir }

    fun channel(id: String?) = id?.let { byId[it] }

    /** The categories down the side: Favourites and Recently watched first, then the playlist's. */
    fun sideCategories(): List<Category> {
        val out = ArrayList<Category>()
        out.add(Category(ALL, "All channels"))
        if (Store.favourites.any { byId.containsKey(it) }) out.add(Category(FAV, "Favourites"))
        if (Store.recent.any { byId.containsKey(it) }) out.add(Category(RECENT, "Recently watched"))
        out.addAll(categories.filter { byCategory[it.id]?.isNotEmpty() == true })
        return out
    }

    fun channelsIn(categoryId: String): List<Channel> = when (categoryId) {
        ALL -> channels
        FAV -> Store.favourites.mapNotNull { byId[it] }
        RECENT -> Store.recent.mapNotNull { byId[it] }
        else -> byCategory[categoryId] ?: emptyList()
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
        categories = cats
        channels = chans
        byId = chans.associateBy { it.id }
        byCategory = chans.groupBy { it.categoryId }
        guideUrl = url
    }

    /** What was saved last time (instant start). False when there's nothing yet. */
    suspend fun loadSaved(): Boolean = withContext(Dispatchers.IO) {
        val ok = try { readChannels() } catch (e: Exception) { Log.w("Dingus", "saved channels", e); false }
        if (ok) {
            try { readGuide() } catch (e: Exception) { Log.w("Dingus", "saved guide", e) }
        }
        ok
    }

    /** Fetches the playlist from the provider (throws with a message to show if it can't). */
    suspend fun refreshChannels(p: Playlist, already: Sources.Result? = null) = withContext(Dispatchers.IO) {
        val r = already ?: Sources.load(p)
        // Keep guide matches from before, so now/next shows straight away until the guide's read again.
        val oldKeys = channels.associate { it.id to it.guideKey }
        r.channels.forEach { it.guideKey = oldKeys[it.id] }
        set(r.categories, r.channels, r.guideUrl)
        try { writeChannels() } catch (e: Exception) { Log.w("Dingus", "save channels", e) }
        changed()
    }

    /** Reads the TV guide (in the background; can take a while for big guides). */
    suspend fun refreshGuide() = withContext(Dispatchers.IO) {
        val url = guideUrl ?: return@withContext
        val chans = channels
        val parsed = Net.open(url, 30_000).use { Guide.parse(it, chans) }
        for (c in chans) c.guideKey = parsed.keysByChannelId[c.id]
        guide = parsed.programmes
        Store.guideLoadedAt = System.currentTimeMillis()
        try { writeGuide() } catch (e: Exception) { Log.w("Dingus", "save guide", e) }
        changed()
    }

    val guideChannels: Int get() = guide.size

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
            o.writeInt(2)
            o.str(guideUrl)
            o.writeInt(categories.size)
            for (c in categories) { o.writeUTF(c.id); o.writeUTF(c.name) }
            o.writeInt(channels.size)
            for (c in channels) {
                o.writeUTF(c.id); o.writeUTF(c.name); o.writeInt(c.number); o.writeUTF(c.categoryId)
                o.str(c.logo); o.str(c.epgId); o.writeUTF(c.url); o.str(c.guideKey)
            }
        }
        commit("channels.bin")
    }

    private fun readChannels(): Boolean {
        val f = File(dir, "channels.bin")
        if (!f.exists()) return false
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            if (i.readInt() != 2) return false
            val url = i.str()
            val cats = List(i.readInt()) { Category(i.readUTF(), i.readUTF()) }
            val chans = List(i.readInt()) {
                Channel(i.readUTF(), i.readUTF(), i.readInt(), i.readUTF(), i.str(), i.str(), i.readUTF()).also { it.guideKey = i.str() }
            }
            set(cats, chans, url)
        }
        return channels.isNotEmpty()
    }

    private fun writeGuide() {
        out("guide.bin").use { o ->
            o.writeInt(1)
            o.writeInt(guide.size)
            for ((k, list) in guide) {
                o.writeUTF(k)
                o.writeInt(list.size)
                for (p in list) { o.writeLong(p.start); o.writeLong(p.stop); o.writeUTF(p.title); o.str(p.desc) }
            }
        }
        commit("guide.bin")
    }

    private fun readGuide() {
        val f = File(dir, "guide.bin")
        if (!f.exists()) return
        val cutoff = System.currentTimeMillis() - 60 * 60_000L
        val g = HashMap<String, Array<Programme>>()
        DataInputStream(BufferedInputStream(FileInputStream(f), 1 shl 16)).use { i ->
            if (i.readInt() != 1) return
            repeat(i.readInt()) {
                val k = i.readUTF()
                val list = ArrayList<Programme>()
                repeat(i.readInt()) {
                    val p = Programme(i.readLong(), i.readLong(), i.readUTF(), i.str())
                    if (p.stop > cutoff) list.add(p)
                }
                if (list.isNotEmpty()) g[k] = list.toTypedArray()
            }
        }
        guide = g
    }
}
