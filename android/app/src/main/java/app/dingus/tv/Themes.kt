package app.dingus.tv

import android.app.Activity
import android.content.Context
import android.os.Bundle
import android.util.TypedValue

/** Background and accent colours, chosen in Settings → Theme. */
object Themes {
    class Choice(val key: String, val name: String, val style: Int)

    val backgrounds = listOf(
        Choice("midnight", "Midnight", R.style.Bg_Midnight),
        Choice("slate", "Slate (lighter grey)", R.style.Bg_Slate),
        Choice("black", "Black (OLED)", R.style.Bg_Black),
        Choice("plum", "Plum", R.style.Bg_Plum),
        Choice("forest", "Forest", R.style.Bg_Forest),
    )
    val accents = listOf(
        Choice("blue", "Blue", R.style.Accent_Blue),
        Choice("purple", "Purple", R.style.Accent_Purple),
        Choice("teal", "Teal", R.style.Accent_Teal),
        Choice("green", "Green", R.style.Accent_Green),
        Choice("orange", "Orange", R.style.Accent_Orange),
        Choice("pink", "Pink", R.style.Accent_Pink),
        Choice("red", "Red", R.style.Accent_Red),
        Choice("gold", "Gold", R.style.Accent_Gold),
    )

    fun background() = backgrounds.firstOrNull { it.key == Store.background } ?: backgrounds[0]
    fun accent() = accents.firstOrNull { it.key == Store.accent } ?: accents[0]

    /** Bumped on every change, so open screens know to redraw. */
    @Volatile var version = 0

    fun apply(a: Activity) {
        a.theme.applyStyle(background().style, true)
        a.theme.applyStyle(accent().style, true)
    }

    /** A theme colour (R.attr.dAccent etc.) for code that draws by hand. */
    fun color(ctx: Context, attr: Int): Int {
        val v = TypedValue()
        ctx.theme.resolveAttribute(attr, v, true)
        return v.data
    }
}

/** Every screen: applies the chosen theme, and redraws itself if the theme changed while it was open. */
abstract class BaseActivity : Activity() {
    // Hold BACK anywhere: straight to the last live channel, full screen (a normal press works as always).
    // Screens with their own key handling call backHold() first.
    private var backHeld = false
    private var lastKey: android.view.KeyEvent? = null
    private var lastKeyResult = false

    /** Self-test builds: every move of the highlight is logged ("focus navFilms"), so the test can go straight to a
     *  button or row by name instead of counting presses. */
    override fun onPostCreate(savedInstanceState: android.os.Bundle?) {
        super.onPostCreate(savedInstanceState)
        // Buttons, pills and chips lift a little when highlighted, everywhere (cards lift themselves).
        window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { o, n -> Motion.buttons(o, n) }
        if (BuildConfig.DEBUG) window.decorView.viewTreeObserver.addOnGlobalFocusChangeListener { _, n ->
            val id = n?.takeIf { it.id != android.view.View.NO_ID }?.let { try { resources.getResourceEntryName(it.id) } catch (_: Exception) { null } }
            if (id != null) Ui.test("focus $id")
        }
    }

    protected fun backHold(event: android.view.KeyEvent): Boolean {
        if (event === lastKey) return lastKeyResult
        lastKey = event
        Motion.key(event)
        lastKeyResult = run {
            if (event.keyCode != android.view.KeyEvent.KEYCODE_BACK) return@run false
            val downAct = event.action == android.view.KeyEvent.ACTION_DOWN
            if (downAct && event.repeatCount == 0) { backHeld = false; return@run false }
            if (downAct && (event.isLongPress || event.repeatCount >= 8)) { if (!backHeld) { backHeld = true; backToLive() }; return@run true }
            if (downAct) return@run true // (still held: waiting to see whether it's a long press)
            if (event.action == android.view.KeyEvent.ACTION_UP && backHeld) { backHeld = false; return@run true }
            false
        }
        return lastKeyResult
    }

    override fun dispatchKeyEvent(event: android.view.KeyEvent): Boolean = backHold(event) || super.dispatchKeyEvent(event)

    private fun backToLive() {
        val id = Store.lastChannel ?: return
        Ui.test("back-held live $id")
        startActivity(android.content.Intent(this, MainActivity::class.java).putExtra(MainActivity.PLAY, id).putExtra(MainActivity.PLAY_CATEGORY, if (Store.isFavourite(id)) Repo.FAV else Repo.ALL)
            .addFlags(android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP or android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }

    private var themeVersion = -1

    /** Sized for a TV across the room: the usual Android TV size, with text a little smaller (less "phone"). */
    override fun attachBaseContext(base: android.content.Context) {
        val c = android.content.res.Configuration(base.resources.configuration)
        c.fontScale = base.resources.configuration.fontScale * 0.85f * Store.textScale // (Settings → Look → Text size)
        super.attachBaseContext(base.createConfigurationContext(c))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        Themes.apply(this)
        themeVersion = Themes.version
        super.onCreate(savedInstanceState)
    }

    override fun onResume() {
        super.onResume()
        if (themeVersion != Themes.version) { recreate(); return }
        Reminders.attach(this)
        Notice.attach(this)
        Vpn.onChanged(vpnBadge)
        showVpnBadge()
    }

    /** Screens with a "vpnBadge" view show the VPN's state there. */
    private val vpnBadge: () -> Unit = { showVpnBadge() }
    private fun showVpnBadge() {
        findViewById<android.widget.TextView>(R.id.vpnBadge)?.let { VpnIndicator.bind(it) }
        findViewById<android.widget.TextView>(R.id.sideVpn)?.let { VpnIndicator.bind(it); SideMenu.showVpnPlace(it) }
    }

    override fun onPause() {
        Vpn.removeListener(vpnBadge)
        Reminders.detach(this)
        Notice.detach(this)
        super.onPause()
    }
}
