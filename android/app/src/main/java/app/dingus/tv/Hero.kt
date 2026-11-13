package app.dingus.tv

import android.app.Activity
import android.os.Handler
import android.os.Looper
import android.widget.ImageView
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch

/** The banner above the rows: shows what's highlighted, fetching a film's or show's plot after a short pause. */
class Hero(private val a: Activity, private val scope: CoroutineScope) {
    private val art: ImageView = a.findViewById(R.id.heroArt)
    private val kicker: TextView = a.findViewById(R.id.heroKicker)
    private val title: TextView = a.findViewById(R.id.heroTitle)
    private val meta: TextView = a.findViewById(R.id.heroMeta)
    private val text: TextView = a.findViewById(R.id.heroText)
    private val handler = Handler(Looper.getMainLooper())
    private var job: Job? = null
    private var showing: Card? = null
    private val artH = a.resources.displayMetrics.heightPixels / 2

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
        when (card) {
            is Card.Title -> {
                val m = card.item
                kicker.text = if (card.kind == Vod.MOVIES) "FILM" else "TV SHOW"
                title.text = m.name
                val d = Vod.cachedDetails(card.kind, m.id)
                bindTitle(card, d)
                if (d == null) handler.postDelayed({
                    job = scope.launch {
                        val got = try { Vod.details(card.kind, m) } catch (e: Exception) { null }
                        if (showing === card && got != null) bindTitle(card, got)
                    }
                }, 650)
            }
            is Card.Live -> {
                val c = card.channel
                val (now, next) = Repo.nowNext(c)
                kicker.text = if (now != null) "ON NOW  ·  ${c.name.uppercase()}" else c.name.uppercase()
                title.text = now?.title ?: c.name
                val t = System.currentTimeMillis()
                meta.text = listOfNotNull(
                    if (Store.showNumbers && c.number > 0) "${c.number}" else null,
                    now?.let { "${Ui.span(it)}  ·  ${(it.stop - t + 59_999) / 60_000} min left" },
                    next?.let { "Next ${Ui.time(it.start)}  ${it.title}" },
                ).joinToString("   ·   ")
                text.text = now?.desc ?: ""
                art.setImageDrawable(null)
                art.tag = null
            }
            is Card.Show -> show(Card.Live(card.channel))
            is Card.SeeAll -> {
                kicker.text = if (card.kind == Vod.MOVIES) "FILMS" else "TV SHOWS"
                title.text = card.name
                meta.text = "See everything in this category"
                text.text = ""
            }
        }
    }

    private fun bindTitle(card: Card.Title, d: Details?) {
        val m = card.item
        meta.text = listOfNotNull(
            (d?.year ?: m.year).takeIf { it > 0 }?.toString(),
            (d?.rating?.toFloatOrNull() ?: m.rating).takeIf { it > 0 }?.let { "★ %.1f".format(it) },
            d?.genre,
            card.label,
        ).joinToString("   ·   ")
        text.text = d?.plot ?: ""
        val pic = d?.backdrop ?: d?.poster ?: m.poster
        if (art.tag != pic) Posters.load(art, pic, artH)
    }
}
