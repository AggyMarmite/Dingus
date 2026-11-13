package app.dingus.tv

import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * A small saved copy of what Home last showed (its feature and rows), per playlist. Home shows it straight away
 * when Dingus starts, while the real channels, guide and films are read in behind it – no loading screen.
 */
object HomeSnapshot {
    private lateinit var base: File
    fun init(ctx: android.content.Context) { base = ctx.filesDir }
    private fun file() = File(File(base, "p_${Store.activeId}").apply { mkdirs() }, "home.json")

    // ------------------------------------------------------------------ saving

    private fun item(m: VodItem) = JSONObject().put("i", m.id).put("n", m.name).put("p", m.poster ?: "").put("c", m.categoryId)
        .put("r", m.rating.toDouble()).put("y", m.year).put("e", m.ext).put("a", m.added)

    private fun channel(c: Channel) = JSONObject().put("i", c.id).put("n", c.name).put("u", c.number).put("l", c.logo ?: "")

    private fun programme(p: Programme) = JSONObject().put("s", p.start).put("e", p.stop).put("t", p.title).put("d", p.desc ?: "").put("g", p.genre.toInt())

    private fun card(c: Card): JSONObject = when (c) {
        is Card.Title -> JSONObject().put("y", "t").put("k", c.kind).put("m", item(c.item)).put("p", c.progress.toDouble()).put("l", c.label ?: "").put("n", c.isNew)
        is Card.Show -> JSONObject().put("y", "s").put("c", channel(c.channel)).put("l", c.label ?: "").also { o -> c.programme?.let { o.put("g", programme(it)) } }
        is Card.Live -> JSONObject().put("y", "l").put("c", channel(c.channel))
        is Card.SeeAll -> JSONObject().put("y", "a").put("k", c.kind).put("i", c.categoryId).put("n", c.name).put("h", c.chip)
    }

    fun save(slides: List<Slide>, rows: List<Row>) {
        val o = JSONObject()
        o.put("slides", JSONArray().apply {
            for (s in slides) put(JSONObject().put("k", s.kicker).put("t", s.title).put("m", s.meta).put("x", s.text ?: "")
                .put("p", s.progress.toDouble()).put("a", s.art ?: "").put("r", s.ref).put("s", s.second?.first ?: "").put("u", s.until)
                .also { j -> s.title_?.let { j.put("tk", it.first).put("ti", item(it.second)) } })
        })
        o.put("rows", JSONArray().apply {
            for (r in rows) put(JSONObject().put("t", r.title).put("c", JSONArray().apply { r.cards.take(20).forEach { put(card(it)) } }))
        })
        try {
            val f = file(); val tmp = File(f.path + ".tmp")
            tmp.writeText(o.toString()); tmp.renameTo(f)
        } catch (_: Exception) {}
    }

    // ------------------------------------------------------------------ reading

    private fun item(o: JSONObject) = VodItem(o.getString("i"), o.getString("n"), o.optString("p").ifEmpty { null }, o.optString("c"),
        o.optDouble("r").toFloat(), o.optInt("y"), o.optString("e"), o.optLong("a"))

    private fun channel(o: JSONObject) = Channel(o.getString("i"), o.getString("n"), o.optInt("u"), "", o.optString("l").ifEmpty { null }, null, "")

    private fun programme(o: JSONObject) = Programme(o.getLong("s"), o.getLong("e"), o.getString("t"), o.optString("d").ifEmpty { null }, o.optInt("g").toByte())

    private fun card(o: JSONObject): Card? = when (o.optString("y")) {
        "t" -> Card.Title(o.getString("k"), item(o.getJSONObject("m")), o.optDouble("p", -1.0).toFloat(), o.optString("l").ifEmpty { null }, o.optBoolean("n"))
        "s" -> {
            val p = o.optJSONObject("g")?.let { programme(it) }
            // A programme that's already finished (or not started yet, for "coming up"): don't show it as current.
            if (p != null && p.stop <= System.currentTimeMillis()) Card.Show(channel(o.getJSONObject("c")), null, FETCHING)
            else Card.Show(channel(o.getJSONObject("c")), p, o.optString("l").ifEmpty { null })
        }
        "l" -> Card.Live(channel(o.getJSONObject("c")))
        "a" -> Card.SeeAll(o.getString("k"), o.getString("i"), o.getString("n"), o.optBoolean("h"))
        else -> null
    }

    /**
     * The saved copy, or null. [action] turns a slide's "what to open" back into what Watch (or the second
     * button, by its label) does.
     */
    const val FETCHING = "Fetching what's on now…"

    fun read(action: (ref: String) -> () -> Unit, second: (label: String) -> (() -> Unit)?): Pair<List<Slide>, List<Row>>? = try {
        val o = JSONObject(file().readText())
        val now = System.currentTimeMillis()
        val slides = ArrayList<Slide>()
        o.optJSONArray("slides")?.let { a ->
            for (i in 0 until a.length()) {
                val j = a.getJSONObject(i)
                val label = j.optString("s")
                val sec = if (label.isEmpty()) null else second(label)?.let { label to it }
                val until = j.optLong("u")
                if (until in 1..now) {
                    // That programme has finished: say so, rather than show what was on then.
                    slides.add(Slide(j.getString("k"), FETCHING, j.getString("m").substringBefore("  ·  "), null, -1f, null,
                        action(j.optString("r")), sec, null, j.optString("r")))
                    continue
                }
                slides.add(Slide(j.getString("k"), j.getString("t"), j.getString("m"), j.optString("x").ifEmpty { null },
                    j.optDouble("p", -1.0).toFloat(), j.optString("a").ifEmpty { null }, action(j.optString("r")), sec,
                    j.optJSONObject("ti")?.let { j.getString("tk") to item(it) }, j.optString("r"), until))
            }
        }
        val rows = ArrayList<Row>()
        o.optJSONArray("rows")?.let { a ->
            for (i in 0 until a.length()) {
                val j = a.getJSONObject(i)
                val cs = j.getJSONArray("c")
                rows.add(Row(j.getString("t"), (0 until cs.length()).mapNotNull { card(cs.getJSONObject(it)) }))
            }
        }
        if (slides.isEmpty() && rows.isEmpty()) null else slides to rows
    } catch (e: Exception) { null }
}
