package app.dingus.tv

import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import android.graphics.Typeface
import android.text.TextPaint
import android.text.TextUtils
import android.util.AttributeSet
import android.view.KeyEvent
import android.view.View
import androidx.core.content.ContextCompat

/**
 * The TV guide grid: channels down the left, programmes across a timeline. Drawn by hand (no child views),
 * so it stays smooth on a Fire Stick even when the remote's button is held down.
 *
 * Up/down move between channels (keeping the same time), left/right between programmes, OK plays.
 * Left on the programme that's on now asks for the categories menu.
 */
class GuideGrid(ctx: Context, attrs: AttributeSet?) : View(ctx, attrs) {
    interface Listener {
        fun onSelected(c: Channel, p: Programme?)
        fun onPlay(c: Channel, p: Programme?)
        fun onLeftEdge()
        fun onMenu(c: Channel, p: Programme?)
    }

    var listener: Listener? = null
    /** Marks programmes with a reminder (a dot in the corner). */
    private var reminder: ((Programme) -> Boolean)? = null
    /** Channel numbers beside the logos (Settings). */
    var showNumbers = true
        set(v) { if (field != v) { field = v; lines.clear(); invalidate() } }

    /** A channel name split over (at most) two lines to fit [width]; worked out once per name. */
    private val lines = HashMap<String, Pair<String, String?>>()
    /** Shortened texts ("Programme 2 on…"), kept so scrolling the guide doesn't redo them every frame. */
    private val fitted = android.util.LruCache<String, String>(800)
    private fun fit(text: String, paint: TextPaint, avail: Float, tag: Char): String {
        val key = "$tag${avail.toInt()}|$text"
        fitted.get(key)?.let { return it }
        val out = if (paint.measureText(text) <= avail) text else TextUtils.ellipsize(text, paint, avail, TextUtils.TruncateAt.END).toString()
        fitted.put(key, out)
        return out
    }

    private fun twoLines(name: String, width: Float, bold: Boolean): Pair<String, String?> {
        val key = "$bold|${width.toInt()}|$name"
        lines[key]?.let { return it }
        val result = if (nameP.measureText(name) <= width) name to null else {
            val words = name.split(' ')
            var first = ""
            var i = 0
            while (i < words.size) {
                val tryFirst = if (first.isEmpty()) words[i] else "$first ${words[i]}"
                if (nameP.measureText(tryFirst) > width) break
                first = tryFirst; i++
            }
            if (first.isEmpty()) { // one very long word: cut it where it fits
                var n = name.length
                while (n > 1 && nameP.measureText(name, 0, n) > width) n--
                name.substring(0, n) to TextUtils.ellipsize(name.substring(n), nameP, width, TextUtils.TruncateAt.END).toString()
            } else first to TextUtils.ellipsize(words.drop(i).joinToString(" "), nameP, width, TextUtils.TruncateAt.END).toString()
        }
        if (lines.size > 2000) lines.clear()
        lines[key] = result
        return result
    }

    /** Colour stripe on programmes by genre (Settings). */
    var genreColours = true
        set(v) { field = v; invalidate() }
    var title: String = ""
        set(v) { field = v; invalidate() }

    private val d = resources.displayMetrics.density
    private val chanW = 224 * d
    private val rowH = 46 * d // (smaller text: more channels on screen)
    private val headerH = 34 * d
    private val gap = 2 * d
    private val pad = 10 * d
    private val spanMin = 120 // minutes across the screen
    private var pxPerMs = 1f

    private var channels: List<Channel> = emptyList()
    private var row = 0
    private var top = 0
    private var sel: Programme? = null
    private var selIdx = -1
    private var anchor = 0L       // the time the selection follows when moving up and down
    private var start = 0L        // time at the left edge

    private fun col(attr: Int) = Themes.color(context, attr)
    private val cBg = col(R.attr.dBg)
    private val cSurface = col(R.attr.dSurface)
    private val cSurface2 = col(R.attr.dSurface2)
    private val cLine = col(R.attr.dLine)
    private val cText = col(R.attr.dText)
    private val cMuted = col(R.attr.dMuted)
    private val cAccent = col(R.attr.dAccent)
    private val cAccentSoft = col(R.attr.dAccentSoft)
    private val cLive = ContextCompat.getColor(context, R.color.live)
    private val cAiring = col(R.attr.dAiring)
    private val cPast = col(R.attr.dPast)

