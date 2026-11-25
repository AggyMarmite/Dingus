package app.dingus.tv

import android.content.Context
import android.graphics.Paint
import android.graphics.Typeface
import android.text.SpannableStringBuilder
import android.text.Spanned
import android.text.TextPaint
import android.text.style.MetricAffectingSpan
import android.widget.TextView

/**
 * Small outline icons beside Settings' sections and rows (Material Design Icons, Pictogrammers Free License – just
 * the ones used, in res/font/icons.ttf).
 */
object SettingsIcons {
    private var face: Typeface? = null
    private fun face(ctx: Context): Typeface? = face ?: try { androidx.core.content.res.ResourcesCompat.getFont(ctx, R.font.icons)?.also { face = it } } catch (e: Exception) { null }

    private val map = mapOf(
        "Playlists" to 0xF0411,
        "Backup servers" to 0xF048D,
        "Update now" to 0xF0450,
        "Update every" to 0xF06B0,
        "Full refresh" to 0xF1634,
        "Coming soon alerts" to 0xF009F,
        "Coming soon: don't count these categories" to 0xF0235,
        "Update TV guide now" to 0xF0503,
        "Source priority" to 0xF04BA,
        "Channel logo priority" to 0xF0976,
        "TV guide sources" to 0xF0339,
        "Keep TV guide for" to 0xF00F0,
        "Clear TV guide" to 0xF09E7,
        "Series reminders" to 0xF1A2A,
        "Remind me" to 0xF009C,
        "Switch for reminders" to 0xF04E1,
        "Channel numbers" to 0xF03A0,
        "Colour-coded programmes" to 0xF0E0C,
        "Edit channels" to 0xF0CB6,
        "Categories" to 0xF0832,
        "Merge duplicate channels" to 0xF0F5C,
        "Merge matching categories" to 0xF00F8,
        "Remove divider channels" to 0xF06F2,
        "Open Dingus on" to 0xF06A1,
        "Pause & rewind live TV" to 0xF045F,
        "Watch in the guide first" to 0xF0E57,
        "Stream format" to 0xF0E2C,
        "Subtitles automatically" to 0xF0A17,
        "Subtitle language" to 0xF05CA,
        "Play the next episode automatically" to 0xF0F27,
        "Match the TV's frame rate" to 0xF1591,
        "Match the TV's dynamic range" to 0xF0D7D,
        "Theme" to 0xF135C,
        "Text size" to 0xF027F,
        "Home rows" to 0xF11D8,
        "Title logos" to 0xF05F4,
        "Lite mode" to 0xF06D3,
        "VPN indicator" to 0xF0CC8,
        "VPN" to 0xF0499,
        "Quick Connect" to 0xF140C,
        "Location" to 0xF07D9,
        "IP address" to 0xF059F,
        "Speed test" to 0xF04C5,
        "More VPN settings" to 0xF08BB,
        "About Dingus" to 0xF02FD,
        "Check for updates" to 0xF0B7D,
        "Clear cache" to 0xF00E2,
        "Provider" to 0xF01D7,
        "Set up from your phone" to 0xF09D5,
        "Type them in with the remote" to 0xF097B,
        "Remove config files" to 0xF1037,
        "Connect when Dingus starts" to 0xF0425,
        "Reconnect by itself" to 0xF006A,
        "Kill switch" to 0xF099C,
        "Switch server if slow" to 0xF0F86,
        "Only Dingus uses this VPN" to 0xF02FD,
        "§Playlist" to 0xF0411,
        "§TV guide" to 0xF0503,
        "§Live TV" to 0xF07F4,
        "§Playback" to 0xF040D,
        "§Look" to 0xF0E0C,
        "§VPN" to 0xF0499,
        "§About" to 0xF02FD,
        "Connected" to 0xF0CC8,
        "Not connected" to 0xF0499,
        "Connecting" to 0xF11A3,    )

    /** The icon for a row's (or, with "§", a section's) name, ignoring any symbol in front ("⚡  Quick Connect"). */
    private fun glyph(name: String): String? {
        val clean = name.trimStart { !it.isLetterOrDigit() && it != '§' }.trim()
        val cp = map[clean] ?: map.entries.firstOrNull { clean.startsWith(it.key) }?.value ?: return null
        return String(Character.toChars(cp))
    }

    /** Puts the row's icon in [icon] (hidden when it has none). */
    fun row(icon: TextView, name: String) {
        val g = glyph(name)
        val f = face(icon.context)
        if (g == null || f == null) { icon.visibility = android.view.View.GONE; return }
        icon.typeface = f; icon.text = g; icon.visibility = android.view.View.VISIBLE
    }

    /** A section's name with its icon in front. */
    fun section(ctx: Context, name: String): CharSequence = inline(ctx, name, "§$name")

    /** A name with its icon in front, in the same line of text. */
    fun inline(ctx: Context, name: String, key: String = name): CharSequence {
        val g = glyph(key) ?: return name
        val f = face(ctx) ?: return name
        val sb = SpannableStringBuilder(g).append("   ").append(name)
        sb.setSpan(Face(f), 0, g.length, Spanned.SPAN_EXCLUSIVE_EXCLUSIVE)
        return sb
    }

    private class Face(val f: Typeface) : MetricAffectingSpan() {
        override fun updateDrawState(p: TextPaint) { p.typeface = f; p.textSize = p.textSize * 1.15f }
        override fun updateMeasureState(p: TextPaint) { p.typeface = f; p.textSize = p.textSize * 1.15f }
    }
}
