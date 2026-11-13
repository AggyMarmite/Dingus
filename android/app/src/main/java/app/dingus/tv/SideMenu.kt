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

    fun start() {
        val p = Store.playlist ?: return
        if (running) return
        running = true
        Background.scope.launch {
            try {
                Notice.working("Updating playlist & TV guide…")
                Repo.refreshChannels(p)
                try { Repo.refreshGuide(); Notice.done("Playlist & TV guide updated") }
                catch (e: Exception) { Notice.done("Playlist updated  ·  TV guide: ${Net.friendly(e)}", 6_000) }
                Vod.reset()
            } catch (e: Exception) {
                Notice.done("Couldn't update the playlist: ${Net.friendly(e)}", 6_000)
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
) {
    enum class Page { HOME, LIVE, FILMS, SHOWS, SEARCH, SETTINGS }

    private val root = a.findViewById<FrameLayout>(android.R.id.content)
    private val d = a.resources.displayMetrics.density
    private val strip = (60 * d).toInt()
    private val full = (232 * d).toInt()
    private val scrim = View(a).apply { setBackgroundColor(0xFF000000.toInt()); alpha = 0f; visibility = View.GONE }
    val panel: View = LayoutInflater.from(a).inflate(R.layout.side_menu, root, false)
    private val edge: View = panel.findViewById(R.id.sideEdge)
    private val fading = listOf<View>(panel.findViewById(R.id.sideName), panel.findViewById(R.id.sideHeader), panel.findViewById(R.id.status))
    private val rows: List<TextView> = listOf(R.id.navHome, R.id.navGuide, R.id.navFilms, R.id.navShows, R.id.navSearch, R.id.settings, R.id.navRefresh)
        .map { panel.findViewById<TextView>(it) }
    private val playlistRow: TextView = panel.findViewById(R.id.navPlaylist)
    private val accountRow: TextView = panel.findViewById(R.id.navAccount)
    private var fraction = 0f
    private var anim: ValueAnimator? = null
    private var returnTo: View? = null

    var isOpen = false; private set
    /** Called once the menu has closed by itself (RIGHT / BACK), for screens that put the focus somewhere special. */
    var onClosed: (() -> Unit)? = null
    /** Called when something in the menu is chosen (Live TV: back to the guide itself, categories closed). */
    var onNavigate: ((Page) -> Unit)? = null

    init {
        content.setPaddingRelative(content.paddingStart + strip, content.paddingTop, content.paddingEnd, content.paddingBottom)
        root.addView(scrim, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        root.addView(panel, FrameLayout.LayoutParams(full, ViewGroup.LayoutParams.MATCH_PARENT))
        panel.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> apply() }

        val size = (22 * d).toInt()
        val colour = Themes.color(a, R.attr.dText)
        for (t in rows) t.compoundDrawablesRelative[0]?.mutate()?.let { dr ->
            dr.setTint(colour); dr.setBounds(0, 0, size, size); t.setCompoundDrawablesRelative(dr, null, null, null)
        }
        rowFor(here).isActivated = true

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
    }

    private fun rowFor(p: Page): TextView = rows[p.ordinal]

    /** How far open (0 = the strip, 1 = open): the panel's shown width, the names, the dimming behind. */
    private fun apply() {
        val w = strip + ((full - strip) * fraction).toInt()
        val h = panel.height.takeIf { it > 0 } ?: a.resources.displayMetrics.heightPixels
        panel.clipBounds = Rect(0, 0, w, h)
        edge.translationX = (w - 1).toFloat()
        val labels = ((fraction - 0.25f) / 0.75f).coerceIn(0f, 1f)
        for (v in fading) v.alpha = labels
        for (t in rows) t.setTextColor(t.textColors.withAlpha((labels * 255).toInt()))
        scrim.alpha = 0.6f * fraction
        scrim.visibility = if (fraction > 0f) View.VISIBLE else View.GONE
    }

    private fun animateTo(target: Float, ms: Long, end: (() -> Unit)? = null) {
        anim?.cancel()
        if (Ui.lite) { fraction = target; apply(); end?.invoke(); return } // lite mode: open and shut at once
        anim = ValueAnimator.ofFloat(fraction, target).apply {
            duration = ms
            interpolator = DecelerateInterpolator()
            addUpdateListener { fraction = it.animatedValue as Float; apply() }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                var cancelled = false
                override fun onAnimationCancel(animation: android.animation.Animator) { cancelled = true }
                override fun onAnimationEnd(animation: android.animation.Animator) { if (!cancelled) end?.invoke() }
            })
            start()
        }
    }

    /** The playlist's name and the subscription; asks the provider again now and then (and after switching). */
    private fun showDetails() {
        val vod = if (Vod.available) View.VISIBLE else View.GONE
        rowFor(Page.FILMS).visibility = vod
        rowFor(Page.SHOWS).visibility = vod
        val several = Store.playlists.size > 1
        playlistRow.text = (Store.playlist?.name ?: "") + if (several) "  ▾" else ""
        playlistRow.tag = several
        val info = Account.saved()
        accountRow.text = info?.let { Account.short(it) } ?: ""
        val p = Store.playlist
        if (p != null && System.currentTimeMillis() - (info?.checkedAt ?: 0) > 86_400_000L && !checking) {
            checking = true
            Background.scope.launch {
                try { Account.refresh(p)?.let { if (Store.playlist?.id == p.id) accountRow.text = Account.short(it) } }
                catch (_: Exception) {}
                finally { checking = false }
            }
        }
    }

    fun open() {
        if (isOpen) return
        isOpen = true
        returnTo = a.currentFocus
        showDetails()
        for (t in rows) t.isFocusable = t.visibility == View.VISIBLE
        playlistRow.isFocusable = playlistRow.tag == true
        rowFor(here).requestFocus()
        animateTo(1f, 200)
        Ui.test("menu-open")
    }

    /** [restore]: put the focus back where it was before the menu opened. */
    fun close(restore: Boolean = true) {
        if (!isOpen) return
        isOpen = false
        val back = returnTo?.takeIf { restore && it.isAttachedToWindow && it.isShown }
        back?.requestFocus()
        for (t in rows) t.isFocusable = false
        playlistRow.isFocusable = false
        returnTo = null
        animateTo(0f, 170)
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
            // Up and down stay in the menu (never off into the screen behind it).
            if (e.keyCode == KeyEvent.KEYCODE_DPAD_UP || e.keyCode == KeyEvent.KEYCODE_DPAD_DOWN) {
                val next = a.currentFocus?.focusSearch(if (e.keyCode == KeyEvent.KEYCODE_DPAD_UP) View.FOCUS_UP else View.FOCUS_DOWN)
                if (next == null || !inside(next)) return true
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

    private fun go(p: Page) {
        if (p == here) { close(); onNavigate?.invoke(p); return }
        close() // (the focus goes back where it was, for when you come back to this screen)
        onNavigate?.invoke(p)
        val i = when (p) {
            Page.HOME -> Intent(a, HomeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
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
                // Start again from Home with the new playlist.
                if (a is HomeActivity) a.recreate()
                else a.startActivity(Intent(a, HomeActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP))
            }
            .show()
    }

    companion object {
        @Volatile private var checking = false

        /** The menu on a screen whose whole layout moves across for the strip. */
        fun attach(a: Activity, page: Page): SideMenu =
            SideMenu(a, page, a.findViewById<ViewGroup>(android.R.id.content).getChildAt(0))
    }
}
