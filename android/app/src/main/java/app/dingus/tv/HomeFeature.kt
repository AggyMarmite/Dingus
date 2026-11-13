package app.dingus.tv

import android.app.Activity
import android.graphics.drawable.GradientDrawable
import android.graphics.drawable.LayerDrawable
import android.os.Handler
import android.os.Looper
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import androidx.recyclerview.widget.RecyclerView
import kotlinx.coroutines.launch

/** One slide of the big feature at the top of Home. */
class Slide(
    val kicker: String, val title: String, val meta: String, val text: String?,
    val progress: Float = -1f, val art: String? = null,
    val watch: () -> Unit, val second: Pair<String, () -> Unit>? = null,
    /** A film or show: its title logo is shown when there is one. */
    val title_: Pair<String, VodItem>? = null,
)

/**
 * The big feature at the top of Home, like the Windows app: a slide at a time (carry on watching, what's on,
 * live sport, new films), Watch / a second action / next, and dots. Moves on by itself every few seconds
 * while you're on it and not pressing anything.
 */
class HomeFeature(private val a: Activity, private val scope: kotlinx.coroutines.CoroutineScope? = null) : RecyclerView.Adapter<HomeFeature.H>() {
    private var slides: List<Slide> = emptyList()
    private var index = 0
    private var holder: H? = null
    private val handler = Handler(Looper.getMainLooper())
    private var lastKey = 0L
    private val artH = a.resources.displayMetrics.heightPixels * 2 / 3

    fun set(list: List<Slide>) {
        val same = list.size == slides.size && list.indices.all { list[it].title == slides[it].title }
        slides = list
        if (index >= list.size) index = 0
        if (same) holder?.let { bind(it) } else notifyDataSetChanged()
    }

    fun noteKey() { lastKey = System.currentTimeMillis() }

    private val advance = object : Runnable {
        override fun run() {
            val h = holder
            // Only while a button here is highlighted and nothing's been pressed for a while.
            if (h != null && slides.size > 1 && h.itemView.hasFocus() && System.currentTimeMillis() - lastKey > 8_000) {
                index = (index + 1) % slides.size; bind(h)
            }
            handler.postDelayed(this, 9_000)
        }
    }

    fun start() { handler.removeCallbacks(advance); handler.postDelayed(advance, 9_000) }
    fun stop() = handler.removeCallbacks(advance)

    inner class H(v: View) : RecyclerView.ViewHolder(v) {
        val art: ImageView = v.findViewById(R.id.heroArt)
        val kicker: TextView = v.findViewById(R.id.heroKicker)
        val title: TextView = v.findViewById(R.id.heroTitle)
        val meta: TextView = v.findViewById(R.id.heroMeta)
        val progress: ProgressBar = v.findViewById(R.id.heroProgress)
        val text: TextView = v.findViewById(R.id.heroText)
        val watch: TextView = v.findViewById(R.id.heroWatch)
        val second: TextView = v.findViewById(R.id.heroSecond)
        val next: TextView = v.findViewById(R.id.heroNext)
        val dots: LinearLayout = v.findViewById(R.id.heroDots)
        val logo: ImageView = v.findViewById(R.id.heroLogo)
    }

    override fun getItemCount() = if (slides.isEmpty()) 0 else 1
    override fun getItemViewType(position: Int) = 100

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): H {
        val v = LayoutInflater.from(a).inflate(R.layout.item_home_hero, parent, false)
        // A soft glow in the theme's accent colour (top right), fading into the background – drawn in code, no image.
        val accent = Themes.color(a, R.attr.dAccent)
        val bg = Themes.color(a, R.attr.dBg)
        val glow = GradientDrawable().apply {
            gradientType = GradientDrawable.RADIAL_GRADIENT
            colors = intArrayOf((accent and 0x00FFFFFF) or (0x55 shl 24), (accent and 0x00FFFFFF) or (0x18 shl 24), 0)
            gradientRadius = a.resources.displayMetrics.widthPixels * 0.42f
            setGradientCenter(0.72f, 0.3f)
        }
        v.findViewById<View>(R.id.heroGlow).background = glow
        val clear = bg and 0x00FFFFFF
        v.findViewById<View>(R.id.heroShade).background = LayerDrawable(arrayOf(
            GradientDrawable(GradientDrawable.Orientation.LEFT_RIGHT, intArrayOf(bg, (bg and 0x00FFFFFF) or (0xAA shl 24), clear)),
            GradientDrawable(GradientDrawable.Orientation.BOTTOM_TOP, intArrayOf(bg, clear, clear)),
        ))
        val h = H(v)
        h.watch.setOnClickListener { slides.getOrNull(index)?.watch?.invoke() }
        h.second.setOnClickListener { slides.getOrNull(index)?.second?.second?.invoke() }
        h.next.setOnClickListener { if (slides.isNotEmpty()) { index = (index + 1) % slides.size; bind(h); noteKey() } }
        return h
    }

    override fun onBindViewHolder(h: H, position: Int) { holder = h; bind(h) }

    private fun bind(h: H) {
        val s = slides.getOrNull(index) ?: return
        h.kicker.text = s.kicker
        h.title.text = s.title
        val t = s.title_
        TitleLogos.show(h.logo, h.title, t?.let { TitleLogos.cached(it.first, it.second) })
        // The background picture TMDB has for the programme or film (wide, so better than a poster); otherwise its own.
        val needArt = t != null
        if (t != null && TitleLogos.enabled && (TitleLogos.cached(t.first, t.second) == null || (needArt && !TitleLogos.knowsBackdrop(t.first, t.second)))) scope?.launch {
            val url = TitleLogos.find(t.first, t.second, Vod.cachedDetails(t.first, t.second.id)?.tmdbId)
            if (slides.getOrNull(index) !== s) return@launch
            if (url != null) TitleLogos.show(h.logo, h.title, url)
            if (needArt) TitleLogos.cachedBackdrop(t.first, t.second)?.let { Posters.load(h.art, it, artH) }
        }
        h.meta.text = s.meta
        h.text.text = s.text ?: ""
        h.text.visibility = if (s.text.isNullOrBlank()) View.GONE else View.VISIBLE
        h.progress.visibility = if (s.progress >= 0) View.VISIBLE else View.GONE
        if (s.progress >= 0) h.progress.progress = (s.progress * 1000).toInt()
        val bg = t?.let { TitleLogos.cachedBackdrop(it.first, it.second) } ?: s.art
        if (bg != null) Posters.load(h.art, bg, artH) else { h.art.setImageDrawable(null); h.art.tag = null }
        h.second.visibility = if (s.second != null) View.VISIBLE else View.GONE
        h.second.text = s.second?.first ?: ""
        h.next.visibility = if (slides.size > 1) View.VISIBLE else View.GONE
        // Dots: the current slide is a wider bar.
        h.dots.removeAllViews()
        if (slides.size > 1) for (i in slides.indices) {
            val d = a.resources.displayMetrics.density
            val dot = View(a)
            val lp = LinearLayout.LayoutParams(((if (i == index) 16 else 6) * d).toInt(), (6 * d).toInt())
            lp.marginStart = (3 * d).toInt(); lp.marginEnd = (3 * d).toInt()
            dot.layoutParams = lp
            dot.background = GradientDrawable().apply { cornerRadius = 3 * d; setColor(if (i == index) 0xFFFFFFFF.toInt() else 0x66FFFFFF) }
            h.dots.addView(dot)
        }
        Ui.test("home-feature ${index + 1}/${slides.size} ${s.title}")
    }
}
