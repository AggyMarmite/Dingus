package app.dingus.tv

/**
 * Settings → Look → Home rows: which rows Home shows, and in what order. A row still only appears when there's
 * something in it. Rows Dingus adds later (not in your saved order yet) go where they'd normally be.
 */
object HomeRows {
    /** Every row Home can show, in its usual order. */
    val all = listOf(
        "Continue watching", "My list", "Favourite channels", "Coming up next", "Live sport now", "Tonight's picks",
        "Recently watched", "Channels", "Trending films", "Trending TV shows", "Top films", "New films", "New TV shows",
    )

    private fun lines(key: String) = try { Store.prefsString(key, "").split('\n').filter { it.isNotBlank() } } catch (_: Exception) { emptyList() }

    /** Your order (every known row, saved ones first in your order). */
    fun order(): List<String> {
        val saved = lines("homeOrder").filter { it in all }
        return saved + all.filter { it !in saved }
    }
    fun hidden(): Set<String> = lines("homeHidden").toSet()

    fun setOrder(list: List<String>) = Store.putString("homeOrder", list.joinToString("\n"))
    fun setHidden(name: String, hide: Boolean) = Store.putString("homeHidden", (if (hide) hidden() + name else hidden() - name).joinToString("\n"))
    fun reset() { Store.putString("homeOrder", ""); Store.putString("homeHidden", "") }
    val customised: Boolean get() = lines("homeOrder").isNotEmpty() || lines("homeHidden").isNotEmpty()

    /** Home's rows as you've arranged them. */
    fun arrange(rows: List<Row>): ArrayList<Row> {
        if (!customised) return ArrayList(rows)
        val hide = hidden()
        val rank = order().withIndex().associate { (i, n) -> n to i }
        return ArrayList(rows.filter { it.title !in hide }.withIndex()
            .sortedWith(compareBy({ rank[it.value.title] ?: (1000 + it.index) }, { it.index })).map { it.value })
    }
}
