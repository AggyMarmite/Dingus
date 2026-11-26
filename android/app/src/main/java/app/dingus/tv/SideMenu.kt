package app.dingus.tv

import android.animation.ValueAnimator
import android.app.Activity
import android.content.Intent
import android.graphics.Rect
import android.view.KeyEvent
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.view.animation.DecelerateInterpolator
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.TextView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** Work that carries on whichever screen is showing (updating the playlist, checking the subscription). */
object Background {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
}

/** Updating the playlist and the TV guide from anywhere (the menu's "Refresh playlist"), with the notes. */
object PlaylistUpdate {
    @Volatile var running = false; private set

    /** Set after switching playlist: the next screen asks whether to update it now. */
    @Volatile var askAfterSwitch = false

    /** "Update “name” now?" – after switching to a playlist that was loaded before (a new one loads by itself). */
    fun ask(a: Activity) {
        askAfterSwitch = false
        val p = Store.playlist ?: return
        if (!Repo.hasSaved()) return
        Dialogs.builder(a).setTitle("Update “${p.name}”?")
            .setMessage("Get the latest channels and TV guide for this playlist now? (Otherwise it updates by itself when it's due.)")
            .setPositiveButton("Update now") { _, _ -> start(); Ui.test("switch-update yes") }
            .setNegativeButton("Not now") { _, _ -> Ui.test("switch-update no") }
            .show()
        Ui.test("switch-update asked")
    }

    fun start() {
        val p = Store.playlist ?: return
        if (running) return
        running = true
        Background.scope.launch {
            try {
                Notice.working("Updating playlist & TV guide…")
                Vod.refreshInBackground() // films and shows download alongside, not afterwards
                Repo.refreshChannels(p)
                try { Repo.refreshGuide(); Notice.updateDone("Playlist & TV guide updated") }
                catch (e: Exception) { Notice.updateDone("Playlist updated  ·  TV guide: ${Net.friendly(e)}", 6_000) }
            } catch (e: Exception) {
                Notice.updateDone("Couldn't update the playlist: ${Net.friendly(e)}", 6_000)
            } finally { running = false }
        }
    }
}

/**
 * The menu on every screen, down the left. Closed, it's a strip of icons (the screen you're on marked); LEFT at
 * the left edge of a screen stretches the same panel open – the icons stay where they are and their names, the
 * playlist and the subscription fade in – and RIGHT (or BACK) slides it shut again.
 *
 * [content] is moved across to make room for the strip.
 */
