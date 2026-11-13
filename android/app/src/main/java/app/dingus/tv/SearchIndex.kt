package app.dingus.tv

import java.text.Normalizer

/**
 * Everything searchable, with the names already lower-cased and without accents, worked out once (in the
 * background) instead of on every key press – so searching thousands of channels, programmes and films is quick.
 */
object SearchIndex {
    private val marks = Regex("\\p{Mn}+")

    /** "Café" → "cafe". Plain ASCII (most names) skips the slow part. */
    fun norm(s: String): String {
        val l = s.lowercase()
        for (ch in l) if (ch.code >= 128) return marks.replace(Normalizer.normalize(l, Normalizer.Form.NFD), "")
        return l
    }

    class Names<T>(val key: Any, val items: List<T>, val names: Array<String>)
    class OnTv(val key: Any, val channels: List<Channel>, val programmes: List<Programme>, val titles: Array<String>)

    @Volatile private var chans: Names<Channel>? = null
    @Volatile private var onTv: OnTv? = null
    private val vod = HashMap<String, Names<VodItem>>()

    fun channels(): Names<Channel> {
        val list = Repo.channels
        chans?.let { if (it.key === list) return it }
        return Names(list, list, Array(list.size) { norm(list[it].name) }).also { chans = it }
    }

    /** Programmes on now or in the next 12 hours (redone when the guide changes, and every 15 minutes). */
    fun onTv(): OnTv {
        val now = System.currentTimeMillis()
        val key = "${Repo.version}/${now / 900_000L}"
        onTv?.let { if (it.key == key) return it }
        val cs = ArrayList<Channel>(); val ps = ArrayList<Programme>(); val ts = ArrayList<String>()
        val until = now + 12 * 3600_000L
        for (c in Repo.channels) for (p in Repo.programmes(c)) {
            if (p.start >= until) break
            if (p.stop > now) { cs.add(c); ps.add(p); ts.add(norm(p.title)) }
        }
        return OnTv(key, cs, ps, ts.toTypedArray()).also { onTv = it }
    }

    fun vod(kind: String): Names<VodItem>? {
        val lib = Vod.cached(kind) ?: return null
        synchronized(vod) { vod[kind]?.let { if (it.key === lib) return it } }
        val items = lib.items
        val n = Names<VodItem>(lib, items, Array(items.size) { norm(items[it].name) })
        synchronized(vod) { vod[kind] = n }
        return n
    }

    /** Works everything out ahead of time (when Search opens). */
    fun warm() { channels(); onTv(); vod(Vod.MOVIES); vod(Vod.SERIES) }
}
