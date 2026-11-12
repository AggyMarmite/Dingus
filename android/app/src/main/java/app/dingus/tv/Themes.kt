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
    private var themeVersion = -1

    override fun onCreate(savedInstanceState: Bundle?) {
        Themes.apply(this)
        themeVersion = Themes.version
        super.onCreate(savedInstanceState)
    }

    override fun onResume() {
        super.onResume()
        if (themeVersion != Themes.version) { recreate(); return }
        Reminders.attach(this)
    }

    override fun onPause() {
        Reminders.detach(this)
        super.onPause()
    }
}