class SideMenu(
    private val a: Activity,
    private val here: Page,
    content: View,
    /** LEFT at the left edge of the screen opens the menu (Live TV opens it itself, from the categories). */
    private val leftOpens: Boolean = true,
    /** Move [content] across for the menu (Home doesn't: its picture goes right under the floating buttons). */
    pad: Boolean = true,
) {
    enum class Page { HOME, LIVE, FILMS, SHOWS, SEARCH, SETTINGS }

    private val root = a.findViewById<FrameLayout>(android.R.id.content)
    private val d = a.resources.displayMetrics.density
    private val strip = (52 * d).toInt() // (slim: the round buttons are 34dp)
    private val full = (200 * d).toInt()
    /** Darkens the left of the screen behind the open menu (fading out across it); the rest stays bright and live. */
    private val scrim = View(a).apply {
        background = android.graphics.drawable.GradientDrawable(android.graphics.drawable.GradientDrawable.Orientation.LEFT_RIGHT,
            intArrayOf(0xF806080C.toInt(), 0xF606080C.toInt(), 0xF206080C.toInt(), 0xB306080C.toInt(), 0x4006080C, 0x0D06080C, 0))
        alpha = 0f; visibility = View.GONE
    }
    val panel: View = LayoutInflater.from(a).inflate(R.layout.side_menu, root, false)
    private val edge: View = panel.findViewById(R.id.sideEdge)
    private val fading = listOf<View>(panel.findViewById(R.id.sideName), panel.findViewById(R.id.sideHeader), panel.findViewById(R.id.status), panel.findViewById(R.id.sideVpnPlace))
    private val clock: TextView = panel.findViewById(R.id.sideClock)
    /** The time under the logo (every minute, on the minute). */
    private val tick = object : Runnable {
        override fun run() {
            clock.text = Ui.time(System.currentTimeMillis())
            clock.postDelayed(this, 60_000L - System.currentTimeMillis() % 60_000L + 50)
        }
    }
    private val rows: List<TextView> = listOf(R.id.navHome, R.id.navGuide, R.id.navFilms, R.id.navShows, R.id.navSearch, R.id.settings, R.id.navRefresh)
        .map { panel.findViewById<TextView>(it) }
    private val playlistRow: TextView = panel.findViewById(R.id.navPlaylist)
    private val accountRow: TextView = panel.findViewById(R.id.navAccount)
    private var fraction = 0f
    private var anim: ValueAnimator? = null
    private var returnTo: View? = null
    private val content = content
    /** The page behind sinks back a little as the menu opens (not Live TV's: a video picture can't be scaled smoothly). */
    private val sink = here != Page.LIVE

    var isOpen = false; private set
    /** Called once the menu has closed by itself (RIGHT / BACK), for screens that put the focus somewhere special. */
    var onClosed: (() -> Unit)? = null
    /** Called when something in the menu is chosen (Live TV: back to the guide itself, categories closed). */
    var onNavigate: ((Page) -> Unit)? = null

    init {
        if (pad) content.setPaddingRelative(content.paddingStart + strip, content.paddingTop, content.paddingEnd, content.paddingBottom)
        panel.background = null
        root.addView(scrim, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(panel, FrameLayout.LayoutParams(full, ViewGroup.LayoutParams.MATCH_PARENT))
        panel.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> apply() }

        val size = (22 * d).toInt()
        val colour = Themes.color(a, R.attr.dText)
        for (t in rows) t.compoundDrawablesRelative[0]?.mutate()?.let { dr ->
            dr.setTint(colour); dr.setBounds(0, 0, size, size); t.setCompoundDrawablesRelative(dr, null, null, null)
        }
        rowFor(here).isActivated = true
        for (t in rows) {
            // (A background can bring its own padding; keep the row's own.)
            val pad = intArrayOf(t.paddingStart, t.paddingTop, t.paddingEnd, t.paddingBottom)
            t.background = Pill(t)
            // (Names a touch bolder, so they read clearly over the page.)
            if (android.os.Build.VERSION.SDK_INT >= 28) t.typeface = android.graphics.Typeface.create(t.typeface, 500, false)
            t.setPaddingRelative(pad[0], pad[1], pad[2], pad[3])
            t.setOnFocusChangeListener { v, has ->
                tintRows()
                // The highlighted pill lifts with the same soft bounce as the cards and buttons – growing mostly to the
                // right: its left end is only 4dp from the screen's edge (growing from the middle pushed it off the
                // screen), its right end 8dp from the panel's.
                v.pivotX = v.width * 0.3f; v.pivotY = v.height / 2f
                if (isOpen || !has) Motion.lift(v, has && isOpen, 1.05f, shine = false)
            }
        }

        panel.findViewById<View>(R.id.navHome).setOnClickListener { go(Page.HOME) }
        panel.findViewById<View>(R.id.navGuide).setOnClickListener { go(Page.LIVE) }
        panel.findViewById<View>(R.id.navFilms).setOnClickListener { go(Page.FILMS) }
        panel.findViewById<View>(R.id.navShows).setOnClickListener { go(Page.SHOWS) }
        panel.findViewById<View>(R.id.navSearch).setOnClickListener { go(Page.SEARCH) }
        panel.findViewById<View>(R.id.settings).setOnClickListener { go(Page.SETTINGS) }
        panel.findViewById<View>(R.id.navRefresh).setOnClickListener { close(); PlaylistUpdate.start(); Ui.test("menu-refresh") }
        playlistRow.setOnClickListener { switchPlaylist() }
        showDetails()
        apply()
        clock.addOnAttachStateChangeListener(object : View.OnAttachStateChangeListener {
            override fun onViewAttachedToWindow(v: View) { clock.removeCallbacks(tick); tick.run() }
            override fun onViewDetachedFromWindow(v: View) { clock.removeCallbacks(tick) }
        })
        panel.findViewById<TextView>(R.id.sideVpn)?.let { VpnIndicator.bind(it); showVpnPlace(it) }
    }

    private fun rowFor(p: Page): TextView = rows[p.ordinal]

    // ------------------------------------------------------------------ the buttons and pills
    // Closed: light round buttons floating on the left (the screen you're on in a soft white). Opening: each one
    // stretches into a pill and its name fades in, with no panel behind – just the left of the screen darkening.
    // Open: the highlighted pill is white; the screen you're on keeps its soft white.

    private inner class Pill(private val v: TextView) : android.graphics.drawable.Drawable() {
        private val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
        private val edgePaint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG).apply {
            style = android.graphics.Paint.Style.STROKE; strokeWidth = d; color = 0x1FFFFFFF
        }
        private val r = android.graphics.RectF()
        override fun draw(c: android.graphics.Canvas) {
            val p = fraction
            val b = bounds
            val inset = 5 * d * (1 - p)
            val w = 34 * d + (b.width() - 34 * d) * p
            r.set(b.left + inset, b.top + inset, b.left + inset + w, b.bottom - inset)
            val rad = r.height() / 2
            val focused = v.isFocused && isOpen
            // Closed: small round buttons, three-quarters see-through so the page shows through. Open: the list goes calm – no boxes, just icons and names – with a
            // soft white pill for the screen you're on and a white one on the highlight.
            paint.color = when {
                focused -> ((0.35f + 0.65f * p) * 255).toInt() shl 24 or 0xFFFFFF
                v.isActivated -> ((0.18f - 0.04f * p) * 255).toInt() shl 24 or 0xFFFFFF
                else -> ((0.25f * (1 - p)) * 255).toInt() shl 24 or 0x1E2636
            }
            if (paint.alpha > 0) c.drawRoundRect(r, rad, rad, paint)
            if (!focused && !v.isActivated && p < 1f) { edgePaint.alpha = (0x14 * (1 - p)).toInt(); c.drawRoundRect(r, rad, rad, edgePaint) }
        }
        override fun isStateful() = true
        override fun onStateChange(state: IntArray): Boolean { invalidateSelf(); return true }
        override fun setAlpha(alpha: Int) {}
        override fun setColorFilter(cf: android.graphics.ColorFilter?) {}
        @Deprecated("Deprecated in Java")
        override fun getOpacity() = android.graphics.PixelFormat.TRANSLUCENT
    }

    /** How far each name has come in: one after another as the menu opens. */
    private fun labelIn(i: Int) = ((fraction - 0.2f - i * 0.05f) / 0.5f).coerceIn(0f, 1f)

    /** The names and icons: dark on the white highlight, light otherwise; the names slide in one after another. */
    private fun tintRows() {
        val light = Themes.color(a, R.attr.dText)
        for ((i, t) in rows.withIndex()) {
            val dark = t.isFocused && isOpen
            val labels = labelIn(i)
            val c = if (dark) 0xFF0B0E14.toInt() else light
            t.setTextColor((c and 0x00FFFFFF) or ((labels * 255).toInt() shl 24))
            t.compoundDrawablesRelative[0]?.setTint(if (dark) 0xFF0B0E14.toInt() else 0xFFFFFFFF.toInt())
            t.background?.invalidateSelf()
        }
    }

    /** How far open (0 = the strip, 1 = open): the panel's shown width, the names, the dimming behind. */
    private fun apply() {
        val w = strip + ((full - strip) * fraction).toInt()
        val h = panel.height.takeIf { it > 0 } ?: a.resources.displayMetrics.heightPixels
        panel.clipBounds = Rect(0, 0, w, h)
        edge.translationX = (w - 1).toFloat()
        val labels = ((fraction - 0.2f) / 0.6f).coerceIn(0f, 1f)
        // The name, the playlist and the VPN's place glide in at the top.
        for (v in fading) { v.alpha = labels; v.translationX = (1 - labels) * -10 * d }
        tintRows()
        edge.alpha = 0f
        scrim.alpha = fraction
        scrim.visibility = if (fraction > 0f) View.VISIBLE else View.GONE
        if (sink && !Ui.lite) {
            val s = 1f - 0.03f * fraction
            if (content.width > 0) { content.pivotX = content.width * 0.75f; content.pivotY = content.height / 2f }
            content.scaleX = s; content.scaleY = s
        }
    }

    private fun animateTo(target: Float, ms: Long, end: (() -> Unit)? = null) {
        anim?.cancel()
        if (Ui.lite) { fraction = target; apply(); end?.invoke(); return } // lite mode: open and shut at once
        if (sink) content.setLayerType(View.LAYER_TYPE_HARDWARE, null)
        anim = ValueAnimator.ofFloat(fraction, target).apply {
            duration = ms
            interpolator = Motion.glide
            addUpdateListener { fraction = it.animatedValue as Float; apply() }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                var cancelled = false
                override fun onAnimationCancel(animation: android.animation.Animator) { cancelled = true }
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    if (sink && fraction == 0f) content.setLayerType(View.LAYER_TYPE_NONE, null)
                    if (!cancelled) end?.invoke()
                }
            })
            start()
        }
    }

    /** The playlist's name and the subscription; asks the provider again now and then (and after switching). */
    private fun showDetails() {
        val vod = if (Vod.available) View.VISIBLE else View.GONE
        rowFor(Page.FILMS).visibility = vod
        rowFor(Page.SHOWS).visibility = vod
        playlistRow.tag = Store.playlists.size > 1
        val p = Store.playlist
        showAccount()
        // Asked again whenever the menu opens (at most every 5 seconds): connections in use change while you watch.
        if (p != null && System.currentTimeMillis() - Account.lastCheck > 5_000L && !checking) {
            checking = true
            Background.scope.launch {
                try { Account.check(p) } finally { checking = false }
                if (Store.playlist?.id == p.id) showAccount()
            }
        }
    }

    /** The playlist's name with "● Online" in green beside it (red "● Offline"), then expiry and "Active connections 1/2". */
    private fun showAccount() {
        val p = Store.playlist
        val name = android.text.SpannableStringBuilder((p?.name ?: "") + if (Store.playlists.size > 1) "  ▾" else "")
        when (Account.online(p)) {
            true -> "  ● Online" to 0xFF4ADE80.toInt()
            false -> "  ● Offline" to 0xFFF87171.toInt()
            null -> null
        }?.let { (t, c) -> val at = name.length; name.append(t); name.setSpan(android.text.style.ForegroundColorSpan(c), at, name.length, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE) }
        playlistRow.text = name
        if (p == null) { accountRow.text = ""; return }
        val sb = android.text.SpannableStringBuilder()
        for ((line, colour) in Account.lines(Account.saved(), p).filter { !it.first.startsWith("Status") }) {
            if (sb.isNotEmpty()) sb.append('\n')
            val start = sb.length
            sb.append(line)
            if (colour != 0) sb.setSpan(android.text.style.ForegroundColorSpan(colour), start + maxOf(0, line.indexOf('●')), sb.length, android.text.Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        }
        accountRow.text = sb
    }

    /** While the menu's open, the connections and status keep up (asked again every 5 seconds). */
    private val poll = object : Runnable {
        override fun run() { if (isOpen) { showDetails(); panel.postDelayed(this, 5_000L) } }
    }

    fun open() {
        if (isOpen) return
        isOpen = true
        panel.removeCallbacks(poll); panel.postDelayed(poll, 20_500L)
        returnTo = a.currentFocus
        showDetails()
        for (t in rows) t.isFocusable = t.visibility == View.VISIBLE
        playlistRow.isFocusable = playlistRow.tag == true
        rowFor(here).requestFocus()
        animateTo(1f, 420)
        Ui.test("menu-open")
    }

    /** [restore]: put the focus back where it was before the menu opened. */
    fun close(restore: Boolean = true) {
        if (!isOpen) return
        isOpen = false
        panel.removeCallbacks(poll)
        val back = returnTo?.takeIf { restore && it.isAttachedToWindow && it.isShown }
        back?.requestFocus()
        for (t in rows) t.isFocusable = false
        playlistRow.isFocusable = false
        returnTo = null
        animateTo(0f, 300)
        Ui.test("menu-closed")
        if (restore && back == null) onClosed?.invoke()
    }

    /** Full screen video: no strip. */
    fun setHidden(hidden: Boolean) {
        if (hidden) close(restore = false)
        panel.visibility = if (hidden) View.GONE else View.VISIBLE
        if (hidden) scrim.visibility = View.GONE else apply()
    }

    fun refresh() { if (isOpen) showDetails() else { val vod = if (Vod.available) View.VISIBLE else View.GONE; rowFor(Page.FILMS).visibility = vod; rowFor(Page.SHOWS).visibility = vod } }

    /** From the screen's dispatchKeyEvent: true when the menu took the key. */
    fun onKey(e: KeyEvent): Boolean {
        if (panel.visibility != View.VISIBLE) return false
        if (isOpen) {
            when (e.keyCode) {
                KeyEvent.KEYCODE_DPAD_RIGHT -> { if (e.action == KeyEvent.ACTION_DOWN) close(); return true }
                KeyEvent.KEYCODE_DPAD_LEFT -> return true
                KeyEvent.KEYCODE_BACK -> {
                    // Home and Live TV: BACK carries on stepping out (leaving the app / back to Home). Elsewhere it closes.
                    if (here == Page.HOME || here == Page.LIVE) return false
                    if (e.action == KeyEvent.ACTION_UP) close()
                    return true
                }
            }
            if (e.action == KeyEvent.ACTION_DOWN && !inside(a.currentFocus)) { rowFor(here).requestFocus(); return true }
            // Up and down step through the menu's own rows, in order (worked out here rather than by Android's
            // nearest-view search, which some screens – Search – steered off into the page behind).
            if (e.keyCode == KeyEvent.KEYCODE_DPAD_UP || e.keyCode == KeyEvent.KEYCODE_DPAD_DOWN) {
                if (e.action == KeyEvent.ACTION_DOWN) {
                    val order = (listOf(playlistRow) + rows).filter { it.isFocusable && it.visibility == View.VISIBLE }
                    val i = order.indexOf(a.currentFocus)
                    val j = if (i < 0) order.indexOf(rowFor(here)) else i + if (e.keyCode == KeyEvent.KEYCODE_DPAD_UP) -1 else 1
                    order.getOrNull(j)?.let { it.requestFocus(); Ui.test("menu-row ${it.text}") }
                }
                return true
            }
            return false
        }
        if (leftOpens && e.action == KeyEvent.ACTION_DOWN && e.keyCode == KeyEvent.KEYCODE_DPAD_LEFT && atLeftEdge()) { open(); return true }
        return false
    }

    private fun inside(v: View?): Boolean {
        var p: Any? = v
        while (p is View) { if (p === panel) return true; p = p.parent }
        return false
    }

    /** Nothing further left to go to from where the focus is. */
    private fun atLeftEdge(): Boolean {
        val f = a.currentFocus ?: return true
        if (f is EditText && f.selectionStart > 0) return false
        val next = f.focusSearch(View.FOCUS_LEFT) ?: return true
        if (next === f || inside(next)) return true
        val r1 = Rect(); val r2 = Rect()
        f.getGlobalVisibleRect(r1); next.getGlobalVisibleRect(r2)
        return r2.centerX() >= r1.left
    }

    /** A screen that can go back to its top (picked again in the menu, or Home from another screen). */
    interface ToTop {
        fun toTop()
        companion object { const val EXTRA = "dingus_to_top" }
    }

    private fun go(p: Page) {
        // The screen you're on: back to its top. (Home also comes back at its top from elsewhere, below.)
        if (p == here) { close(restore = a !is ToTop); onNavigate?.invoke(p); (a as? ToTop)?.toTop(); return }
        close() // (the focus goes back where it was, for when you come back to this screen)
        onNavigate?.invoke(p)
        val i = when (p) {
            Page.HOME -> Intent(a, HomeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra(ToTop.EXTRA, true)
            Page.LIVE -> Intent(a, MainActivity::class.java).putExtra(MainActivity.CATEGORIES, true)
            Page.FILMS -> Intent(a, BrowseActivity::class.java).putExtra(BrowseActivity.KIND, Vod.MOVIES)
            Page.SHOWS -> Intent(a, BrowseActivity::class.java).putExtra(BrowseActivity.KIND, Vod.SERIES)
            Page.SEARCH -> Intent(a, SearchActivity::class.java)
            Page.SETTINGS -> Intent(a, SettingsActivity::class.java)
        }
        a.startActivity(i)
        // Home and Live TV stay underneath; the other screens make way, so BACK never walks through a pile of them.
        if (here != Page.HOME && here != Page.LIVE) a.finish()
        Ui.test("menu-go $p")
    }

    private fun switchPlaylist() {
        val all = Store.playlists
        Dialogs.builder(a).setTitle("Switch playlist")
            .setSingleChoiceItems(all.map { it.name }.toTypedArray(), all.indexOfFirst { it.id == Store.activeId }) { dlg, n ->
                dlg.dismiss()
                if (all[n].id == Store.activeId) return@setSingleChoiceItems
                close(restore = false)
                Playlists.switchTo(all[n].id)
                checking = false
                showDetails() // (asks the provider for the new playlist's subscription)
                // Start again from Home with the new playlist (which asks whether to update it).
                PlaylistUpdate.askAfterSwitch = true
                if (a is HomeActivity) a.recreate()
                else a.startActivity(Intent(a, HomeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP))
            }
            .show()
    }

    companion object {
        /** The VPN's location beside its indicator (seen once the menu's open). */
        fun showVpnPlace(indicator: TextView) {
            val place = (indicator.parent as? View)?.findViewById<TextView>(R.id.sideVpnPlace) ?: return
            val on = Vpn.status == Vpn.Status.ON
            place.text = if (on) Vpn.connectedTo?.label ?: "" else if (Vpn.status == Vpn.Status.CONNECTING) "Connecting…" else "VPN not connected"
            place.visibility = indicator.visibility
        }

        @Volatile private var checking = false

        /** The menu on a screen whose whole layout moves across for the strip. */
        fun attach(a: Activity, page: Page): SideMenu =
            SideMenu(a, page, a.findViewById<ViewGroup>(android.R.id.content).getChildAt(0))
    }
}
