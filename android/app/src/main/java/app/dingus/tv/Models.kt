package app.dingus.tv

/** A playlist category ("UK Sport", "News"…). */
class Category(val id: String, val name: String)

/** A live channel from the playlist. [epgId] is the playlist's guide id (Xtream epg_channel_id / M3U tvg-id). */
class Channel(
    val id: String,
    val name: String,
    val number: Int,
    val categoryId: String,
    val logo: String?,
    val epgId: String?,
    val url: String,
    /** Days of catch-up the provider keeps for this channel (0 = none). */
    val archiveDays: Int = 0,
) {
    /** The key its programmes are filed under in the guide (set once the guide's loaded). */
    @Volatile var guideKey: String? = null
}

/** A programme in the TV guide (times in milliseconds since 1970, UTC). */
class Programme(val start: Long, val stop: Long, val title: String, desc: String?, val genre: Byte = 0) {
    /** The description, or where it is in the saved guide (read only when it's shown – most never are). */
    private var d: Any? = desc
    val desc: String? get() {
        val v = d
        return if (v is DescRef) v.read().also { d = it } else v as String?
    }
    constructor(start: Long, stop: Long, title: String, ref: DescRef, genre: Byte) : this(start, stop, title, null as String?, genre) { d = ref }
}

/** All the saved guide's descriptions in one block of bytes (half the memory of separate strings), read on demand. */
class DescBlob { @Volatile var bytes: ByteArray = ByteArray(0) }
class DescRef(private val blob: DescBlob, private val off: Int, private val len: Int) {
    fun read(): String? = if (len <= 0 || off + len > blob.bytes.size) null else String(blob.bytes, off, len, Charsets.UTF_8)
}

/** Your playlist: an Xtream Codes login, or an M3U link. */
data class Playlist(
    val name: String,
    val type: String,          // "xtream" | "m3u"
    val server: String = "",
    val user: String = "",
    val pass: String = "",
    val m3uUrl: String = "",
    /** Your own TV guide addresses, one per line, first = used first. */
    val epgUrl: String = "",
    val id: String = "",
    /** Also use the guide the provider gives (after your own). */
    val useProviderGuide: Boolean = true,
) {
    val epgUrls: List<String> get() = epgUrl.split('\n').map { it.trim() }.filter { it.isNotEmpty() }
}
