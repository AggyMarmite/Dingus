package app.dingus.tv

import android.app.Activity
import android.os.Handler
import android.os.Looper
import android.widget.ImageView
import android.view.View
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.launch

/** The banner above the rows: shows what's highlighted, fetching a film's or show's plot after a short pause. */
class Hero(
    private val a: Activity,
    private val scope: CoroutineScope,
    /** Where the picture goes (Films and TV Shows: behind the whole screen); the banner's own otherwise. */
    private val art: ImageView = a.findViewById(R.id.heroArt),
    /** The picture's height to fetch at. */
    private val artH: Int = a.resources.displayMetrics.heightPixels / 2,
) {
    private val kicker: TextView = a.findViewById(R.id.heroKicker)
    private val title: TextView = a.findViewById(R.id.heroTitle)
    private val meta: TextView = a.findViewById(R.id.heroMeta)
    private val text: TextView = a.findViewById(R.id.heroText)
    private val logo: ImageView = a.findViewById(R.id.heroLogo)
    private val handler = Handler(Looper.getMainLooper())
    private var job: Job? = null
    private var showing: Card? = null
    private var pendingArt: String? = null
    private val loadArt = Runnable { val p = pendingArt; if (art.tag != p) Posters.load(art, p, artH, wide = true, keep = true) }

    init {
        // Fades the artwork into the background colour (left and bottom), drawn in code so it suits every theme.
        val bg = Themes.color(a, R.attr.dBg)
        val clear = bg and 0x00FFFFFF
        val half = (bg and 0x00FFFFFF) or (0x99 shl 24)
        val left = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(bg, half, clear))
        val bottom = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(bg, clear, clear))
        a.findViewById<android.view.View>(R.id.heroShade).background = android.graphics.drawable.LayerDrawable(arrayOf(left, bottom))
    }

    fun show(card: Card) {
        showing = card
        handler.removeCallbacksAndMessages(null)
        job?.cancel()
        TitleLogos.show(logo, title, (card as? Card.Title)?.let { TitleLogos.cached(it.kind, it.item) })
        when (card) {
            is Card.Title -> {
                val m = card.item
                // No label above a film or show: the room goes to its description.
                kicker.text = ""; kicker.visibility = View.GONE
                title.text = m.name
                val d = Vod.cachedDetails(card.kind, m.id)
                bindTitle(card, d)
                // Details already known but not the logo: look it up after a moment (no wait for the provider).
                if (d != null && TitleLogos.enabled && TitleLogos.cached(card.kind, m) == null && !TitleLogos.knowsBackdrop(card.kind, m)) handler.postDelayed({
                    job = scope.launch { TitleLogos.find(card.kind, m, d.tmdbId)?.let { if (showing === card) TitleLogos.show(logo, title, it) } }
                }, 200)
                // Not known yet: the details and the logo looked up together, as soon as you've paused on it.
                if (d == null) handler.postDelayed({
                    job = scope.launch {
                        val logoJob = if (TitleLogos.enabled) async { try { TitleLogos.find(card.kind, m, null) } catch (e: Exception) { null } } else null
                        val got = try { Vod.details(card.kind, m) } catch (e: Exception) { null }
                        if (showing === card) { posterFallback = got == null; bindTitle(card, got); posterFallback = false }
                        val found = logoJob?.await() ?: if (TitleLogos.enabled && got?.tmdbId != null) TitleLogos.find(card.kind, m, got.tmdbId) else null
                        if (showing === card) found?.let { TitleLogos.show(logo, title, it) }
                    }
                }, 250)
            }
            is Card.Live -> {
                val c = card.channel
                val (now, next) = Repo.nowNext(c)
                kicker.visibility = View.VISIBLE
                kicker.text = if (now != null) "ON NOW  ·  ${c.name.uppercase()}" else c.name.uppercase()
                title.text = now?.title ?: c.name
                val t = System.currentTimeMillis()
                meta.text = listOfNotNull(
                    if (Store.showNumbers && c.number > 0) "${c.number}" else null,
                    now?.let { "${Ui.span(it)}  ·  ${(it.stop - t + 59_999) / 60_000} min left" },
                    next?.let { "Next ${Ui.time(it.start)}  ${it.title}" },
                ).joinToString("   ·   ")
                text.text = now?.desc ?: ""
                art.removeCallbacks(loadArt)
                art.setImageDrawable(null)
                art.tag = null
            }
            is Card.Show -> show(Card.Live(card.channel))
            is Card.SeeAll -> {
                kicker.visibility = View.VISIBLE
                kicker.text = if (card.kind == Vod.MOVIES) "FILMS" else "TV SHOWS"
                title.text = if (card.chip) card.name.substringBefore("  ·  ") else card.name
                val n = Vod.cached(card.kind)?.let { Vod.itemsIn(card.kind, it, card.categoryId).size } ?: 0
                meta.text = (if (n > 0) "$n ${if (card.kind == Vod.MOVIES) "films" else "shows"}  ·  " else "") + "OK to see them all"
                text.text = ""
            }
        }
    }

    private var posterFallback = false
    private val rated = HashSet<String>()

    private fun bindTitle(card: Card.Title, d: Details?) {
        val m = card.item
        // Year, UK age badge and TMDB's score (once looked up; the provider's ★ until then), genre.
        val r = Ratings.cached(card.kind, m)
        meta.text = Badges.line(meta.context, d?.year ?: m.year, r, d?.rating?.toFloatOrNull() ?: m.rating, listOfNotNull(d?.genre, card.label), meta.textSize)
        if (r == null && d != null && TitleLogos.enabled && rated.add("${card.kind}:${m.id}")) scope.launch {
            Ratings.find(card.kind, m, d.tmdbId)?.let { if (showing === card) bindTitle(card, d) }
        }
        text.text = d?.plot ?: ""
        // Details not known yet: no picture until they are (they have the right one) – showing the poster first and then
        // swapping to the proper background looked like it fixed itself. (If they can't be had, the poster after all.)
        // (The last title's picture goes meanwhile, so it's never shown under this one's name.)
        if (d == null && !posterFallback) { art.removeCallbacks(loadArt); art.setImageDrawable(null); art.tag = null; return }
        val pic = d?.backdrop ?: d?.poster ?: m.poster
        // The big picture behind only once you've stopped on something (decoding and drawing a full-screen picture
        // on every step is what made scrolling stutter).
        pendingArt = pic
        art.removeCallbacks(loadArt)
        if (art.tag != pic) art.postDelayed(loadArt, 280)
    }
}
