package app.dingus.tv

/**
 * What an update is doing right now, for the note at the top of the screen: "Updating Live TV…",
 * "Updating films & TV shows…", "Saving…". Several steps can run at once (films download while the TV guide does).
 */
object Progress {
    const val LIVE = "Live TV"
    const val GUIDE = "TV guide"
    const val FILMS = "films"
    const val SHOWS = "TV shows"
    private const val SAVING = "\u0001saving"

    private val active = LinkedHashSet<String>()

    /** Runs [block] as the step [name] (shown in the note while it runs). */
    inline fun <T> step(name: String, block: () -> T): T {
        begin(name)
        try { return block() } finally { end(name) }
    }

    inline fun <T> saving(block: () -> T): T = step(savingName(), block)
    fun savingName() = SAVING

    fun begin(name: String) { synchronized(active) { active.add(name) }; show() }
    fun end(name: String) {
        val any = synchronized(active) { active.remove(name); active.isNotEmpty() }
        if (any) { show(); return }
        // All done (unless the next step starts straight away, e.g. saving after downloading): the update's own
        // "done" note now (it waited for the films and shows), or nothing.
        main.removeCallbacks(finish); main.postDelayed(finish, 500)
    }

    private val main = android.os.Handler(android.os.Looper.getMainLooper())
    private val finish = Runnable {
        if (running) return@Runnable
        val d = pendingDone; pendingDone = null
        if (d != null) Notice.show(d.first, d.second) else if (shown) Notice.clear()
        shown = false
    }
    /** Whether the note showing is one of ours ("Updating …"), so finishing only clears that. */
    @Volatile private var shown = false

    /** Another note came: leave it alone. */
    fun cancelFinish() { main.removeCallbacks(finish); shown = false }

    /** A "done" note that came while other steps were still going. */
    @Volatile var pendingDone: Pair<String, Long>? = null

    val running: Boolean get() = synchronized(active) { active.isNotEmpty() }

    private fun show() {
        val (work, saving) = synchronized(active) { active.filter { it != SAVING } to (SAVING in active) }
        if (work.isEmpty() && !saving) return
        val text = when {
            work.isEmpty() -> "Saving…"
            else -> "Updating " + when (work.size) { 1 -> work[0]; else -> work.dropLast(1).joinToString(", ") + " & " + work.last() } + "…"
        }
        Notice.working(text)
        shown = true
        Ui.test("progress $text")
    }
}
