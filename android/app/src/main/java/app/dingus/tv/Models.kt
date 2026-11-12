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
) {
    /** The key its programmes are filed under in the guide (set once the guide's loaded). */
    @Volatile var guideKey: String? = null
}

/** A programme in the TV guide (times in milliseconds since 1970, UTC). */
class Programme(val start: Long, val stop: Long, val title: String, val desc: String?)

/** Your playlist: an Xtream Codes login, or an M3U link. */
data class Playlist(
    val name: String,
    val type: String,          // "xtream" | "m3u"
    val server: String = "",
    val user: String = "",
    val pass: String = "",
    val m3uUrl: String = "",
    val epgUrl: String = "",
)