    private val fill = Paint(Paint.ANTI_ALIAS_FLAG)
    private val titleP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 14 * d; typeface = Typeface.DEFAULT_BOLD; color = cText }
    private val subP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 11.5f * d; color = cMuted }
    private val numP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 12 * d; color = cMuted; textAlign = Paint.Align.RIGHT }
    private val nameP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 13 * d; color = cText }
    private val headP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 14 * d; color = cMuted }
    private val headBoldP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 15 * d; color = cText; typeface = Typeface.DEFAULT_BOLD }
    private val starP = TextPaint(Paint.ANTI_ALIAS_FLAG).apply { textSize = 12 * d; color = 0xFFFBBF24.toInt() }
    private val r = RectF()
    private val tri = Path()

    init {
        isFocusable = true
        isFocusableInTouchMode = true
        setBackgroundColor(cBg)
    }

    private fun now() = System.currentTimeMillis()
    private fun floor30(t: Long) = t - t % (30 * 60_000L)
    private fun xOf(t: Long) = chanW + (t - start) * pxPerMs
    private val visibleRows get() = ((height - headerH) / rowH).toInt().coerceAtLeast(1)
    val current: Channel? get() = channels.getOrNull(row)

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        pxPerMs = (w - chanW) / (spanMin * 60_000f)
        keepRowVisible()
    }

    /** Show a category's channels, highlighting [focusId] if it's there. */
    fun setChannels(list: List<Channel>, focusId: String?) {
        channels = list
        row = list.indexOfFirst { it.id == focusId }.coerceAtLeast(0)
        start = floor30(now())
        anchor = now()
        top = 0
        keepRowVisible(centre = true)
        pick()
    }

    /** Highlight a channel in the current list (e.g. the one you were just watching). */
    fun focusChannel(id: String) {
        val i = channels.indexOfFirst { it.id == id }
        if (i < 0 || i == row) return
        row = i
        anchor = maxOf(anchor, now())
        keepRowVisible(centre = true)
        pick()
    }

    /** Called every minute (and when the guide's been read again): move on with the clock. */
    fun refresh() {
        // Move on with the clock – unless you're looking back at earlier programmes (catch-up).
        val past = sel?.let { it.stop <= now() } == true
        if (!past) {
            val minStart = floor30(now())
            if (start < minStart) start = minStart
            if (anchor < now()) anchor = now()
        }
        val s = sel
        if (s == null || (!past && s.stop <= now()) || selIdx < 0) pick() else {
            // The guide may have been replaced: find the same programme again.
            val list = current?.let { Repo.programmes(it) } ?: emptyArray()
            if (list.getOrNull(selIdx) !== s) pick()
        }
        invalidate()
    }

    private fun pick() {
        val c = current
        if (c == null) { sel = null; selIdx = -1; invalidate(); return }
        val list = Repo.programmes(c)
        selIdx = indexAt(list, anchor)
        sel = list.getOrNull(selIdx)
        sel?.let { ensureVisible(it) }
        invalidate()
        listener?.onSelected(c, sel)
    }

    /** The programme on at [t], or the next one after it. */
    private fun indexAt(list: Array<Programme>, t: Long): Int {
        var lo = 0; var hi = list.size - 1; var found = -1
        while (lo <= hi) {
            val mid = (lo + hi) ushr 1
            if (list[mid].stop <= t) lo = mid + 1 else { found = mid; hi = mid - 1 }
        }
        return found
    }

    private fun ensureVisible(p: Programme) {
        val end = start + spanMin * 60_000L
        if (p.start >= end - 30 * 60_000L) start = floor30(p.start) - 30 * 60_000L
        else if (p.start < start) start = if (p.stop <= now()) floor30(p.start) else maxOf(floor30(now()), floor30(p.start))
        if (end <= start) start = floor30(now())
    }

    private fun keepRowVisible(centre: Boolean = false) {
        val n = visibleRows
        if (centre) top = row - n / 2
        if (row < top + 1) top = row - 1
        if (row > top + n - 2) top = row - n + 2
        top = top.coerceIn(0, maxOf(0, channels.size - n))
    }

    private fun moveRow(by: Int) {
        if (channels.isEmpty()) return
        val n = (row + by).coerceIn(0, channels.size - 1)
        if (n == row) return
        row = n
        keepRowVisible()
        pick()
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val c = current
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_DOWN -> { moveRow(1); return true }
            KeyEvent.KEYCODE_DPAD_UP -> { moveRow(-1); return true }
            KeyEvent.KEYCODE_PAGE_DOWN, KeyEvent.KEYCODE_CHANNEL_DOWN, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> { moveRow(visibleRows - 1); return true }
            KeyEvent.KEYCODE_PAGE_UP, KeyEvent.KEYCODE_CHANNEL_UP, KeyEvent.KEYCODE_MEDIA_REWIND -> { moveRow(-(visibleRows - 1)); return true }
            KeyEvent.KEYCODE_DPAD_RIGHT -> {
                if (c == null) return true
                val list = Repo.programmes(c)
                val next = list.getOrNull(selIdx + 1)
                if (sel != null && next != null) {
                    selIdx++; sel = next; anchor = next.start
                    ensureVisible(next); invalidate(); listener?.onSelected(c, next)
                }
                return true
            }
            KeyEvent.KEYCODE_DPAD_LEFT -> {
                if (c == null) { listener?.onLeftEdge(); return true }
                val list = Repo.programmes(c)
                val prev = list.getOrNull(selIdx - 1)
                // From what's on now, LEFT shows the categories (past programmes are under ≡ → Catch up). Already
                // back in the past (from there), LEFT keeps going back as far as catch-up allows.
                val inPast = (sel?.stop ?: Long.MAX_VALUE) <= now()
                if (sel == null || prev == null || (prev.stop <= now() && !(inPast && Repo.canCatchUp(c, prev)))) { listener?.onLeftEdge(); return true }
                selIdx--; sel = prev; anchor = if (prev.stop <= now()) prev.start else maxOf(prev.start, now())
                ensureVisible(prev); invalidate(); listener?.onSelected(c, prev)
                return true
            }
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> {
                c?.let { listener?.onPlay(it, sel) }; return true
            }
            KeyEvent.KEYCODE_MENU -> { if (event.repeatCount == 0) c?.let { listener?.onMenu(it, sel) }; return true }
        }
        return super.onKeyDown(keyCode, event)
    }

    // ------------------------------------------------------------------ drawing

    override fun onDraw(canvas: Canvas) {
        val w = width.toFloat()
        val now = now()
        val end = start + spanMin * 60_000L

        // Header: category name over the channels, times across the top.
        canvas.drawText(TextUtils.ellipsize(title, headBoldP, chanW - 2 * pad, TextUtils.TruncateAt.END).toString(),
            pad, headerH - 11 * d, headBoldP)
        var t = start
        while (t < end) {
            val x = xOf(t)
            fill.color = cLine
            canvas.drawRect(x, headerH - 10 * d, x + d, headerH - 2 * d, fill)
            canvas.drawText(Ui.time(t), x + 6 * d, headerH - 11 * d, headP)
            t += 30 * 60_000L
        }

        val n = visibleRows + 1
        for (i in top until minOf(channels.size, top + n)) {
            val y = headerH + (i - top) * rowH
            drawRow(canvas, channels[i], i == row, y, w, now, end)
        }

        // The "now" line.
        val nx = xOf(now)
        if (nx >= chanW) {
            fill.color = cLive
            canvas.drawRect(nx - d, headerH - 4 * d, nx + d, height.toFloat(), fill)
            tri.reset(); tri.moveTo(nx - 6 * d, headerH - 10 * d); tri.lineTo(nx + 6 * d, headerH - 10 * d); tri.lineTo(nx, headerH - 3 * d); tri.close()
            canvas.drawPath(tri, fill)
        }
    }

    private fun drawRow(canvas: Canvas, c: Channel, selected: Boolean, y: Float, w: Float, now: Long, end: Long) {
        val y0 = y + gap; val y1 = y + rowH - gap

        // Channel cell: number, logo, name.
        fill.color = if (selected) cAccentSoft else cSurface
        r.set(0f, y0, chanW - gap * 2, y1)
        canvas.drawRoundRect(r, 6 * d, 6 * d, fill)
        val mid = (y0 + y1) / 2
        if (showNumbers && c.number > 0) canvas.drawText(c.number.toString(), 38 * d, mid + 4 * d, numP)
        val logoX = if (showNumbers) 46 * d else 8 * d; val logoW = 60 * d; val logoH = rowH - 14 * d
        val bmp = Logos.get(c.logo, logoH.toInt()) { invalidate() }
        if (bmp != null) {
            val scale = minOf(logoW / bmp.width, logoH / bmp.height)
            val bw = bmp.width * scale; val bh = bmp.height * scale
            r.set(logoX + (logoW - bw) / 2, mid - bh / 2, logoX + (logoW + bw) / 2, mid + bh / 2)
            canvas.drawBitmap(bmp, null, r, null)
        } else {
            fill.color = cSurface2
            r.set(logoX, mid - logoH / 2, logoX + logoW, mid + logoH / 2)
            canvas.drawRoundRect(r, 4 * d, 4 * d, fill)
            subP.textAlign = Paint.Align.CENTER
            canvas.drawText(Ui.initials(c.name), logoX + logoW / 2, mid + 4 * d, subP)
            subP.textAlign = Paint.Align.LEFT
        }
        val fav = Store.isFavourite(c.id)
        val nameX = logoX + logoW + 10 * d
        val nameW = chanW - nameX - pad - (if (fav) 16 * d else 0f)
        nameP.typeface = if (selected) Typeface.DEFAULT_BOLD else Typeface.DEFAULT
        // Names get two lines, so long ones aren't cut short.
        val (l1, l2) = twoLines(c.name, nameW, selected)
        if (l2 == null) canvas.drawText(l1, nameX, mid + 4 * d, nameP)
        else { canvas.drawText(l1, nameX, mid - 3 * d, nameP); canvas.drawText(l2, nameX, mid + 12 * d, nameP) }
        if (fav) canvas.drawText("★", chanW - pad - 12 * d, mid + 4 * d, starP)

        // Programmes.
        val list = Repo.programmes(c)
        val rs = Reminders.all()
        reminder = if (rs.isEmpty() || rs.none { it.channelId == c.id }) null else { p -> rs.any { it.channelId == c.id && it.start == p.start } }
        var i = indexAt(list, start)
        if (i < 0 || list[i].start >= end) {
            block(canvas, chanW, w, y0, y1, if (list.isEmpty()) "No information" else "", null, selected && sel == null, false)
            return
        }
        // Gap before the first programme in view.
        if (list[i].start > start) block(canvas, chanW, xOf(list[i].start), y0, y1, "", null, false, false)
        while (i < list.size && list[i].start < end) {
            val p = list[i]
            val x0 = maxOf(chanW, xOf(p.start)); val x1 = minOf(w, xOf(p.stop))
            block(canvas, x0, x1, y0, y1, p.title, p, selected && p === sel, p.start <= now && now < p.stop,
                past = p.stop <= now, catchUp = p.stop <= now && Repo.canCatchUp(c, p, now))
            i++
        }
    }

    private fun block(canvas: Canvas, x0: Float, x1: Float, y0: Float, y1: Float, text: String, p: Programme?, on: Boolean, airing: Boolean,
                      past: Boolean = false, catchUp: Boolean = false) {
        if (x1 - x0 < 3 * d) return
        fill.color = when { on -> cAccent; airing -> cAiring; past -> cPast; else -> cSurface }
        r.set(x0 + gap, y0, x1 - gap, y1)
        canvas.drawRoundRect(r, 6 * d, 6 * d, fill)
        if (genreColours && p != null && p.genre != Genres.NONE && x1 - x0 > 12 * d) {
            fill.color = Genres.color(p.genre)
            if (past && !on) fill.alpha = 110
            r.set(x0 + gap + 4 * d, y1 - 5 * d, x1 - gap - 4 * d, y1 - 3 * d)
            canvas.drawRoundRect(r, d, d, fill)
            fill.alpha = 255
        }
        val avail = x1 - x0 - 2 * pad
        if (avail < 12 * d || text.isEmpty()) return
        titleP.color = if (past && !on && !catchUp) cMuted else cText
        if (p == null) {
            canvas.drawText(fit(text, subP, avail, 's'), x0 + pad, (y0 + y1) / 2 + 4 * d, subP)
            return
        }
        val bell = reminder?.invoke(p) == true
        canvas.drawText(fit(text, titleP, avail - if (bell) 14 * d else 0f, 't'), x0 + pad, y0 + 18 * d, titleP)
        if (bell) { fill.color = if (on) cText else cAccent; canvas.drawCircle(x1 - gap - 10 * d, y0 + 12 * d, 4 * d, fill) }
        subP.color = if (on) 0xDDFFFFFF.toInt() else cMuted
        canvas.drawText(fit((if (catchUp) "↺  " else "") + Ui.span(p), subP, avail, 's'), x0 + pad, y0 + 34 * d, subP)
        subP.color = cMuted
    }
}
